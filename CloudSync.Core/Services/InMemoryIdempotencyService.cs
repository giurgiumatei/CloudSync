using CloudSync.Core.Configuration;
using CloudSync.Core.Services.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace CloudSync.Core.Services;

public class InMemoryIdempotencyService : IIdempotencyService, IDisposable
{
    private readonly IdempotencyConfiguration _config;
    private readonly ILogger<InMemoryIdempotencyService> _logger;
    
    // Thread-safe storage for processed messages by consumer group
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ProcessedMessageInfo>> _processedMessages;
    
    // Thread-safe storage for processing locks
    private readonly ConcurrentDictionary<string, ProcessingLock> _processingLocks;
    
    // Statistics tracking
    private readonly IdempotencyStats _stats;
    private readonly object _statsLock = new();
    
    // Cleanup timer
    private readonly Timer _cleanupTimer;
    private bool _disposed = false;

    public InMemoryIdempotencyService(
        IOptions<IdempotencyConfiguration> config,
        ILogger<InMemoryIdempotencyService> logger)
    {
        _config = config.Value;
        _logger = logger;
        _processedMessages = new ConcurrentDictionary<string, ConcurrentDictionary<string, ProcessedMessageInfo>>();
        _processingLocks = new ConcurrentDictionary<string, ProcessingLock>();
        _stats = new IdempotencyStats();

        // Start cleanup timer
        var cleanupInterval = TimeSpan.FromMinutes(_config.CleanupIntervalMinutes);
        _cleanupTimer = new Timer(PerformCleanup, null, cleanupInterval, cleanupInterval);

        _logger.LogInformation("InMemory Idempotency Service initialized with cache retention: {RetentionMinutes} minutes, max size: {MaxSize}",
            _config.CacheRetentionMinutes, _config.MaxCacheSize);
    }

    public async Task<bool> IsMessageProcessedAsync(string messageId, string consumerGroup)
    {
        try
        {
            if (!_processedMessages.TryGetValue(consumerGroup, out var groupCache))
            {
                groupCache = new ConcurrentDictionary<string, ProcessedMessageInfo>();
                _processedMessages.TryAdd(consumerGroup, groupCache);
            }

            var isProcessed = groupCache.ContainsKey(messageId);

            // Update statistics
            if (_config.EnableStatistics)
            {
                UpdateStats(stats =>
                {
                    stats.TotalMessagesChecked++;
                    if (isProcessed)
                    {
                        stats.DuplicateMessagesDetected++;
                    }
                    
                    if (!stats.ConsumerGroupStats.TryGetValue(consumerGroup, out var groupStats))
                    {
                        groupStats = new ConsumerGroupStats { GroupName = consumerGroup };
                        stats.ConsumerGroupStats.TryAdd(consumerGroup, groupStats);
                    }
                    if (isProcessed)
                    {
                        groupStats.DuplicatesDetected++;
                    }
                    groupStats.LastActivity = DateTime.UtcNow;
                });
            }

            if (isProcessed && _config.EnableDetailedLogging)
            {
                _logger.LogDebug("Duplicate message detected: {MessageId} in group {ConsumerGroup}", 
                    messageId, consumerGroup);
            }

            return await Task.FromResult(isProcessed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking if message {MessageId} was processed in group {ConsumerGroup}", 
                messageId, consumerGroup);
            return false; // Assume not processed on error to avoid losing messages
        }
    }

