using CloudSync.Core.Configuration;
using CloudSync.Core.Services;
using CloudSync.Core.Services.Interfaces;
using CloudSync.Data.Contexts;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

builder.Services.AddDbContext<AzureDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("AzureConnection")));

builder.Services.AddDbContext<AwsDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("AwsConnection")));

// Configure Kafka
builder.Services.Configure<KafkaConfiguration>(builder.Configuration.GetSection("Kafka"));

// Register services
builder.Services.AddScoped<ISyncService, SyncService>();
builder.Services.AddSingleton<IKafkaProducerService, KafkaProducerService>();

// Add health checks
builder.Services.AddHealthChecks()
    .AddSqlServer(builder.Configuration.GetConnectionString("AzureConnection")!, name: "azure-db")
    .AddSqlServer(builder.Configuration.GetConnectionString("AwsConnection")!, name: "aws-db");

var app = builder.Build();

// Add health check endpoints
app.MapHealthChecks("/health");
app.MapHealthChecks("/api/healthcheck/test-connections", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        var result = new
        {
            azureConnection = report.Entries.ContainsKey("azure-db") && report.Entries["azure-db"].Status == Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy,
            awsConnection = report.Entries.ContainsKey("aws-db") && report.Entries["aws-db"].Status == Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy
        };
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(result));
    }
});

// Initialize databases
using (var scope = app.Services.CreateScope())
{
    try
    {
        var azureContext = scope.ServiceProvider.GetRequiredService<AzureDbContext>();
        var awsContext = scope.ServiceProvider.GetRequiredService<AwsDbContext>();
        
        // Ensure databases are created
        await azureContext.Database.EnsureCreatedAsync();
        await awsContext.Database.EnsureCreatedAsync();
        
        Console.WriteLine("Databases initialized successfully");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Database initialization failed: {ex.Message}");
        // Don't fail startup - let health checks handle this
    }
}

app.MapControllers();

app.Run();
