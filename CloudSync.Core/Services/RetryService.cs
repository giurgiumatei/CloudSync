using CloudSync.Core.Configuration;
using CloudSync.Core.DTOs;
using Microsoft.Extensions.Logging;

namespace CloudSync.Core.Services;

public class RetryService
{
    private readonly RetryConfiguration _config;
    private readonly ILogger<RetryService> _logger;
    private readonly CircuitBreakerState _circuitBreaker;
    private readonly Random _random = new();

    public RetryService(RetryConfiguration config, ILogger<RetryService> logger)
    {
        _config = config;
        _logger = logger;
        _circuitBreaker = new CircuitBreakerState(_config);
    }

    public async Task<T> ExecuteWithRetryAsync<T>(
        Func<Task<T>> operation,
        ErrorType errorType,
        string operationName,
        string? correlationId = null)
    {
        if (_config.EnableCircuitBreaker && _circuitBreaker.IsOpen)
        {
            throw new InvalidOperationException($"Circuit breaker is open for operation: {operationName}");
        }

        var attempt = 0;
        Exception? lastException = null;

        while (attempt <= _config.MaxRetries)
        {
            try
            {
                _logger.LogDebug("Executing operation {OperationName}, attempt {Attempt}/{MaxRetries}, correlation: {CorrelationId}",
                    operationName, attempt + 1, _config.MaxRetries + 1, correlationId);

                var result = await operation();
                
                if (attempt > 0)
                {
                    _logger.LogInformation("Operation {OperationName} succeeded after {Attempts} attempts, correlation: {CorrelationId}",
                        operationName, attempt + 1, correlationId);
                }

                _circuitBreaker.RecordSuccess();
                return result;
            }
            catch (Exception ex)
            {
                lastException = ex;
                var classifiedError = ErrorClassifier.ClassifyException(ex);
                
                _logger.LogWarning(ex, "Operation {OperationName} failed on attempt {Attempt}/{MaxRetries}, error type: {ErrorType}, correlation: {CorrelationId}",
                    operationName, attempt + 1, _config.MaxRetries + 1, classifiedError, correlationId);

                _circuitBreaker.RecordFailure();

                // Check if error is retryable
                if (!ErrorClassifier.IsRetryable(classifiedError))
                {
                    _logger.LogError("Operation {OperationName} failed with non-retryable error: {ErrorType}, correlation: {CorrelationId}",
                        operationName, classifiedError, correlationId);
                    throw;
                }

                // Check if we've exhausted retries
                if (attempt >= _config.MaxRetries)
                {
                    _logger.LogError("Operation {OperationName} failed after {MaxRetries} retries, correlation: {CorrelationId}",
                        operationName, _config.MaxRetries, correlationId);
                    break;
                }

                // Calculate delay with exponential backoff and jitter
                var delay = CalculateDelay(attempt);
                _logger.LogDebug("Retrying operation {OperationName} in {DelayMs}ms, correlation: {CorrelationId}",
                    operationName, delay.TotalMilliseconds, correlationId);

                await Task.Delay(delay);
                attempt++;
            }
        }

        throw lastException ?? new InvalidOperationException($"Operation {operationName} failed without exception");
    }

    public async Task ExecuteWithRetryAsync(
        Func<Task> operation,
        ErrorType errorType,
        string operationName,
        string? correlationId = null)
    {
        await ExecuteWithRetryAsync(async () =>
        {
            await operation();
            return true;
        }, errorType, operationName, correlationId);
    }

    private TimeSpan CalculateDelay(int attempt)
    {
        // Exponential backoff: delay = initialDelay * (backoffMultiplier ^ attempt)
        var exponentialDelay = _config.InitialDelayMs * Math.Pow(_config.BackoffMultiplier, attempt);
        
        // Cap at maximum delay
        exponentialDelay = Math.Min(exponentialDelay, _config.MaxDelayMs);
        
        // Add jitter to prevent thundering herd
        var jitterRange = exponentialDelay * _config.Jitter;
        var jitter = (_random.NextDouble() - 0.5) * 2 * jitterRange;
        
        var finalDelay = Math.Max(0, exponentialDelay + jitter);
        return TimeSpan.FromMilliseconds(finalDelay);
    }

    public ProcessingError CreateProcessingError(
        Exception exception,
        string? messageId = null,
        int retryCount = 0,
        string? source = null,
        Dictionary<string, string>? context = null)
    {
        var errorType = ErrorClassifier.ClassifyException(exception);
        var severity = ErrorClassifier.GetErrorSeverity(errorType);

        return new ProcessingError
        {
            Type = errorType,
            Severity = severity,
            Message = exception.Message,
            StackTrace = exception.StackTrace,
            Source = source,
            RetryCount = retryCount,
            MessageId = messageId,
            Context = context ?? new Dictionary<string, string>()
        };
    }
}

public class CircuitBreakerState
{
    private readonly RetryConfiguration _config;
    private int _failureCount = 0;
    private DateTime? _lastFailureTime;
    private readonly object _lock = new();

    public CircuitBreakerState(RetryConfiguration config)
    {
        _config = config;
    }

    public bool IsOpen
    {
        get
        {
            lock (_lock)
            {
                if (_failureCount < _config.CircuitBreakerThreshold)
                    return false;

                if (_lastFailureTime == null)
                    return false;

                var timeSinceLastFailure = DateTime.UtcNow - _lastFailureTime.Value;
                if (timeSinceLastFailure > TimeSpan.FromMilliseconds(_config.CircuitBreakerTimeoutMs))
                {
                    // Reset circuit breaker
                    _failureCount = 0;
                    _lastFailureTime = null;
                    return false;
                }

                return true;
            }
        }
    }

    public void RecordFailure()
    {
        lock (_lock)
        {
            _failureCount++;
            _lastFailureTime = DateTime.UtcNow;
        }
    }

    public void RecordSuccess()
    {
        lock (_lock)
        {
            _failureCount = 0;
            _lastFailureTime = null;
        }
    }
} 