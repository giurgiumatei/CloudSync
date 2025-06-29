using CloudSync.Core.Configuration;
using CloudSync.Core.DTOs;
using CloudSync.Core.Services.Interfaces;
using CloudSync.Data.Contexts;
using CloudSync.Data.Entities;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace CloudSync.KafkaAzureConsumer.Services;

public interface IAzureKafkaConsumerService
{
    Task StartConsumingAsync(CancellationToken cancellationToken);
    Task StopConsumingAsync();
}

public class AzureKafkaConsumerService : IAzureKafkaConsumerService, IKafkaConsumerService, IDisposable
{
    private readonly ILogger<AzureKafkaConsumerService> _logger;
    private readonly KafkaConfiguration _kafkaConfig;
    private readonly AzureEndpointConfiguration _azureConfig;
    private readonly IRetryService _retryService;
    private readonly IErrorClassifier _errorClassifier;
    private readonly IIdempotencyService _idempotencyService;
    private readonly AzureDbContext _azureDbContext;
    private IConsumer<string, string>? _consumer;
    private readonly string _consumerGroupId = "azure-sync-group";
    private bool _isConsuming = false;

    public AzureKafkaConsumerService(
        ILogger<AzureKafkaConsumerService> logger,
        IOptions<KafkaConfiguration> kafkaConfig,
        IOptions<AzureEndpointConfiguration> azureConfig,
        IRetryService retryService,
        IErrorClassifier errorClassifier,
        IIdempotencyService idempotencyService,
        AzureDbContext azureDbContext)
    {
        _logger = logger;
        _kafkaConfig = kafkaConfig.Value;
        _azureConfig = azureConfig.Value;
        _retryService = retryService;
        _errorClassifier = errorClassifier;
        _idempotencyService = idempotencyService;
        _azureDbContext = azureDbContext;
    }

    public async Task StartConsumingAsync(CancellationToken cancellationToken)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _kafkaConfig.BootstrapServers,
            GroupId = _kafkaConfig.Consumer.GroupId,
            AutoOffsetReset = Enum.Parse<AutoOffsetReset>(_kafkaConfig.Consumer.AutoOffsetReset),
            EnableAutoCommit = _kafkaConfig.Consumer.EnableAutoCommit,
            SessionTimeoutMs = _kafkaConfig.Consumer.SessionTimeoutMs,
            MaxPollIntervalMs = _kafkaConfig.Consumer.MaxPollIntervalMs,
            EnablePartitionEof = _kafkaConfig.Consumer.EnablePartitionEof
        };

        _consumer = new ConsumerBuilder<string, string>(config)
            .SetErrorHandler((_, e) => _logger.LogError("Consumer error: {Error}", e.Reason))
            .SetLogHandler((_, message) => _logger.LogDebug("Consumer log: {Message}", message.Message))
            .Build();

        _consumer.Subscribe(_kafkaConfig.Topics.DataTopic);
        _isConsuming = true;

        _logger.LogInformation("Azure Consumer started with idempotency. Group: {GroupId}, Topic: {Topic}", 
            _consumerGroupId, _kafkaConfig.Topics.DataTopic);

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
                    _logger.LogInformation("Azure Consumer operation cancelled");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Critical error in Azure consumer loop");
            throw;
        }
    }

    private async Task ProcessMessageWithIdempotency(ConsumeResult<string, string> consumeResult, CancellationToken cancellationToken)
    {
        var messageId = consumeResult.Message.Key ?? Guid.NewGuid().ToString();
        var messageProcessingId = $"azure-{messageId}-{consumeResult.Offset}";
        
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
                    operation: () => SaveToAzureEndpoint(dataMessage),
                    operationName: "SaveToAzureEndpoint",
                    correlationId: messageProcessingId,
                    errorType: ErrorType.Transient
                );

                processingResult = true;
                var processingDuration = DateTime.UtcNow - processingStartTime;
                
                _logger.LogInformation("Successfully processed Azure message {MessageId} in {Duration}ms", 
                    messageProcessingId, processingDuration.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                processingResult = false;
                var errorType = _errorClassifier.ClassifyError(ex);
                var processingDuration = DateTime.UtcNow - processingStartTime;
                
                _logger.LogError(ex, "Failed to process Azure message {MessageId} after {Duration}ms. Error type: {ErrorType}", 
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

    private async Task SaveToAzureEndpoint(DataSyncMessage message)
    {
        // Simulate different types of failures for testing
        // await SimulateProcessingConditions(); // DISABLED: Simulated errors

        // Create a new data entity for the Azure database
        var dataEntity = new DataEntity
        {
            // Generate a new integer ID instead of parsing the UUID string
            Id = 0, // Let the database auto-generate the ID
            Data = message.Data,
            CreatedAt = DateTime.UtcNow
        };

        try
        {
            // Add the entity to the context
            _azureDbContext.DataEntities.Add(dataEntity);
            
            // Save changes to the database
            await _azureDbContext.SaveChangesAsync();
            
            _logger.LogDebug("Successfully saved data to Azure database for message {MessageId}", message.Id);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Database error saving to Azure database for message {MessageId}", message.Id);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error saving to Azure database for message {MessageId}", message.Id);
            throw;
        }
    }

    private async Task SimulateProcessingConditions()
    {
        // For testing different error scenarios with slightly different patterns than AWS
        var random = new Random();
        var scenario = random.Next(100);

        if (scenario < 3) // 3% timeout (slightly higher than AWS)
        {
            _logger.LogDebug("Simulating Azure timeout condition");
            await Task.Delay(35000); // Longer than HttpClient timeout
        }
        else if (scenario < 5) // 2% network error
        {
            _logger.LogDebug("Simulating Azure network error");
            throw new HttpRequestException("Simulated Azure network connectivity issue");
        }
        else if (scenario < 7) // 2% rate limiting
        {
            _logger.LogDebug("Simulating Azure rate limiting");
            throw new HttpRequestException("Rate limited by Azure API");
        }
        else if (scenario < 9) // 2% authentication error
        {
            _logger.LogDebug("Simulating Azure authentication error");
            throw new UnauthorizedAccessException("Simulated Azure authentication failure");
        }
        // 91% success - slightly different from AWS for testing variety
    }

    public async Task StopConsumingAsync()
    {
        _logger.LogInformation("Stopping Azure consumer...");
        _isConsuming = false;
        
        try
        {
            _consumer?.Close();
            await Task.Delay(1000); // Allow graceful shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping Azure consumer");
        }
    }

    public async Task<bool> ProcessMessageAsync(DataSyncMessage message)
    {
        try
        {
            await SaveToAzureEndpoint(message);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process message {MessageId}", message.Id);
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            _consumer?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disposing Azure consumer service");
        }
    }
} 