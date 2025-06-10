using CloudSync.Core.DTOs;

namespace CloudSync.Core.Services.Interfaces;

public interface IKafkaProducerService
{
    Task<bool> PublishDataSyncMessageAsync(DataSyncMessage message);
    Task<bool> PublishDataSyncMessageAsync(string data);
    Task<bool> PublishToDeadLetterQueueAsync(DataSyncMessage message, string error);
    Task<bool> PublishToDeadLetterQueueAsync(
        DataSyncMessage message, 
        string error, 
        ProcessingError? processingError = null,
        Dictionary<string, string>? additionalContext = null);
} 