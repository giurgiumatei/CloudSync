namespace CloudSync.Core.Configuration;
using System.Collections.Concurrent;

public class IdempotencyConfiguration
{
    /// <summary>
    /// How long to keep processed message IDs in memory (in minutes)
    /// </summary>
    public int CacheRetentionMinutes { get; set; } = 60;

    /// <summary>
    /// Maximum number of message IDs to keep in memory per consumer group
    /// </summary>
    public int MaxCacheSize { get; set; } = 100000;

    /// <summary>
    /// How often to clean up expired entries (in minutes)
    /// </summary>
    public int CleanupIntervalMinutes { get; set; } = 5;

    /// <summary>
    /// Default processing lock timeout in milliseconds
    /// </summary>
    public int DefaultLockTimeoutMs { get; set; } = 30000;

    /// <summary>
    /// Whether to enable persistent storage for processed message IDs
    /// </summary>
    public bool EnablePersistentStorage { get; set; } = false;

    /// <summary>
    /// Connection string for persistent storage (if enabled)
    /// </summary>
    public string? PersistentStorageConnectionString { get; set; }

    /// <summary>
    /// Whether to enable detailed logging of duplicate message detection
    /// </summary>
    public bool EnableDetailedLogging { get; set; } = true;

    /// <summary>
    /// Whether to track processing statistics
    /// </summary>
    public bool EnableStatistics { get; set; } = true;

    public ConcurrentDictionary<string, ConsumerGroupStats> ConsumerGroupStats { get; set; } = new();
}

public class IdempotencyStats
{
    public long TotalMessagesChecked { get; set; }
    public long DuplicateMessagesDetected { get; set; }
    public long UniqueMessagesProcessed { get; set; }
    public long ProcessingLocksAcquired { get; set; }
    public long ProcessingLocksReleased { get; set; }
    public long ExpiredEntriesCleanedUp { get; set; }
    public int CurrentCacheSize { get; set; }
    public DateTime LastCleanupTime { get; set; }
    public ConcurrentDictionary<string, ConsumerGroupStats> ConsumerGroupStats { get; set; } = new();

    public double DuplicationRate => TotalMessagesChecked > 0 
        ? (double)DuplicateMessagesDetected / TotalMessagesChecked * 100 
        : 0;
}

public class ConsumerGroupStats
{
    public string GroupName { get; set; } = string.Empty;
    public long MessagesProcessed { get; set; }
    public long DuplicatesDetected { get; set; }
    public int CacheSize { get; set; }
    public DateTime LastActivity { get; set; }
} 