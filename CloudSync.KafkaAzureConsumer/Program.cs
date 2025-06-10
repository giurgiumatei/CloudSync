using CloudSync.Core.Configuration;
using CloudSync.Core.Services;
using CloudSync.Core.Services.Interfaces;
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
builder.Services.AddLogging(configure => 
{
    configure.AddConsole();
    configure.AddConfiguration(builder.Configuration.GetSection("Logging"));
});

// Configure Kafka settings
builder.Services.Configure<KafkaConfiguration>(builder.Configuration.GetSection("Kafka"));
builder.Services.Configure<AzureEndpointConfiguration>(builder.Configuration.GetSection("AzureEndpoint"));

// Register HTTP client
builder.Services.AddHttpClient<AzureKafkaConsumerService>();

// Register services
builder.Services.AddSingleton<IKafkaProducerService, KafkaProducerService>();
builder.Services.AddScoped<IKafkaConsumerService, AzureKafkaConsumerService>();

// Register the hosted service
builder.Services.AddHostedService<AzureConsumerHostedService>();

// Build and run the host
var host = builder.Build();

var logger = host.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("Starting Azure Kafka Consumer Application");

try
{
    await host.RunAsync();
}
catch (Exception ex)
{
    logger.LogCritical(ex, "Azure Kafka Consumer Application terminated unexpectedly");
    throw;
}
finally
{
    logger.LogInformation("Azure Kafka Consumer Application stopped");
} 