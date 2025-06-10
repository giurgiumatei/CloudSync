using CloudSync.Core.Configuration;
using CloudSync.Core.DTOs;
using CloudSync.Core.Services;
using CloudSync.Core.Services.Interfaces;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text.Json;

namespace CloudSync.KafkaAzureConsumer.Services;

public class AzureKafkaConsumerService : IKafkaConsumerService, IDisposable
{
    private readonly IConsumer<string, string> _consumer;
    private readonly IKafkaProducerService _kafkaProducerService;
    private readonly KafkaConfiguration _kafkaConfig;
    private readonly AzureEndpointConfiguration _azureConfig;
    private readonly ErrorHandlingConfiguration _errorConfig;
    private readonly ILogger<AzureKafkaConsumerService> _logger;
    private readonly HttpClient _httpClient;
    private readonly RetryService _retryService;
    private bool _isConsuming = false;
    private bool _disposed = false;

    public AzureKafkaConsumerService(
        IOptions<KafkaConfiguration> kafkaConfig,
        IOptions<AzureEndpointConfiguration> azureConfig,
        IOptions<ErrorHandlingConfiguration> errorConfig,
        IKafkaProducerService kafkaProducerService,
        ILogger<AzureKafkaConsumerService> logger,
        HttpClient httpClient)
    {
        _kafkaConfig = kafkaConfig.Value;
        _azureConfig = azureConfig.Value;
        _errorConfig = errorConfig.Value;
        _kafkaProducerService = kafkaProducerService;
        _logger = logger;
        _httpClient = httpClient;
        _retryService = new RetryService(_errorConfig.TransientErrors, logger);

        var config = new ConsumerConfig
        {
            BootstrapServers = _kafkaConfig.BootstrapServers,
            GroupId = _kafkaConfig.Consumer.GroupId,
            ClientId = _kafkaConfig.Consumer.ClientId,
            AutoOffsetReset = ParseAutoOffsetReset(_kafkaConfig.Consumer.AutoOffsetReset),
            EnableAutoCommit = _kafkaConfig.Consumer.EnableAutoCommit,
            MaxPollIntervalMs = _kafkaConfig.Consumer.MaxPollIntervalMs,
            SessionTimeoutMs = _kafkaConfig.Consumer.SessionTimeoutMs,
            HeartbeatIntervalMs = _kafkaConfig.Consumer.HeartbeatIntervalMs,
            FetchMinBytes = _kafkaConfig.Consumer.FetchMinBytes,
            FetchMaxWaitMs = _kafkaConfig.Consumer.FetchMaxWaitMs,
            MaxPartitionFetchBytes = _kafkaConfig.Consumer.MaxPartitionFetchBytes,
            CheckCrcs = _kafkaConfig.Consumer.CheckCrcs,
            EnablePartitionEof = _kafkaConfig.Consumer.EnablePartitionEof
        };

        _consumer = new ConsumerBuilder<string, string>(config)
            .SetErrorHandler((_, e) => _logger.LogError("Azure Consumer error: {Error}", e.Reason))
            .SetPartitionsAssignedHandler((_, partitions) => 
            {
                _logger.LogInformation("Azure Consumer assigned partitions: [{Partitions}]", 
                    string.Join(", ", partitions.Select(p => $"{p.Topic}[{p.Partition}]")));
            })
            .SetPartitionsRevokedHandler((_, partitions) => 
            {
                _logger.LogInformation("Azure Consumer revoked partitions: [{Partitions}]", 
                    string.Join(", ", partitions.Select(p => $"{p.Topic}[{p.Partition}]")));
            })
            .Build();

        _httpClient.Timeout = TimeSpan.FromSeconds(_azureConfig.TimeoutSeconds);
        _logger.LogInformation("Azure Kafka Consumer initialized for group: {GroupId}", _kafkaConfig.Consumer.GroupId);
    }

