using CloudSync.Core.Configuration;
using CloudSync.Core.DTOs;
using CloudSync.Core.Services.Interfaces;
using CloudSync.KafkaAwsConsumer.Configuration;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace CloudSync.KafkaAwsConsumer.Services;

public interface IAwsKafkaConsumerService
{
    Task StartConsumingAsync(CancellationToken cancellationToken);
    Task StopConsumingAsync();
}

public class AwsKafkaConsumerService : IAwsKafkaConsumerService, IDisposable
{
    private readonly ILogger<AwsKafkaConsumerService> _logger;
    private readonly KafkaConfiguration _kafkaConfig;
    private readonly AwsEndpointConfiguration _awsConfig;
    private readonly IRetryService _retryService;
    private readonly IErrorClassifier _errorClassifier;
    private readonly IIdempotencyService _idempotencyService;
    private readonly HttpClient _httpClient;
    private IConsumer<string, string>? _consumer;
    private readonly string _consumerGroupId = "aws-sync-group";
    private bool _isConsuming = false;

    public AwsKafkaConsumerService(
        ILogger<AwsKafkaConsumerService> logger,
        IOptions<KafkaConfiguration> kafkaConfig,
        IOptions<AwsEndpointConfiguration> awsConfig,
        IRetryService retryService,
        IErrorClassifier errorClassifier,
        IIdempotencyService idempotencyService,
        HttpClient httpClient)
    {
        _logger = logger;
        _kafkaConfig = kafkaConfig.Value;
        _awsConfig = awsConfig.Value;
        _retryService = retryService;
        _errorClassifier = errorClassifier;
        _idempotencyService = idempotencyService;
        _httpClient = httpClient;
        
        ConfigureHttpClient();
    }

    private void ConfigureHttpClient()
    {
        _httpClient.BaseAddress = new Uri(_awsConfig.BaseUrl);
        _httpClient.Timeout = TimeSpan.FromSeconds(_awsConfig.TimeoutSeconds);
        
        if (!string.IsNullOrEmpty(_awsConfig.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Add("X-API-Key", _awsConfig.ApiKey);
        }
    }

    public async Task StartConsumingAsync(CancellationToken cancellationToken)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _kafkaConfig.Consumer.BootstrapServers,
            GroupId = _kafkaConfig.Consumer.GroupId,
            AutoOffsetReset = Enum.Parse<AutoOffsetReset>(_kafkaConfig.Consumer.AutoOffsetReset),
            EnableAutoCommit = _kafkaConfig.Consumer.EnableAutoCommit,
            SessionTimeoutMs = _kafkaConfig.Consumer.SessionTimeoutMs,
            MaxPollIntervalMs = _kafkaConfig.Consumer.MaxPollIntervalMs,
            EnablePartitionEof = _kafkaConfig.Consumer.EnablePartitionEof,
            AllowAutoCreateTopics = _kafkaConfig.Consumer.AllowAutoCreateTopics,
            IsolationLevel = Enum.Parse<IsolationLevel>(_kafkaConfig.Consumer.IsolationLevel)
        };

        _consumer = new ConsumerBuilder<string, string>(config)
            .SetErrorHandler((_, e) => _logger.LogError("Consumer error: {Error}", e.Reason))
            .SetLogHandler((_, message) => _logger.LogDebug("Consumer log: {Message}", message.Message))
            .Build();

        _consumer.Subscribe(_kafkaConfig.TopicName);
        _isConsuming = true;

        _logger.LogInformation("AWS Consumer started with idempotency. Group: {GroupId}, Topic: {Topic}", 
            _consumerGroupId, _kafkaConfig.TopicName);

