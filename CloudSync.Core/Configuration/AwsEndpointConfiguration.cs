namespace CloudSync.Core.Configuration;

public class AwsEndpointConfiguration
{
    public string BaseUrl { get; set; } = "https://api.aws-example.com";
    public int TimeoutSeconds { get; set; } = 30;
    public int MaxRetries { get; set; } = 3;
    public int RetryDelayMs { get; set; } = 1000;
} 