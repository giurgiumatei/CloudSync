using CloudSync.Core.Services.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CloudSync.KafkaAzureConsumer.Services;

public class AzureConsumerHostedService : BackgroundService
{
    private readonly IKafkaConsumerService _consumerService;
    private readonly ILogger<AzureConsumerHostedService> _logger;

    public AzureConsumerHostedService(
        IKafkaConsumerService consumerService,
        ILogger<AzureConsumerHostedService> logger)
    {
        _consumerService = consumerService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Azure Consumer Hosted Service starting...");

        try
        {
            await _consumerService.StartConsumingAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Azure Consumer Hosted Service cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Azure Consumer Hosted Service encountered an error");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Azure Consumer Hosted Service stopping...");
        
        await _consumerService.StopConsumingAsync();
        await base.StopAsync(cancellationToken);
        
        _logger.LogInformation("Azure Consumer Hosted Service stopped");
    }
} 