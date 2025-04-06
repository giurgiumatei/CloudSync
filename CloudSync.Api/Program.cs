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

builder.Services.AddScoped<ISyncService, SyncService>();

var app = builder.Build();

app.MapControllers();

app.Run();
