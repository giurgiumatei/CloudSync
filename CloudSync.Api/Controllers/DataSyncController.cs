using CloudSync.Core.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace CloudSync.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DataSyncController : ControllerBase
{
    private readonly IKafkaProducerService _kafkaProducerService;
    private readonly ILogger<DataSyncController> _logger;

    public DataSyncController(IKafkaProducerService kafkaProducerService, ILogger<DataSyncController> logger)
    {
        _kafkaProducerService = kafkaProducerService;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> SyncData([FromBody] string data)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(data))
            {
                return BadRequest("Data cannot be null or empty.");
            }

            var result = await _kafkaProducerService.PublishDataSyncMessageAsync(data);
            
            if (result)
            {
                _logger.LogDebug("Data message published to Kafka successfully");
                return Ok("Data message queued for synchronization.");
            }
            else
            {
                _logger.LogWarning("Failed to publish data message to Kafka");
                return StatusCode(500, "Failed to queue data for synchronization.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while publishing data to Kafka");
            return StatusCode(500, "Internal server error occurred.");
        }
    }
}
