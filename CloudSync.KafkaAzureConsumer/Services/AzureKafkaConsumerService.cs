using CloudSync.Core.Configuration;
using CloudSync.Core.DTOs;
using CloudSync.Core.Services.Interfaces;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace CloudSync.KafkaAzureConsumer.Services;

public class AzureKafkaConsumerService : IKafkaConsumerService, IDisposable
{
    private readonly IConsumer<string, string> _consumer;
    private readonly IKafkaProducerService _kafkaProducerService;
    private readonly KafkaConfiguration _kafkaConfig;
    private readonly AzureEndpointConfiguration _azureConfig;
    private readonly ILogger<AzureKafkaConsumerService> _logger;
    private readonly HttpClient _httpClient;
    private bool _isConsuming = false;
    private bool _disposed = false;

    public AzureKafkaConsumerService(
        IOptions<KafkaConfiguration> kafkaConfig,
        IOptions<AzureEndpointConfiguration> azureConfig,
        IKafkaProducerService kafkaProducerService,
        ILogger<AzureKafkaConsumerService> logger,
        HttpClient httpClient)
    {
        _kafkaConfig = kafkaConfig.Value;
        _azureConfig = azureConfig.Value;
        _kafkaProducerService = kafkaProducerService;
        _logger = logger;
        _httpClient = httpClient;

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
                }
                catch (OperationCanceledException)
                {
                    break;
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
        try
        {
            var message = JsonSerializer.Deserialize<DataSyncMessage>(consumeResult.Message.Value);
            if (message == null) return;

            _logger.LogInformation("Azure Consumer processing message {MessageId} from partition {Partition}", 
                message.Id, consumeResult.Partition.Value);

            var success = await ProcessMessageAsync(message);

            if (success)
            {
                _consumer.Commit(consumeResult);
                _logger.LogInformation("Azure Consumer successfully processed message {MessageId}", message.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Azure Consumer error processing message");
        }
    }

    public async Task<bool> ProcessMessageAsync(DataSyncMessage message)
    {
        try
        {
            var success = await SaveToAzureEndpoint(message.Data);
            
            if (!success)
            {
                await _kafkaProducerService.PublishToDeadLetterQueueAsync(message, "Failed to save to Azure endpoint");
            }
            
            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Azure Consumer exception processing message {MessageId}", message.Id);
            await _kafkaProducerService.PublishToDeadLetterQueueAsync(message, ex.Message);
            return false;
        }
    }

    private async Task<bool> SaveToAzureEndpoint(string jsonData)
    {
        try
        {
            _logger.LogInformation("Azure Consumer: Saving data to Azure endpoint: {BaseUrl}", _azureConfig.BaseUrl);
            _logger.LogDebug("Azure Consumer: Data content: {Data}", jsonData);

            // Placeholder implementation - simulate successful save
            await Task.Delay(10);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Azure Consumer error saving to Azure endpoint");
            return false;
        }
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