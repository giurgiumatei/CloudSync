namespace CloudSync.Core.Configuration;

public class KafkaConfiguration
{
    public string BootstrapServers { get; set; } = "kafka:9092";
    public ProducerConfiguration Producer { get; set; } = new();
    public TopicsConfiguration Topics { get; set; } = new();
}

public class ProducerConfiguration
{
    public string ClientId { get; set; } = "cloudsync-api-producer";
    public string Acks { get; set; } = "All";
    public string CompressionType { get; set; } = "Lz4";
    public int BatchSize { get; set; } = 1000000;
    public int LingerMs { get; set; } = 5;
    public int MaxInFlight { get; set; } = 5;
    public int DeliveryTimeoutMs { get; set; } = 300000;
    public int RequestTimeoutMs { get; set; } = 30000;
    public int RetryBackoffMs { get; set; } = 100;
}

public class TopicsConfiguration
{
    public string DataTopic { get; set; } = "data-topic";
    public string DeadLetterQueue { get; set; } = "data-topic-dlq";
} 