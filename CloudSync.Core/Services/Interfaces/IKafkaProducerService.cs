using CloudSync.Core.DTOs;

namespace CloudSync.Core.Services.Interfaces;

public interface IKafkaProducerService
{
    Task<bool> PublishDataSyncMessageAsync(DataSyncMessage message);
    Task<bool> PublishDataSyncMessageAsync(string data);
    Task<bool> PublishToDeadLetterQueueAsync(DataSyncMessage message, string error);
} 