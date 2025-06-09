using CloudSync.Core.Services.Interfaces;
using CloudSync.Data.Contexts;
using CloudSync.Data.Entities;

namespace CloudSync.Core.Services;

public class SyncService : ISyncService
{
    private readonly AzureDbContext _azureContext;
    private readonly AwsDbContext _awsContext;

    public SyncService(AzureDbContext azureContext, AwsDbContext awsContext)
    {
        _azureContext = azureContext;
        _awsContext = awsContext;
    }

    public async Task<bool> SyncDataAsync(string data)
    {
        // Create separate entity instances for each database to avoid identity conflicts
        var azureEntity = new DataEntity { Data = data };
        var awsEntity = new DataEntity { Data = data };

        await using var azureTransaction = await _azureContext.Database.BeginTransactionAsync();
        await using var awsTransaction = await _awsContext.Database.BeginTransactionAsync();

        try
        {
            await _azureContext.DataEntities.AddAsync(azureEntity);
            await _awsContext.DataEntities.AddAsync(awsEntity);

            await _azureContext.SaveChangesAsync();
            await _awsContext.SaveChangesAsync();

            await azureTransaction.CommitAsync();
            await awsTransaction.CommitAsync();

            return true;
        }
        catch
        {
            await azureTransaction.RollbackAsync();
            await awsTransaction.RollbackAsync();
            return false;
        }
    }
}
