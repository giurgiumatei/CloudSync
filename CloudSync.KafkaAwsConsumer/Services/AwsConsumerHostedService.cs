using CloudSync.Core.Services.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CloudSync.KafkaAwsConsumer.Services;

public class AwsConsumerHostedService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AwsConsumerHostedService> _logger;

    public AwsConsumerHostedService(
        IServiceProvider serviceProvider,
        ILogger<AwsConsumerHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AWS Consumer Hosted Service starting...");

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var consumerService = scope.ServiceProvider.GetRequiredService<IKafkaConsumerService>();
            await consumerService.StartConsumingAsync(stoppingToken);
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
        
        using var scope = _serviceProvider.CreateScope();
        var consumerService = scope.ServiceProvider.GetRequiredService<IKafkaConsumerService>();
        await consumerService.StopConsumingAsync();
        await base.StopAsync(cancellationToken);
        
        _logger.LogInformation("AWS Consumer Hosted Service stopped");
    }
} 