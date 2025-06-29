using CloudSync.Core.DTOs;

namespace CloudSync.Core.Services.Interfaces;

public interface IRetryService
{
    Task<T> ExecuteWithRetryAsync<T>(
        Func<Task<T>> operation,
        ErrorType errorType,
        string operationName,
        string? correlationId = null);

    Task ExecuteWithRetryAsync(
        Func<Task> operation,
        ErrorType errorType,
        string operationName,
        string? correlationId = null);

    ProcessingError CreateProcessingError(
        Exception exception,
        string? messageId = null,
        int retryCount = 0,
        string? source = null,
        Dictionary<string, string>? context = null);
} 