    public async Task StartConsumingAsync(CancellationToken cancellationToken)
    {
        _consumer.Subscribe(_kafkaConfig.Topics.DataTopic);
        _isConsuming = true;

        _logger.LogInformation("Azure Consumer started consuming from topic: {Topic}", _kafkaConfig.Topics.DataTopic);

        try
        {
            while (!cancellationToken.IsCancellationRequested && _isConsuming)
            {
                try
                {
                    var consumeResult = _consumer.Consume(cancellationToken);
                    
                    if (consumeResult?.Message?.Value != null)
                    {
                        await ProcessConsumedMessage(consumeResult);
                    }
                }
                catch (ConsumeException ex)
                {
                    _logger.LogError(ex, "Azure Consumer error: {Error}", ex.Error.Reason);
                    
                    if (ex.Error.IsFatal)
                    {
                        _logger.LogCritical("Fatal Azure Consumer error, stopping consumption");
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Azure Consumer operation cancelled");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error in Azure Consumer consumption loop");
                    await Task.Delay(5000, cancellationToken); // Brief pause before retrying
                }
            }
        }
        finally
        {
            _consumer.Close();
            _logger.LogInformation("Azure Consumer stopped");
        }
    }

    public Task StopConsumingAsync()
    {
        _isConsuming = false;
        return Task.CompletedTask;
    }

    private async Task ProcessConsumedMessage(ConsumeResult<string, string> consumeResult)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        DataSyncMessage? message = null;
        
