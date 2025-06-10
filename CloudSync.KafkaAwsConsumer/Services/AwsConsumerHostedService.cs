using CloudSync.Core.Services.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CloudSync.KafkaAwsConsumer.Services;

public class AwsConsumerHostedService : BackgroundService
{
    private readonly IKafkaConsumerService _consumerService;
    private readonly ILogger<AwsConsumerHostedService> _logger;

    public AwsConsumerHostedService(
        IKafkaConsumerService consumerService,
        ILogger<AwsConsumerHostedService> logger)
    {
        _consumerService = consumerService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AWS Consumer Hosted Service starting...");

        try
        {
            await _consumerService.StartConsumingAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("AWS Consumer Hosted Service cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AWS Consumer Hosted Service encountered an error");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("AWS Consumer Hosted Service stopping...");
        
        await _consumerService.StopConsumingAsync();
        await base.StopAsync(cancellationToken);
        
        _logger.LogInformation("AWS Consumer Hosted Service stopped");
    }
} 