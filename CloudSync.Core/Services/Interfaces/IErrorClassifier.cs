using CloudSync.Core.DTOs;

namespace CloudSync.Core.Services.Interfaces;

public interface IErrorClassifier
{
    ErrorType ClassifyError(Exception exception);
    ErrorType ClassifyHttpStatusCode(System.Net.HttpStatusCode statusCode);
    ErrorSeverity GetErrorSeverity(ErrorType errorType);
    bool IsRetryable(ErrorType errorType);
} 