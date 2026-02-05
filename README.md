# .NET Batch Application - Database Best Practices

Production-grade .NET batch application pattern for high-throughput database operations using `Microsoft.Data.SqlClient` with stored procedures.

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│  Program.cs (Composition Root)                                  │
│  ├── ISqlConnectionFactory  (Singleton)  - connection string    │
│  ├── ISqlExecutor           (Scoped)     - owns connection      │
│  ├── ISampleRepository      (Scoped)     - stored proc contracts│
│  └── BatchOrchestrator      (Singleton)  - parallel processing  │
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

| Layer | Class | Lifetime | Responsibility |
|-------|-------|----------|----------------|
| **Factory** | `SqlConnectionFactory` | Singleton | Creates `SqlConnection` instances from connection string |
| **Executor** | `SqlExecutor` | Scoped | Connection lifecycle, retries, reconnection, command execution |
| **Repository** | `SampleRepository` | Scoped | Stored procedure contracts, parameter binding |
| **Orchestrator** | `BatchOrchestrator` | Singleton | File processing, chunking, parallelism, scope management |

## Key Design Decisions

### Why Scoped (not Transient)?

```csharp
builder.Services.AddScoped<ISqlExecutor, SqlExecutor>();
builder.Services.AddScoped<ISampleRepository, SampleRepository>();
```

- **Scoped**: Same instance within a DI scope. Repository gets the *same* `SqlExecutor` that was opened.
- **Transient**: New instance every resolution. Repository would get a *different* `SqlExecutor` with a closed connection.

### Connection Lifetime

- **1 connection per chunk** (1000 rows)
- Connection opened at chunk start via `OpenAsync()`
- Same connection reused for all rows in the chunk
- Connection disposed (returned to pool) when scope ends
- Up to 24 connections active concurrently

### Connection Pooling

ADO.NET manages the connection pool automatically. Connections are pooled by connection string.

Recommended connection string settings:
```
Server=...;Database=...;Max Pool Size=30;Min Pool Size=0;Connect Timeout=15;
```

- `Max Pool Size=30`: Matches parallelism (24) + buffer for retry overlap
- `Connect Timeout=15`: Seconds to wait for connection from pool

### Resilience (Polly)

```
Attempt 1: SqlException (transient/connection error)
  → OnRetry: attempt reconnect (failure caught, logged)
    → Backoff: 100ms * 2^attempt + jitter (±25%)
      → Attempt 2
        → Attempt 3
          → Final failure thrown to caller
```

Retryable errors:
- **Connection**: timeout, network error, connection closed, Azure throttling
- **Transient**: deadlock victim, lock timeout, snapshot conflict

### Transaction Boundary

Each row commits independently (no batch transaction). This matches the requirement for sequential per-row commits.

## Usage

```bash
dotnet run -- input.txt
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

## Adding a New Repository

1. Define the interface:
```csharp
public interface IOrderRepository
{
    Task ProcessOrderAsync(int orderId, CancellationToken ct = default);
}
```

2. Implement using `ISqlExecutor`:
```csharp
public sealed class OrderRepository : IOrderRepository
{
    private readonly ISqlExecutor _executor;

    public OrderRepository(ISqlExecutor executor) => _executor = executor;

    public async Task ProcessOrderAsync(int orderId, CancellationToken ct = default)
    {
        await _executor.ExecuteNonQueryAsync(
            "[dbo].[ProcessOrder]",
            p => p.Add("@OrderId", SqlDbType.Int).Value = orderId,
            commandTimeout: 30,
            ct);
    }
}
```

3. Register as Scoped:
```csharp
builder.Services.AddScoped<IOrderRepository, OrderRepository>();
```

## Observability

### Tracing (DataDog)

Add `[Trace]` attributes to:
- `SqlExecutor.OpenAsync`
- `SqlExecutor.ExecuteNonQueryAsync`
- `SqlExecutor.ExecuteScalarAsync`
- `SqlExecutor.ExecuteReaderAsync`
- Repository methods

### Logging

Uses `ILogger<T>`. Key log events:
- Batch start/complete with row counts
- Retry attempts with error numbers and delays
- Reconnection attempts (success/failure)

## Error Classification

| Error Number | Type | Description |
|--------------|------|-------------|
| -2 | Connection | Command timeout |
| -1 | Connection | Network error |
| 53 | Connection | Cannot connect |
| 1205 | Transient | Deadlock victim |
| 1222 | Transient | Lock timeout |
| 40613 | Connection | Azure DB unavailable |

See `SqlExecutor.IsConnectionErrorNumber()` and `IsTransientErrorNumber()` for full list.
