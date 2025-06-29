using CloudSync.Core.Configuration;
using CloudSync.Core.Services;
using CloudSync.Core.Services.Interfaces;
using CloudSync.KafkaAwsConsumer.Services;
using CloudSync.Data.Contexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CloudSync.Core.DTOs;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

// Configure configuration sources
builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args);

// Configure logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// Configure settings
builder.Services.Configure<KafkaConfiguration>(builder.Configuration.GetSection("Kafka"));
builder.Services.Configure<AwsEndpointConfiguration>(builder.Configuration.GetSection("AwsEndpoint"));
builder.Services.Configure<ErrorHandlingConfiguration>(builder.Configuration.GetSection("ErrorHandling"));
builder.Services.Configure<RetryConfiguration>(builder.Configuration.GetSection("ErrorHandling:RetryConfiguration"));
builder.Services.Configure<IdempotencyConfiguration>(builder.Configuration.GetSection("Idempotency"));

// Register database context
builder.Services.AddDbContext<AwsDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("AwsConnection")));

// Add health checks
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AwsDbContext>("aws-db", tags: new[] { "database", "aws" });

// Register services
builder.Services.AddSingleton<IErrorClassifier, ErrorClassifier>();
builder.Services.AddSingleton<IRetryService>(sp =>
    new RetryService(
        sp.GetRequiredService<IOptions<RetryConfiguration>>().Value,
        sp.GetRequiredService<ILogger<RetryService>>(),
        sp.GetRequiredService<IErrorClassifier>()));
builder.Services.AddSingleton<IIdempotencyService, InMemoryIdempotencyService>();
builder.Services.AddScoped<IAwsKafkaConsumerService, AwsKafkaConsumerService>();
builder.Services.AddScoped<IKafkaConsumerService, AwsKafkaConsumerService>();

// Register the hosted service
builder.Services.AddHostedService<AwsConsumerHostedService>();

// Build the application
var app = builder.Build();

// Configure health check endpoints
app.MapHealthChecks("/healthcheck");
app.MapHealthChecks("/health");

var logger = app.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("Starting AWS Kafka Consumer Application with enhanced error handling and health checks");

// Configure graceful shutdown
var cancellationTokenSource = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellationTokenSource.Cancel();
};

try
{
    await app.RunAsync(cancellationTokenSource.Token);
}
catch (OperationCanceledException)
{
    logger.LogInformation("AWS Consumer service stopped gracefully.");
}
catch (Exception ex)
{
    logger.LogCritical(ex, "AWS Consumer service failed");
    throw;
}
finally
{
    logger.LogInformation("AWS Kafka Consumer Application stopped");
} 