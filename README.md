# .NET Batch Application - Database Best Practices

Production-grade .NET batch application pattern for high-throughput database operations using `Microsoft.Data.SqlClient` with stored procedures.

## Project Structure

```
BatchApp.sln
├── BatchApp.Data/          # Reusable library (see BatchApp.Data/README.md)
│   └── Services/
│       ├── ISqlConnectionFactory.cs
│       ├── SqlConnectionFactory.cs
│       ├── ISqlExecutor.cs
│       └── SqlExecutor.cs
└── BatchApp/               # Sample console application
    ├── Program.cs
    ├── Services/
    │   └── BatchOrchestrator.cs
    └── Repositories/
        ├── ISampleRepository.cs
        └── SampleRepository.cs
```

## BatchApp.Data (Reusable Library)

See **[BatchApp.Data/README.md](BatchApp.Data/README.md)** for full documentation.

Core database infrastructure:
- `SqlConnectionFactory` - Creates connections from connection string
- `SqlExecutor` - Connection lifecycle, Polly resilience, stored proc execution

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│  Program.cs (Composition Root)                                  │
│  ├── ISqlConnectionFactory  (Singleton)  - from BatchApp.Data   │
│  ├── ISqlExecutor           (Scoped)     - from BatchApp.Data   │
│  ├── ISampleRepository      (Scoped)     - app-specific         │
│  └── BatchOrchestrator      (Singleton)  - app-specific         │
└─────────────────────────────────────────────────────────────────┘

Per Parallel Worker (24 concurrent):
┌─────────────────────────────────────────┐
│  DI Scope (1 per chunk of 1000 rows)    │
│  ┌─────────────────────────────────┐    │
│  │ SqlExecutor                     │    │
│  │  └── SqlConnection (1, reused)  │    │
│  │  └── ResiliencePipeline (Polly) │    │
│  └─────────────────────────────────┘    │
│  ┌─────────────────────────────────┐    │
│  │ Repository                      │    │
│  │  └── uses same SqlExecutor      │    │
│  └─────────────────────────────────┘    │
│                                         │
│  Processes 1000 rows sequentially       │
│  Scope disposes → connection to pool    │
└─────────────────────────────────────────┘
```

## Layer Responsibilities

| Layer | Class | Lifetime | Project |
|-------|-------|----------|---------|
| **Factory** | `SqlConnectionFactory` | Singleton | BatchApp.Data |
| **Executor** | `SqlExecutor` | Scoped | BatchApp.Data |
| **Repository** | `SampleRepository` | Scoped | BatchApp |
| **Orchestrator** | `BatchOrchestrator` | Singleton | BatchApp |

## Usage

```bash
dotnet run --project BatchApp -- input.txt
```

### Configuration

`appsettings.json`:
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=localhost;Database=MyDb;Trusted_Connection=True;TrustServerCertificate=True;Max Pool Size=30;"
  }
}
```

## Sample App: BatchOrchestrator

The sample app demonstrates parallel file processing:

```csharp
await Parallel.ForEachAsync(
    chunks,
    new ParallelOptions { MaxDegreeOfParallelism = 24 },
    async (chunk, ct) =>
    {
        await using var scope = _scopeFactory.CreateAsyncScope();

        var executor = scope.ServiceProvider.GetRequiredService<ISqlExecutor>();
        await executor.OpenAsync(ct);

        var repository = scope.ServiceProvider.GetRequiredService<ISampleRepository>();

        foreach (var row in chunk)
        {
            await repository.DoSomethingAsync(row, ct);
        }
    });
```

Key patterns:
- **1 scope per chunk** (1000 rows)
- **1 connection per scope** (opened once, reused)
- **Scoped registration** ensures repository shares the executor's connection
- **Sequential commits** - each row commits independently

## Adding a Repository

1. Define interface and implementation using `ISqlExecutor`
2. Register as **Scoped** (same lifetime as executor)
3. Resolve within a DI scope

See [BatchApp.Data/README.md](BatchApp.Data/README.md#repository-pattern) for full example.
