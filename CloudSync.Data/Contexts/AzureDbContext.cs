using CloudSync.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CloudSync.Data.Contexts;

public class AzureDbContext(DbContextOptions<AzureDbContext> options) : DbContext(options)
{
    public DbSet<DataEntity> DataEntities { get; set; } = null!;
}