        await ConsumeLoop(cancellationToken);
    }

    private async Task ConsumeLoop(CancellationToken cancellationToken)
    {
        try
        {
            while (_isConsuming && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var consumeResult = _consumer!.Consume(TimeSpan.FromMilliseconds(1000));
                    
                    if (consumeResult?.Message != null)
                    {
                        await ProcessMessageWithIdempotency(consumeResult, cancellationToken);
                    }
                }
                catch (ConsumeException ex)
                {
                    _logger.LogError(ex, "Error consuming message from Kafka");
                    await Task.Delay(1000, cancellationToken); // Brief pause on consume errors
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("AWS Consumer operation cancelled");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Critical error in AWS consumer loop");
            throw;
        }
    }

    private async Task ProcessMessageWithIdempotency(ConsumeResult<string, string> consumeResult, CancellationToken cancellationToken)
    {
        var messageId = consumeResult.Message.Key ?? Guid.NewGuid().ToString();
        var messageProcessingId = $"aws-{messageId}-{consumeResult.Offset}";
        
        var processingStartTime = DateTime.UtcNow;
        var messageSize = consumeResult.Message.Value?.Length ?? 0;
        
        _logger.LogDebug("Processing message {MessageId} from partition {Partition}, offset {Offset}, size: {Size} bytes",
            messageProcessingId, consumeResult.Partition.Value, consumeResult.Offset.Value, messageSize);

        try
        {
            // Check if message was already processed (idempotency check)
            var alreadyProcessed = await _idempotencyService.IsMessageProcessedAsync(messageProcessingId, _consumerGroupId);
            if (alreadyProcessed)
            {
                _logger.LogInformation("Duplicate message detected and skipped: {MessageId}", messageProcessingId);
                
                // Still commit the offset since we've "processed" this duplicate
                CommitOffset(consumeResult);
                return;
            }

            // Try to acquire processing lock
            var lockAcquired = await _idempotencyService.TryAcquireProcessingLockAsync(messageProcessingId, _consumerGroupId, 30000);
            if (!lockAcquired)
            {
                _logger.LogWarning("Failed to acquire processing lock for message {MessageId}, skipping", messageProcessingId);
                return; // Don't commit offset, let another consumer handle it
            }

            bool processingResult = false;
            try
            {
                // Parse and process the message
                var dataMessage = JsonSerializer.Deserialize<DataSyncMessage>(consumeResult.Message.Value);
                if (dataMessage == null)
                {
                    throw new InvalidOperationException("Failed to deserialize message");
                }

                // Process the message with retry logic
                await _retryService.ExecuteWithRetryAsync(
                    operation: () => SaveToAwsEndpoint(dataMessage),
                    operationName: "SaveToAwsEndpoint",
                    correlationId: messageProcessingId
                );

                processingResult = true;
                var processingDuration = DateTime.UtcNow - processingStartTime;
                
                _logger.LogInformation("Successfully processed AWS message {MessageId} in {Duration}ms", 
                    messageProcessingId, processingDuration.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                processingResult = false;
                var errorType = _errorClassifier.ClassifyError(ex);
                var processingDuration = DateTime.UtcNow - processingStartTime;
                
                _logger.LogError(ex, "Failed to process AWS message {MessageId} after {Duration}ms. Error type: {ErrorType}", 
                    messageProcessingId, processingDuration.TotalMilliseconds, errorType);
                
                // Don't commit offset on processing failure - let Kafka retry
                return;
            }
            finally
            {
                // Release processing lock
                await _idempotencyService.ReleaseProcessingLockAsync(messageProcessingId, _consumerGroupId);
            }

            // Mark message as processed for idempotency
            await _idempotencyService.MarkMessageProcessedAsync(messageProcessingId, _consumerGroupId, processingResult);

            // Commit offset only after successful processing and idempotency marking
            CommitOffset(consumeResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Critical error processing message {MessageId}", messageProcessingId);
            
            // Release lock on critical error
            try
            {
                await _idempotencyService.ReleaseProcessingLockAsync(messageProcessingId, _consumerGroupId);
            }
            catch (Exception lockEx)
            {
                _logger.LogError(lockEx, "Failed to release processing lock for message {MessageId}", messageProcessingId);
            }
        }
    }

    private void CommitOffset(ConsumeResult<string, string> consumeResult)
    {
        try
        {
            _consumer?.Commit(consumeResult);
            _logger.LogTrace("Committed offset {Offset} for partition {Partition}", 
                consumeResult.Offset.Value, consumeResult.Partition.Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to commit offset {Offset} for partition {Partition}", 
                consumeResult.Offset.Value, consumeResult.Partition.Value);
        }
    }

    private async Task SaveToAwsEndpoint(DataSyncMessage message)
    {
        // Simulate different types of failures for testing
        await SimulateProcessingConditions();

        var jsonPayload = JsonSerializer.Serialize(new
        {
            Id = message.Id,
            Data = message.Data,
            Timestamp = message.Timestamp,
            Source = "AWS-Consumer",
            ProcessedAt = DateTime.UtcNow
        });

        var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");
        
        try
        {
            var response = await _httpClient.PostAsync("/api/sync/aws", content);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException($"AWS endpoint returned {response.StatusCode}: {errorContent}");
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            _logger.LogDebug("AWS endpoint response: {Response}", responseContent);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error calling AWS endpoint for message {MessageId}", message.Id);
            throw;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "Timeout calling AWS endpoint for message {MessageId}", message.Id);
            throw new TimeoutException($"AWS endpoint timeout for message {message.Id}", ex);
        }
    }

    private async Task SimulateProcessingConditions()
    {
        // For testing different error scenarios
        var random = new Random();
        var scenario = random.Next(100);

        if (scenario < 2) // 2% timeout
        {
            _logger.LogDebug("Simulating timeout condition");
            await Task.Delay(35000); // Longer than HttpClient timeout
        }
        else if (scenario < 4) // 2% network error
        {
            _logger.LogDebug("Simulating network error");
            throw new HttpRequestException("Simulated network connectivity issue");
        }
        else if (scenario < 6) // 2% authentication error
        {
            _logger.LogDebug("Simulating authentication error");
            throw new UnauthorizedAccessException("Simulated authentication failure");
        }
        // 94% success - no simulation needed
    }

    public async Task StopConsumingAsync()
    {
        _logger.LogInformation("Stopping AWS consumer...");
        _isConsuming = false;
        
        try
        {
            _consumer?.Close();
            await Task.Delay(1000); // Allow graceful shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping AWS consumer");
        }
    }

    public void Dispose()
    {
        try
        {
            _consumer?.Dispose();
            _httpClient?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disposing AWS consumer service");
        }
    }
} 