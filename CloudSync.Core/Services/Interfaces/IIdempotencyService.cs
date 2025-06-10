using CloudSync.Core.Configuration;

namespace CloudSync.Core.Services.Interfaces;

public interface IIdempotencyService
{
    /// <summary>
    /// Checks if a message has already been processed
    /// </summary>
    /// <param name="messageId">Unique message identifier</param>
    /// <param name="consumerGroup">Consumer group name for scoping</param>
    /// <returns>True if message was already processed, false if it's new</returns>
    Task<bool> IsMessageProcessedAsync(string messageId, string consumerGroup);

    /// <summary>
    /// Marks a message as processed
    /// </summary>
    /// <param name="messageId">Unique message identifier</param>
    /// <param name="consumerGroup">Consumer group name for scoping</param>
    /// <param name="processingResult">Result of processing (success/failure)</param>
    /// <returns>True if successfully marked</returns>
    Task<bool> MarkMessageProcessedAsync(string messageId, string consumerGroup, bool processingResult);

    /// <summary>
    /// Attempts to acquire a processing lock for a message
    /// </summary>
    /// <param name="messageId">Unique message identifier</param>
    /// <param name="consumerGroup">Consumer group name for scoping</param>
    /// <param name="lockTimeoutMs">Lock timeout in milliseconds</param>
    /// <returns>True if lock acquired, false if already locked</returns>
    Task<bool> TryAcquireProcessingLockAsync(string messageId, string consumerGroup, int lockTimeoutMs = 30000);

    /// <summary>
    /// Releases a processing lock for a message
    /// </summary>
    /// <param name="messageId">Unique message identifier</param>
    /// <param name="consumerGroup">Consumer group name for scoping</param>
    /// <returns>True if successfully released</returns>
    Task<bool> ReleaseProcessingLockAsync(string messageId, string consumerGroup);

    /// <summary>
    /// Gets processing statistics for monitoring
    /// </summary>
    /// <returns>Idempotency statistics</returns>
    Task<IdempotencyStats> GetStatsAsync();
} 