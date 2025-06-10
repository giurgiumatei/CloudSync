using CloudSync.Core.DTOs;

namespace CloudSync.Core.Services.Interfaces;

public interface IKafkaConsumerService
{
    Task StartConsumingAsync(CancellationToken cancellationToken);
    Task StopConsumingAsync();
    Task<bool> ProcessMessageAsync(DataSyncMessage message);
} 