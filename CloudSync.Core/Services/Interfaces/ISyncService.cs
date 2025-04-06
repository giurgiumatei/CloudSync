namespace CloudSync.Core.Services.Interfaces;

public interface ISyncService
{
    Task<bool> SyncDataAsync(string data);
}