    public async Task<bool> MarkMessageProcessedAsync(string messageId, string consumerGroup, bool processingResult)
    {
        try
        {
            if (!_processedMessages.TryGetValue(consumerGroup, out var groupCache))
            {
                groupCache = new ConcurrentDictionary<string, ProcessedMessageInfo>();
                _processedMessages.TryAdd(consumerGroup, groupCache);
            }

            var processedInfo = new ProcessedMessageInfo
            {
                MessageId = messageId,
                ProcessedAt = DateTime.UtcNow,
                ProcessingResult = processingResult,
                ConsumerGroup = consumerGroup
            };

            // Check cache size limit
            if (groupCache.Count >= _config.MaxCacheSize)
            {
                _logger.LogWarning("Cache size limit reached for group {ConsumerGroup}, performing emergency cleanup", 
                    consumerGroup);
                await PerformEmergencyCleanup(consumerGroup);
            }

            groupCache.TryAdd(messageId, processedInfo);

            // Update statistics
            if (_config.EnableStatistics)
            {
                UpdateStats(stats =>
                {
                    stats.UniqueMessagesProcessed++;
                    
                    if (!stats.ConsumerGroupStats.TryGetValue(consumerGroup, out var groupStats))
                    {
                        groupStats = new ConsumerGroupStats { GroupName = consumerGroup };
                        stats.ConsumerGroupStats.TryAdd(consumerGroup, groupStats);
                    }
                    groupStats.MessagesProcessed++;
                    groupStats.CacheSize = groupCache.Count;
                    groupStats.LastActivity = DateTime.UtcNow;
                });
            }

            if (_config.EnableDetailedLogging)
            {
                _logger.LogDebug("Marked message {MessageId} as processed in group {ConsumerGroup}, result: {Result}", 
                    messageId, consumerGroup, processingResult);
            }

            return await Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking message {MessageId} as processed in group {ConsumerGroup}", 
                messageId, consumerGroup);
            return false;
        }
    }

    public async Task<bool> TryAcquireProcessingLockAsync(string messageId, string consumerGroup, int lockTimeoutMs = 30000)
    {
        try
        {
            var lockKey = $"{consumerGroup}:{messageId}";
            var expiryTime = DateTime.UtcNow.AddMilliseconds(lockTimeoutMs);
            
            var processingLock = new ProcessingLock
            {
                MessageId = messageId,
                ConsumerGroup = consumerGroup,
                ExpiryTime = expiryTime,
                AcquiredAt = DateTime.UtcNow
            };

            var lockAcquired = _processingLocks.TryAdd(lockKey, processingLock);

            if (lockAcquired && _config.EnableStatistics)
            {
                UpdateStats(stats => stats.ProcessingLocksAcquired++);
            }

            if (_config.EnableDetailedLogging)
            {
                _logger.LogDebug("Processing lock for {MessageId} in group {ConsumerGroup}: {Result}", 
                    messageId, consumerGroup, lockAcquired ? "ACQUIRED" : "FAILED");
            }

            return await Task.FromResult(lockAcquired);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error acquiring processing lock for message {MessageId} in group {ConsumerGroup}", 
                messageId, consumerGroup);
            return false;
        }
    }

    public async Task<bool> ReleaseProcessingLockAsync(string messageId, string consumerGroup)
    {
        try
        {
            var lockKey = $"{consumerGroup}:{messageId}";
            var lockReleased = _processingLocks.TryRemove(lockKey, out _);

            if (lockReleased && _config.EnableStatistics)
            {
                UpdateStats(stats => stats.ProcessingLocksReleased++);
            }

            if (_config.EnableDetailedLogging)
            {
                _logger.LogDebug("Processing lock for {MessageId} in group {ConsumerGroup}: {Result}", 
                    messageId, consumerGroup, lockReleased ? "RELEASED" : "NOT_FOUND");
            }

            return await Task.FromResult(lockReleased);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error releasing processing lock for message {MessageId} in group {ConsumerGroup}", 
                messageId, consumerGroup);
            return false;
        }
    }

    public async Task<IdempotencyStats> GetStatsAsync()
    {
        IdempotencyStats statsCopy;
        
        lock (_statsLock)
        {
            // Update current cache sizes
            _stats.CurrentCacheSize = _processedMessages.Values.Sum(cache => cache.Count);
            
            foreach (var kvp in _processedMessages)
            {
                if (!_stats.ConsumerGroupStats.TryGetValue(kvp.Key, out var groupStats))
                {
                    groupStats = new ConsumerGroupStats { GroupName = kvp.Key };
                    _stats.ConsumerGroupStats.TryAdd(kvp.Key, groupStats);
                }
                groupStats.CacheSize = kvp.Value.Count;
            }

            // Create a deep copy for thread safety
            statsCopy = new IdempotencyStats
            {
                TotalMessagesChecked = _stats.TotalMessagesChecked,
                DuplicateMessagesDetected = _stats.DuplicateMessagesDetected,
                UniqueMessagesProcessed = _stats.UniqueMessagesProcessed,
                ProcessingLocksAcquired = _stats.ProcessingLocksAcquired,
                ProcessingLocksReleased = _stats.ProcessingLocksReleased,
                ExpiredEntriesCleanedUp = _stats.ExpiredEntriesCleanedUp,
                CurrentCacheSize = _stats.CurrentCacheSize,
                LastCleanupTime = _stats.LastCleanupTime,
                ConsumerGroupStats = new ConcurrentDictionary<string, ConsumerGroupStats>(_stats.ConsumerGroupStats)
            };
        }

        return await Task.FromResult(statsCopy);
    }

    private void PerformCleanup(object? state)
    {
        try
        {
            var cutoffTime = DateTime.UtcNow.AddMinutes(-_config.CacheRetentionMinutes);
            var cleanedUpCount = 0;

            foreach (var groupKvp in _processedMessages)
            {
                var groupName = groupKvp.Key;
                var groupCache = groupKvp.Value;
                
                var expiredKeys = groupCache
                    .Where(kvp => kvp.Value.ProcessedAt < cutoffTime)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var expiredKey in expiredKeys)
                {
                    if (groupCache.TryRemove(expiredKey, out _))
                    {
                        cleanedUpCount++;
                    }
                }

                // Remove empty group caches
                if (groupCache.IsEmpty)
                {
                    _processedMessages.TryRemove(groupName, out _);
                }
            }

            // Clean up expired processing locks
            var expiredLocks = _processingLocks
                .Where(kvp => kvp.Value.ExpiryTime < DateTime.UtcNow)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var expiredLock in expiredLocks)
            {
                _processingLocks.TryRemove(expiredLock, out _);
            }

            // Update statistics
            if (_config.EnableStatistics)
            {
                UpdateStats(stats =>
                {
                    stats.ExpiredEntriesCleanedUp += cleanedUpCount;
                    stats.LastCleanupTime = DateTime.UtcNow;
                });
            }

            if (cleanedUpCount > 0)
            {
                _logger.LogDebug("Cleanup completed: removed {Count} expired entries and {LockCount} expired locks", 
                    cleanedUpCount, expiredLocks.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during idempotency cache cleanup");
        }
    }

    private async Task PerformEmergencyCleanup(string consumerGroup)
    {
        try
        {
            if (!_processedMessages.TryGetValue(consumerGroup, out var groupCache))
                return;

            // Remove oldest 25% of entries
            var entriesToRemove = groupCache.Count / 4;
            var oldestEntries = groupCache
                .OrderBy(kvp => kvp.Value.ProcessedAt)
                .Take(entriesToRemove)
                .Select(kvp => kvp.Key)
                .ToList();

            var removedCount = 0;
            foreach (var key in oldestEntries)
            {
                if (groupCache.TryRemove(key, out _))
                {
                    removedCount++;
                }
            }

            _logger.LogWarning("Emergency cleanup for group {ConsumerGroup}: removed {Count} oldest entries", 
                consumerGroup, removedCount);

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during emergency cleanup for group {ConsumerGroup}", consumerGroup);
        }
    }

    private void UpdateStats(Action<IdempotencyStats> updateAction)
    {
        lock (_statsLock)
        {
            updateAction(_stats);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _cleanupTimer?.Dispose();
            _processedMessages.Clear();
            _processingLocks.Clear();
            _disposed = true;
            _logger.LogInformation("InMemory Idempotency Service disposed");
        }
    }
}

internal class ProcessedMessageInfo
{
    public string MessageId { get; set; } = string.Empty;
    public DateTime ProcessedAt { get; set; }
    public bool ProcessingResult { get; set; }
    public string ConsumerGroup { get; set; } = string.Empty;
}

internal class ProcessingLock
{
    public string MessageId { get; set; } = string.Empty;
    public string ConsumerGroup { get; set; } = string.Empty;
    public DateTime ExpiryTime { get; set; }
    public DateTime AcquiredAt { get; set; }
} 