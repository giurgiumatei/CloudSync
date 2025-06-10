using CloudSync.Core.Configuration;
using CloudSync.Core.DTOs;
using CloudSync.Core.Services.Interfaces;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace CloudSync.KafkaAwsConsumer.Services;

public class AwsKafkaConsumerService : IKafkaConsumerService, IDisposable
{
    private readonly IConsumer<string, string> _consumer;
    private readonly IKafkaProducerService _kafkaProducerService;
    private readonly KafkaConfiguration _kafkaConfig;
    private readonly AwsEndpointConfiguration _awsConfig;
    private readonly ILogger<AwsKafkaConsumerService> _logger;
    private readonly HttpClient _httpClient;
    private bool _isConsuming = false;
    private bool _disposed = false;

    public AwsKafkaConsumerService(
        IOptions<KafkaConfiguration> kafkaConfig,
        IOptions<AwsEndpointConfiguration> awsConfig,
        IKafkaProducerService kafkaProducerService,
        ILogger<AwsKafkaConsumerService> logger,
        HttpClient httpClient)
    {
        _kafkaConfig = kafkaConfig.Value;
        _awsConfig = awsConfig.Value;
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
            .SetErrorHandler((_, e) => _logger.LogError("AWS Consumer error: {Error}", e.Reason))
            .SetPartitionsAssignedHandler((_, partitions) => 
            {
                _logger.LogInformation("AWS Consumer assigned partitions: [{Partitions}]", 
                    string.Join(", ", partitions.Select(p => $"{p.Topic}[{p.Partition}]")));
            })
            .Build();

        _httpClient.Timeout = TimeSpan.FromSeconds(_awsConfig.TimeoutSeconds);
        _logger.LogInformation("AWS Kafka Consumer initialized for group: {GroupId}", _kafkaConfig.Consumer.GroupId);
    }

    public async Task StartConsumingAsync(CancellationToken cancellationToken)
    {
        _consumer.Subscribe(_kafkaConfig.Topics.DataTopic);
        _isConsuming = true;

        _logger.LogInformation("AWS Consumer started consuming from topic: {Topic}", _kafkaConfig.Topics.DataTopic);

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
                    _logger.LogError(ex, "AWS Consumer error: {Error}", ex.Error.Reason);
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
            _logger.LogInformation("AWS Consumer stopped");
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

            _logger.LogInformation("AWS Consumer processing message {MessageId} from partition {Partition}", 
                message.Id, consumeResult.Partition.Value);

            var success = await ProcessMessageAsync(message);

            if (success)
            {
                _consumer.Commit(consumeResult);
                _logger.LogInformation("AWS Consumer successfully processed message {MessageId}", message.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AWS Consumer error processing message");
        }
    }

    public async Task<bool> ProcessMessageAsync(DataSyncMessage message)
    {
        try
        {
            var success = await SaveToAwsEndpoint(message.Data);
            
            if (!success)
            {
                await _kafkaProducerService.PublishToDeadLetterQueueAsync(message, "Failed to save to AWS endpoint");
            }
            
            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AWS Consumer exception processing message {MessageId}", message.Id);
            await _kafkaProducerService.PublishToDeadLetterQueueAsync(message, ex.Message);
            return false;
        }
    }

    private async Task<bool> SaveToAwsEndpoint(string jsonData)
    {
        try
        {
            _logger.LogInformation("AWS Consumer: Saving data to AWS endpoint: {BaseUrl}", _awsConfig.BaseUrl);
            _logger.LogDebug("AWS Consumer: Data content: {Data}", jsonData);

            // Placeholder implementation - simulate successful save
            await Task.Delay(10);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AWS Consumer error saving to AWS endpoint");
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
            _logger.LogInformation("AWS Kafka Consumer disposed");
        }
    }
} 