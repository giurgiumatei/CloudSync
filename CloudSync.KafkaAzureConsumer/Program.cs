using CloudSync.Core.Configuration;
using CloudSync.Core.Services;
using CloudSync.Core.Services.Interfaces;
using CloudSync.KafkaAzureConsumer.Configuration;
using CloudSync.KafkaAzureConsumer.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

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
builder.Services.Configure<AzureEndpointConfiguration>(builder.Configuration.GetSection("AzureEndpoint"));
builder.Services.Configure<ErrorHandlingConfiguration>(builder.Configuration.GetSection("ErrorHandling"));
builder.Services.Configure<RetryConfiguration>(builder.Configuration.GetSection("ErrorHandling:RetryConfiguration"));
builder.Services.Configure<IdempotencyConfiguration>(builder.Configuration.GetSection("Idempotency"));

// Register HTTP client
builder.Services.AddHttpClient<AzureKafkaConsumerService>();

// Register services
builder.Services.AddSingleton<IRetryService, RetryService>();
builder.Services.AddSingleton<IErrorClassifier, ErrorClassifier>();
builder.Services.AddSingleton<IIdempotencyService, InMemoryIdempotencyService>();
builder.Services.AddSingleton<IAzureKafkaConsumerService, AzureKafkaConsumerService>();

// Register the hosted service
builder.Services.AddHostedService<AzureConsumerHostedService>();

// Build and run the host
var host = builder.Build();

var logger = host.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("Starting Azure Kafka Consumer Application with enhanced error handling");

// Configure graceful shutdown
var cancellationTokenSource = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellationTokenSource.Cancel();
};

try
{
    await host.RunAsync(cancellationTokenSource.Token);
}
catch (OperationCanceledException)
{
    logger.LogInformation("Azure Consumer service stopped gracefully.");
}
catch (Exception ex)
{
    logger.LogCritical(ex, "Azure Consumer service failed");
    throw;
}
finally
{
    logger.LogInformation("Azure Kafka Consumer Application stopped");
} 