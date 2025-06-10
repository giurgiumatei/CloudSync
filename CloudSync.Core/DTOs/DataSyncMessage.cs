using System.Text.Json.Serialization;

namespace CloudSync.Core.DTOs;

public class DataSyncMessage
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();
    
    [JsonPropertyName("data")]
    public string Data { get; set; } = string.Empty;
    
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    
    [JsonPropertyName("source")]
    public string Source { get; set; } = "api";
    
    [JsonPropertyName("retryCount")]
    public int RetryCount { get; set; } = 0;
    
    [JsonPropertyName("correlationId")]
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString();
} 