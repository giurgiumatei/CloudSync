using CloudSync.Core.Configuration;
using CloudSync.Core.DTOs;
using CloudSync.Core.Services.Interfaces;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace CloudSync.Core.Services;

public class KafkaProducerService : IKafkaProducerService, IDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly KafkaConfiguration _kafkaConfig;
    private readonly ILogger<KafkaProducerService> _logger;
    private bool _disposed = false;

    public KafkaProducerService(IOptions<KafkaConfiguration> kafkaConfig, ILogger<KafkaProducerService> logger)
    {
        _kafkaConfig = kafkaConfig.Value;
        _logger = logger;

        var config = new ProducerConfig
        {
            BootstrapServers = _kafkaConfig.BootstrapServers,
            ClientId = _kafkaConfig.Producer.ClientId,
            Acks = ParseAcks(_kafkaConfig.Producer.Acks),
            CompressionType = ParseCompressionType(_kafkaConfig.Producer.CompressionType),
            BatchSize = _kafkaConfig.Producer.BatchSize,
            LingerMs = _kafkaConfig.Producer.LingerMs,
            MaxInFlight = _kafkaConfig.Producer.MaxInFlight,
            DeliveryTimeoutMs = _kafkaConfig.Producer.DeliveryTimeoutMs,
            RequestTimeoutMs = _kafkaConfig.Producer.RequestTimeoutMs,
            RetryBackoffMs = _kafkaConfig.Producer.RetryBackoffMs,
            
            // High throughput optimizations
            MessageSendMaxRetries = 3,
            EnableIdempotence = true,
            MessageMaxBytes = 1000000,
            BatchNumMessages = 10000,
            QueueBufferingMaxMessages = 100000,
            QueueBufferingMaxKbytes = 1048576,
            
            // Additional performance settings
            SocketNagleDisable = true,
            SocketKeepaliveEnable = true,
            MetadataMaxAgeMs = 300000
        };

        _producer = new ProducerBuilder<string, string>(config)
            .SetErrorHandler((_, e) => _logger.LogError("Kafka producer error: {Error}", e.Reason))
            .SetLogHandler((_, log) => 
            {
                if (log.Level <= SyslogLevel.Warning)
                    _logger.LogWarning("Kafka log: {Message}", log.Message);
                else
                    _logger.LogDebug("Kafka log: {Message}", log.Message);
            })
            .Build();

        _logger.LogInformation("Kafka producer initialized with bootstrap servers: {BootstrapServers}", _kafkaConfig.BootstrapServers);
    }

    public async Task<bool> PublishDataSyncMessageAsync(DataSyncMessage message)
    {
        try
        {
            var messageJson = JsonSerializer.Serialize(message);
            var kafkaMessage = new Message<string, string>
            {
                Key = message.Id,
                Value = messageJson,
                Headers = new Headers
                {
                    { "MessageType", System.Text.Encoding.UTF8.GetBytes("DataSync") },
                    { "Source", System.Text.Encoding.UTF8.GetBytes(message.Source) },
                    { "CorrelationId", System.Text.Encoding.UTF8.GetBytes(message.CorrelationId) }
                }
            };

            var deliveryResult = await _producer.ProduceAsync(_kafkaConfig.Topics.DataTopic, kafkaMessage);
            
            _logger.LogDebug("Message delivered to partition {Partition} at offset {Offset}", 
                deliveryResult.Partition.Value, deliveryResult.Offset.Value);
            
            return deliveryResult.Status == PersistenceStatus.Persisted;
        }
        catch (ProduceException<string, string> ex)
        {
            _logger.LogError(ex, "Failed to publish message {MessageId}: {Error}", message.Id, ex.Error.Reason);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error publishing message {MessageId}", message.Id);
            return false;
        }
    }

    public async Task<bool> PublishDataSyncMessageAsync(string data)
    {
        var message = new DataSyncMessage
        {
            Data = data,
            Timestamp = DateTime.UtcNow,
            Source = "api"
        };

        return await PublishDataSyncMessageAsync(message);
    }

    public async Task<bool> PublishToDeadLetterQueueAsync(DataSyncMessage message, string error)
    {
        try
        {
            var dlqMessage = new DataSyncMessage
            {
                Id = message.Id,
                Data = message.Data,
                Timestamp = message.Timestamp,
                Source = message.Source,
                RetryCount = message.RetryCount,
                CorrelationId = message.CorrelationId
            };

            var messageJson = JsonSerializer.Serialize(dlqMessage);
            var kafkaMessage = new Message<string, string>
            {
                Key = dlqMessage.Id,
                Value = messageJson,
                Headers = new Headers
                {
                    { "MessageType", System.Text.Encoding.UTF8.GetBytes("DeadLetter") },
                    { "OriginalTopic", System.Text.Encoding.UTF8.GetBytes(_kafkaConfig.Topics.DataTopic) },
                    { "Error", System.Text.Encoding.UTF8.GetBytes(error) },
                    { "FailedAt", System.Text.Encoding.UTF8.GetBytes(DateTime.UtcNow.ToString("O")) }
                }
            };

            var deliveryResult = await _producer.ProduceAsync(_kafkaConfig.Topics.DeadLetterQueue, kafkaMessage);
            
            _logger.LogWarning("Message {MessageId} sent to DLQ due to error: {Error}", message.Id, error);
            
            return deliveryResult.Status == PersistenceStatus.Persisted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send message {MessageId} to DLQ", message.Id);
            return false;
        }
    }

    private static Acks ParseAcks(string acks)
    {
        return acks.ToLowerInvariant() switch
        {
            "none" or "0" => Acks.None,
            "leader" or "1" => Acks.Leader,
            "all" or "-1" => Acks.All,
            _ => Acks.All
        };
    }

    private static CompressionType ParseCompressionType(string compressionType)
    {
        return compressionType.ToLowerInvariant() switch
        {
            "none" => CompressionType.None,
            "gzip" => CompressionType.Gzip,
            "snappy" => CompressionType.Snappy,
            "lz4" => CompressionType.Lz4,
            "zstd" => CompressionType.Zstd,
            _ => CompressionType.Lz4
        };
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _producer?.Flush(TimeSpan.FromSeconds(10));
            _producer?.Dispose();
            _disposed = true;
            _logger.LogInformation("Kafka producer disposed");
        }
    }
} 