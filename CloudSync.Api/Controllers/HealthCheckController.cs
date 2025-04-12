using CloudSync.Data.Contexts;
using Microsoft.AspNetCore.Mvc;

namespace CloudSync.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HealthCheckController(AzureDbContext azureContext, AwsDbContext awsContext) : ControllerBase
{
    [HttpGet("test-connections")]
    public async Task<IActionResult> TestConnections()
    {
        bool azureConnected = await azureContext.Database.CanConnectAsync();
        bool awsConnected = await awsContext.Database.CanConnectAsync();

        return Ok(new
        {
            AzureConnection = azureConnected,
            AWSConnection = awsConnected
        });
    }
}
