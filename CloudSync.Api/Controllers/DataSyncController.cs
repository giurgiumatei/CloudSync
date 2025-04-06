using CloudSync.Core.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace CloudSync.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DataSyncController : ControllerBase
{
    private readonly ISyncService _syncService;

    public DataSyncController(ISyncService syncService)
    {
        _syncService = syncService;
    }

    [HttpPost]
    public async Task<IActionResult> SyncData([FromBody] string data)
    {
        var result = await _syncService.SyncDataAsync(data);
        return result ? Ok("Data synchronized.") : StatusCode(500, "Sync failed.");
    }
}
