using BatchApp.Data;
using BatchApp.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BatchApp.Services;

public sealed class BatchOrchestrator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BatchOrchestrator> _logger;

    private const int ChunkSize = 1000;
    private const int MaxDegreeOfParallelism = 24;

    public BatchOrchestrator(IServiceScopeFactory scopeFactory, ILogger<BatchOrchestrator> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task ProcessFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting batch processing of {FilePath}", filePath);

        var allRows = await File.ReadAllLinesAsync(filePath, cancellationToken);
        var chunks = allRows.Chunk(ChunkSize);

        await Parallel.ForEachAsync(
            chunks,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxDegreeOfParallelism,
                CancellationToken = cancellationToken
            },
            async (chunk, ct) =>
            {
                await using var scope = _scopeFactory.CreateAsyncScope();

                // Get executors and open connections for this chunk
                var writeExecutor = scope.ServiceProvider.GetRequiredKeyedService<ISqlExecutor>("default");
                var readExecutor = scope.ServiceProvider.GetRequiredKeyedService<ISqlExecutor>("readonly");
                await Task.WhenAll(writeExecutor.OpenAsync(ct), readExecutor.OpenAsync(ct));

                // Get repository (uses the same executor instances via DI scope)
                var repository = scope.ServiceProvider.GetRequiredService<ISampleRepository>();

                foreach (var row in chunk)
                {
                    await repository.DoSomethingAsync(row, ct);
                }
            });

        _logger.LogInformation("Batch processing completed. Total rows: {TotalRows}", allRows.Length);
    }
}