        try
        {
            message = JsonSerializer.Deserialize<DataSyncMessage>(consumeResult.Message.Value);
            if (message == null)
            {
                _logger.LogWarning("Failed to deserialize message from partition {Partition} offset {Offset}", 
                    consumeResult.Partition.Value, consumeResult.Offset.Value);
                _consumer.Commit(consumeResult); // Commit to skip corrupted message
                return;
            }

            _logger.LogDebug("Azure Consumer processing message {MessageId} from partition {Partition} offset {Offset}", 
                message.Id, consumeResult.Partition.Value, consumeResult.Offset.Value);

            var success = await ProcessMessageAsync(message);

            if (success)
            {
                _consumer.Commit(consumeResult);
                _logger.LogDebug("Azure Consumer successfully processed and committed message {MessageId} in {ElapsedMs}ms", 
                    message.Id, sw.ElapsedMilliseconds);
            }
            else
            {
                _logger.LogWarning("Azure Consumer failed to process message {MessageId}, will NOT commit offset for retry", message.Id);
                // Don't commit - let the message be retried on next poll
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Azure Consumer failed to deserialize message from partition {Partition} offset {Offset}", 
                consumeResult.Partition.Value, consumeResult.Offset.Value);
            
            // For JSON errors, commit to avoid infinite retries
            _consumer.Commit(consumeResult);
            
            if (message != null)
            {
                var processingError = _retryService.CreateProcessingError(ex, message.Id, 0, "Azure-Consumer-Deserialization");
                await _kafkaProducerService.PublishToDeadLetterQueueAsync(message, "JSON deserialization failed", processingError);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Azure Consumer unexpected error processing message from partition {Partition} offset {Offset}", 
                consumeResult.Partition.Value, consumeResult.Offset.Value);
            
            if (message != null)
            {
                var processingError = _retryService.CreateProcessingError(ex, message.Id, 0, "Azure-Consumer-Processing");
                await _kafkaProducerService.PublishToDeadLetterQueueAsync(message, "Unexpected processing error", processingError);
            }
        }
    }

    public async Task<bool> ProcessMessageAsync(DataSyncMessage message)
    {
        try
        {
            var success = await _retryService.ExecuteWithRetryAsync(
                async () => await SaveToAzureEndpoint(message.Data),
                ErrorType.Transient,
                "SaveToAzureEndpoint",
                message.CorrelationId);
            
            if (success)
            {
                _logger.LogInformation("Azure Consumer successfully saved message {MessageId} to Azure endpoint", message.Id);
                return true;
            }
            
            return false;
        }
        catch (Exception ex)
        {
            var errorType = ErrorClassifier.ClassifyException(ex);
            var processingError = _retryService.CreateProcessingError(
                ex, 
                message.Id, 
                message.RetryCount, 
                "Azure-Consumer",
                new Dictionary<string, string>
                {
                    { "Endpoint", _azureConfig.BaseUrl },
                    { "ConsumerGroup", _kafkaConfig.Consumer.GroupId },
                    { "MessageTimestamp", message.Timestamp.ToString("O") }
                });

            _logger.LogError(ex, "Azure Consumer failed to process message {MessageId} after retries, sending to DLQ. Error type: {ErrorType}", 
                message.Id, errorType);

            await _kafkaProducerService.PublishToDeadLetterQueueAsync(
                message, 
                $"Failed to save to Azure endpoint after {_errorConfig.TransientErrors.MaxRetries} retries: {ex.Message}", 
                processingError);
            
            return false;
        }
    }

    private async Task<bool> SaveToAzureEndpoint(string jsonData)
    {
        try
        {
            _logger.LogDebug("Azure Consumer: Saving data to Azure endpoint: {BaseUrl}", _azureConfig.BaseUrl);

            // Simulate different types of failures for testing
            await SimulateFailuresForTesting();

            // In production, this would be:
            // var content = new StringContent(jsonData, Encoding.UTF8, "application/json");
            // var response = await _httpClient.PostAsync($"{_azureConfig.BaseUrl}/api/sync", content);
            // 
            // if (!response.IsSuccessStatusCode)
            // {
            //     var errorType = ErrorClassifier.ClassifyHttpStatusCode(response.StatusCode);
            //     throw new HttpRequestException($"Azure endpoint returned {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            // }
            // 
            // return response.IsSuccessStatusCode;

            await Task.Delay(15); // Simulate slightly different processing time than AWS
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Azure Consumer error saving to Azure endpoint");
            throw; // Re-throw for retry logic
        }
    }

    private async Task SimulateFailuresForTesting()
    {
        // This method simulates different types of failures for testing purposes
        // Remove this in production
        var random = new Random();
        var failureType = random.Next(1, 100);

        if (failureType <= 8) // 8% chance of timeout (slightly different from AWS)
        {
            await Task.Delay(TimeSpan.FromSeconds(_azureConfig.TimeoutSeconds + 1));
            throw new TimeoutException("Azure endpoint timeout");
        }
        else if (failureType <= 12) // 4% chance of network error
        {
            throw new HttpRequestException("Azure network connection failed");
        }
        else if (failureType <= 17) // 5% chance of server error
        {
            throw new HttpRequestException("Azure internal server error", null, HttpStatusCode.InternalServerError);
        }
        else if (failureType <= 19) // 2% chance of rate limiting
        {
            throw new HttpRequestException("Rate limited", null, HttpStatusCode.TooManyRequests);
        }
        else if (failureType <= 20) // 1% chance of auth error (non-retryable)
        {
            throw new HttpRequestException("Azure unauthorized", null, HttpStatusCode.Unauthorized);
        }
        // 80% success rate (slightly higher than AWS for testing variety)
    }

    private static AutoOffsetReset ParseAutoOffsetReset(string autoOffsetReset)
    {
        return autoOffsetReset.ToLowerInvariant() switch
        {
            "earliest" => AutoOffsetReset.Earliest,
            "latest" => AutoOffsetReset.Latest,
            "error" => AutoOffsetReset.Error,
            _ => AutoOffsetReset.Earliest
        };
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _isConsuming = false;
            _consumer?.Close();
            _consumer?.Dispose();
            _disposed = true;
            _logger.LogInformation("Azure Kafka Consumer disposed");
        }
    }
} 