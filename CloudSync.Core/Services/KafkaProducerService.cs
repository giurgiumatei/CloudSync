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
        return await PublishToDeadLetterQueueAsync(message, error, null, null);
    }

    public async Task<bool> PublishToDeadLetterQueueAsync(
        DataSyncMessage message, 
        string error, 
        ProcessingError? processingError = null,
        Dictionary<string, string>? additionalContext = null)
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
                    { "FailedAt", System.Text.Encoding.UTF8.GetBytes(DateTime.UtcNow.ToString("O")) },
                    { "RetryCount", System.Text.Encoding.UTF8.GetBytes(message.RetryCount.ToString()) }
                }
            };

            // Add processing error details if available
            if (processingError != null)
            {
                kafkaMessage.Headers.Add("ErrorType", System.Text.Encoding.UTF8.GetBytes(processingError.Type.ToString()));
                kafkaMessage.Headers.Add("ErrorSeverity", System.Text.Encoding.UTF8.GetBytes(processingError.Severity.ToString()));
                kafkaMessage.Headers.Add("ErrorId", System.Text.Encoding.UTF8.GetBytes(processingError.Id));
                
                if (!string.IsNullOrEmpty(processingError.Source))
                    kafkaMessage.Headers.Add("ErrorSource", System.Text.Encoding.UTF8.GetBytes(processingError.Source));
            }

            // Add additional context if provided
            if (additionalContext != null)
            {
                foreach (var kvp in additionalContext)
                {
                    kafkaMessage.Headers.Add($"Context_{kvp.Key}", System.Text.Encoding.UTF8.GetBytes(kvp.Value));
                }
            }

            var deliveryResult = await _producer.ProduceAsync(_kafkaConfig.Topics.DeadLetterQueue, kafkaMessage);
            
            var errorType = processingError != null ? processingError.Type.ToString() : "Unknown";
            var severity = processingError != null ? processingError.Severity.ToString() : "Unknown";
            
            _logger.LogWarning("Message {MessageId} sent to DLQ - Error: {Error}, Type: {ErrorType}, Severity: {Severity}, Retries: {RetryCount}", 
                message.Id, error, errorType, severity, message.RetryCount);
            
            return deliveryResult.Status == PersistenceStatus.Persisted;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Critical failure: Could not send message {MessageId} to DLQ. Message may be lost!", message.Id);
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