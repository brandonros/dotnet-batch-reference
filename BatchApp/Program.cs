using BatchApp.Data;
using BatchApp.Repositories;
using BatchApp.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// Register services
var defaultConnectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
var readOnlyConnectionString = builder.Configuration.GetConnectionString("ReadOnlyConnection")
    ?? throw new InvalidOperationException("Connection string 'ReadOnlyConnection' not found.");

// Infrastructure layer (connection management) - keyed services for read/write separation
builder.Services.AddKeyedSingleton<ISqlConnectionFactory>("default",
    new SqlConnectionFactory(defaultConnectionString));
builder.Services.AddKeyedSingleton<ISqlConnectionFactory>("readonly",
    new SqlConnectionFactory(readOnlyConnectionString));

builder.Services.AddKeyedScoped<ISqlExecutor>("default", (sp, key) =>
    new SqlExecutor(
        sp.GetRequiredKeyedService<ISqlConnectionFactory>("default"),
        sp.GetRequiredService<ILogger<SqlExecutor>>()));

builder.Services.AddKeyedScoped<ISqlExecutor>("readonly", (sp, key) =>
    new SqlExecutor(
        sp.GetRequiredKeyedService<ISqlConnectionFactory>("readonly"),
        sp.GetRequiredService<ILogger<SqlExecutor>>()));

// Repository layer (business logic / stored proc contracts)
// Scoped: same executor instance shared within each parallel worker's scope
builder.Services.AddScoped<ISampleRepository, SampleRepository>();

// Orchestration
builder.Services.AddSingleton<BatchOrchestrator>();

var host = builder.Build();

// Run the batch
var orchestrator = host.Services.GetRequiredService<BatchOrchestrator>();
var logger = host.Services.GetRequiredService<ILogger<Program>>();

try
{
    var filePath = args.Length > 0 ? args[0] : "input.txt";
    await orchestrator.ProcessFileAsync(filePath);
    logger.LogInformation("Batch completed successfully");
}
catch (Exception ex)
{
    logger.LogError(ex, "Batch failed");
    throw;
}
