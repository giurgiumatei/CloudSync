namespace CloudSync.Core.Configuration;

public class RetryConfiguration
{
    public int MaxRetries { get; set; } = 3;
    public int InitialDelayMs { get; set; } = 1000;
    public int MaxDelayMs { get; set; } = 30000;
    public double BackoffMultiplier { get; set; } = 2.0;
    public double Jitter { get; set; } = 0.1;
    public int CircuitBreakerThreshold { get; set; } = 5;
    public int CircuitBreakerTimeoutMs { get; set; } = 60000;
    public bool EnableCircuitBreaker { get; set; } = true;
}

public class ErrorHandlingConfiguration
{
    public RetryConfiguration TransientErrors { get; set; } = new();
    public RetryConfiguration PermanentErrors { get; set; } = new() { MaxRetries = 0 };
    public bool EnableDetailedErrorLogging { get; set; } = true;
    public bool SendToDlqOnPermanentError { get; set; } = true;
    public bool SendToDlqOnMaxRetries { get; set; } = true;
} 