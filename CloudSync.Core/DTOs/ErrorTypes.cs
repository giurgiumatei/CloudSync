using System.Net;

namespace CloudSync.Core.DTOs;

public enum ErrorType
{
    Transient,
    Permanent,
    Timeout,
    Authentication,
    RateLimited,
    ServiceUnavailable,
    NetworkError,
    SerializationError,
    ValidationError,
    Unknown
}

public enum ErrorSeverity
{
    Low,
    Medium,
    High,
    Critical
}

public class ProcessingError
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public ErrorType Type { get; set; }
    public ErrorSeverity Severity { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? StackTrace { get; set; }
    public string? Source { get; set; }
    public int RetryCount { get; set; }
    public string? MessageId { get; set; }
    public Dictionary<string, string> Context { get; set; } = new();
}

public static class ErrorClassifier
{
    public static ErrorType ClassifyException(Exception exception)
    {
        return exception switch
        {
            HttpRequestException httpEx => ClassifyHttpException(httpEx),
            TaskCanceledException or TimeoutException => ErrorType.Timeout,
            UnauthorizedAccessException => ErrorType.Authentication,
            ArgumentException or ArgumentNullException => ErrorType.ValidationError,
            System.Text.Json.JsonException => ErrorType.SerializationError,
            SocketException => ErrorType.NetworkError,
            _ => ErrorType.Unknown
        };
    }

    public static ErrorType ClassifyHttpStatusCode(HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.BadRequest => ErrorType.ValidationError,
            HttpStatusCode.Unauthorized => ErrorType.Authentication,
            HttpStatusCode.Forbidden => ErrorType.Authentication,
            HttpStatusCode.NotFound => ErrorType.Permanent,
            HttpStatusCode.RequestTimeout => ErrorType.Timeout,
            HttpStatusCode.TooManyRequests => ErrorType.RateLimited,
            HttpStatusCode.InternalServerError => ErrorType.Transient,
            HttpStatusCode.BadGateway => ErrorType.Transient,
            HttpStatusCode.ServiceUnavailable => ErrorType.ServiceUnavailable,
            HttpStatusCode.GatewayTimeout => ErrorType.Timeout,
            _ when ((int)statusCode >= 400 && (int)statusCode < 500) => ErrorType.Permanent,
            _ when ((int)statusCode >= 500) => ErrorType.Transient,
            _ => ErrorType.Unknown
        };
    }

    private static ErrorType ClassifyHttpException(HttpRequestException httpEx)
    {
        var message = httpEx.Message.ToLowerInvariant();
        
        if (message.Contains("timeout") || message.Contains("timed out"))
            return ErrorType.Timeout;
        
        if (message.Contains("connection") || message.Contains("network"))
            return ErrorType.NetworkError;
        
        if (message.Contains("unauthorized") || message.Contains("forbidden"))
            return ErrorType.Authentication;
        
        return ErrorType.Transient;
    }

    public static ErrorSeverity GetErrorSeverity(ErrorType errorType)
    {
        return errorType switch
        {
            ErrorType.Authentication => ErrorSeverity.High,
            ErrorType.Permanent => ErrorSeverity.High,
            ErrorType.SerializationError => ErrorSeverity.Medium,
            ErrorType.ValidationError => ErrorSeverity.Medium,
            ErrorType.Timeout => ErrorSeverity.Medium,
            ErrorType.RateLimited => ErrorSeverity.Low,
            ErrorType.Transient => ErrorSeverity.Low,
            ErrorType.ServiceUnavailable => ErrorSeverity.Medium,
            ErrorType.NetworkError => ErrorSeverity.Medium,
            _ => ErrorSeverity.Low
        };
    }

    public static bool IsRetryable(ErrorType errorType)
    {
        return errorType switch
        {
            ErrorType.Transient => true,
            ErrorType.Timeout => true,
            ErrorType.RateLimited => true,
            ErrorType.ServiceUnavailable => true,
            ErrorType.NetworkError => true,
            ErrorType.Authentication => false,
            ErrorType.Permanent => false,
            ErrorType.SerializationError => false,
            ErrorType.ValidationError => false,
            _ => false
        };
    }
} 