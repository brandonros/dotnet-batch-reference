# BatchApp.Data

Reusable database infrastructure library for .NET batch applications using `Microsoft.Data.SqlClient` with stored procedures.

## Installation

Add a project reference:
```xml
<ProjectReference Include="..\BatchApp.Data\BatchApp.Data.csproj" />
```

**Dependencies**: `Microsoft.Data.SqlClient`, `Polly.Core`, `Microsoft.Extensions.Logging.Abstractions`

**Namespace**: `BatchApp.Data`

## Components

| Class | Purpose |
|-------|---------|
| `ISqlConnectionFactory` | Creates `SqlConnection` instances from connection string |
| `SqlConnectionFactory` | Implementation - holds connection string, creates connections |
| `ISqlExecutor` | Interface for executing stored procedures with resilience |
| `SqlExecutor` | Implementation - connection lifecycle, Polly retries, reconnection |

## Quick Start

```csharp
using BatchApp.Data;

// 1. Register services
builder.Services.AddSingleton<ISqlConnectionFactory>(
    new SqlConnectionFactory(connectionString));
builder.Services.AddScoped<ISqlExecutor, SqlExecutor>();

// 2. Use in parallel processing
await Parallel.ForEachAsync(items, async (item, ct) =>
{
    await using var scope = scopeFactory.CreateAsyncScope();
    var executor = scope.ServiceProvider.GetRequiredService<ISqlExecutor>();
    await executor.OpenAsync(ct);

    await executor.ExecuteNonQueryAsync(
        "[dbo].[ProcessItem]",
        p => p.Add("@ItemId", SqlDbType.Int).Value = item.Id,
        commandTimeout: 30,
        ct);
});
```

## DI Lifetime: Why Scoped?

```csharp
builder.Services.AddScoped<ISqlExecutor, SqlExecutor>();
```

| Lifetime | Behavior | Result |
|----------|----------|--------|
| **Scoped** | Same instance within a DI scope | Repository gets the *same* `SqlExecutor` that was opened ✓ |
| Transient | New instance every resolution | Repository gets a *different* `SqlExecutor` with closed connection ✗ |

**Critical**: If you use Transient, repositories will get executors with unopened connections.

## ISqlExecutor API

```csharp
public interface ISqlExecutor : IAsyncDisposable
{
    // Open connection (with retry)
    Task OpenAsync(CancellationToken ct = default);

    // Execute INSERT/UPDATE/DELETE or stored proc with no result
    Task<int> ExecuteNonQueryAsync(
        string storedProcedure,
        Action<SqlParameterCollection>? configureParameters = null,
        int? commandTimeout = null,
        CancellationToken ct = default);

    // Execute and return scalar value
    Task<T?> ExecuteScalarAsync<T>(
        string storedProcedure,
        Action<SqlParameterCollection>? configureParameters = null,
        int? commandTimeout = null,
        CancellationToken ct = default);

    // Execute and process results via reader
    Task<T> ExecuteReaderAsync<T>(
        string storedProcedure,
        Func<SqlDataReader, Task<T>> readFunc,
        Action<SqlParameterCollection>? configureParameters = null,
        int? commandTimeout = null,
        CancellationToken ct = default);
}
```

## Parameter Binding

Always use explicit `SqlDbType` to prevent implicit conversion bugs:

```csharp
await executor.ExecuteNonQueryAsync(
    "[dbo].[UpdateOrder]",
    p =>
    {
        p.Add("@OrderId", SqlDbType.Int).Value = orderId;
        p.Add("@Status", SqlDbType.NVarChar, 50).Value = status;
        p.Add("@Amount", SqlDbType.Decimal).Value = amount;
        p.Add("@UpdatedAt", SqlDbType.DateTime2).Value = DateTime.UtcNow;
    },
    commandTimeout: 30,
    cancellationToken);
```

## Connection Pooling

ADO.NET manages the connection pool automatically. Connections are pooled by connection string.

**Recommended connection string settings**:
```
Server=...;Database=...;Max Pool Size=30;Min Pool Size=0;Connect Timeout=15;
```

| Setting | Recommendation | Reason |
|---------|----------------|--------|
| `Max Pool Size` | Match parallelism + buffer (e.g., 30 for 24 workers) | Prevents pool exhaustion |
| `Min Pool Size` | 0 | Don't pre-allocate unused connections |
| `Connect Timeout` | 15 | Seconds to wait for connection from pool |

## Resilience (Polly)

Built-in retry with exponential backoff and jitter:

```
Attempt 1: SqlException (transient/connection error)
  → OnRetry: attempt reconnect (failure caught, logged)
    → Backoff: 100ms * 2^attempt + jitter (±25%)
      → Attempt 2
        → Backoff: 200ms + jitter
          → Attempt 3
            → Final failure thrown to caller
```

**Configuration** (in SqlExecutor):
- `MaxRetryAttempts`: 3
- `InitialDelay`: 100ms
- `MaxDelay`: 2s
- Jitter: ±25% to prevent thundering herd

## Error Classification

### Connection Errors (trigger reconnect)

| Error Number | Description |
|--------------|-------------|
| -2 | Command timeout |
| -1 | Network error |
| 0 | Connection closed |
| 53 | Cannot connect |
| 233 | Named pipes error |
| 4060 | Cannot open database |
| 10054 | Connection forcibly closed |
| 10060 | Connection timed out |
| 40501 | Azure: Service busy |
| 40613 | Azure: Database unavailable |
| 49918 | Azure: Too many requests |
| 49919 | Azure: Resource limit |
| 49920 | Azure: Database limit |

### Transient Errors (retry only, no reconnect)

| Error Number | Description |
|--------------|-------------|
| 1205 | Deadlock victim |
| 1222 | Lock timeout |
| 3960 | Snapshot conflict |

## Connection Lifetime Pattern

```
┌─────────────────────────────────────────┐
│  DI Scope                               │
│  ┌─────────────────────────────────┐    │
│  │ SqlExecutor                     │    │
│  │  └── SqlConnection (1, reused)  │    │
│  │  └── ResiliencePipeline (Polly) │    │
│  └─────────────────────────────────┘    │
│                                         │
│  1. Scope created                       │
│  2. Executor resolved (Scoped)          │
│  3. OpenAsync() called                  │
│  4. Multiple operations on same conn    │
│  5. Scope disposed → connection to pool │
└─────────────────────────────────────────┘
```

- **1 connection per scope**
- Connection opened once via `OpenAsync()`
- Reused for all operations in that scope
- Returned to ADO.NET pool when scope disposes
- `IAsyncDisposable` handled automatically by DI

## Repository Pattern

Create repositories that depend on `ISqlExecutor`:

```csharp
using BatchApp.Data;

public interface IOrderRepository
{
    Task ProcessOrderAsync(int orderId, CancellationToken ct = default);
    Task<Order?> GetOrderAsync(int orderId, CancellationToken ct = default);
}

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

    public async Task<Order?> GetOrderAsync(int orderId, CancellationToken ct = default)
    {
        return await _executor.ExecuteReaderAsync(
            "[dbo].[GetOrder]",
            async reader =>
            {
                if (await reader.ReadAsync(ct))
                {
                    return new Order(
                        Id: reader.GetInt32(reader.GetOrdinal("Id")),
                        Status: reader.GetString(reader.GetOrdinal("Status")),
                        Amount: reader.GetDecimal(reader.GetOrdinal("Amount"))
                    );
                }
                return null;
            },
            p => p.Add("@OrderId", SqlDbType.Int).Value = orderId,
            commandTimeout: 30,
            ct);
    }
}
```

Register as Scoped (same lifetime as executor):
```csharp
builder.Services.AddScoped<IOrderRepository, OrderRepository>();
```

## Observability

### Tracing (DataDog)

Add `[Trace]` attributes to these methods:
- `SqlExecutor.OpenAsync`
- `SqlExecutor.ExecuteNonQueryAsync`
- `SqlExecutor.ExecuteScalarAsync`
- `SqlExecutor.ExecuteReaderAsync`

### Logging

Uses `ILogger<SqlExecutor>`. Key log events:
- Retry attempts with error numbers and delays
- Reconnection attempts (success/failure)

## Thread Safety

`SqlExecutor` is **not thread-safe**. Each parallel worker must have its own instance via DI scopes:

```csharp
// ✓ Correct: each parallel iteration gets its own scope/executor
await Parallel.ForEachAsync(items, async (item, ct) =>
{
    await using var scope = scopeFactory.CreateAsyncScope();
    var executor = scope.ServiceProvider.GetRequiredService<ISqlExecutor>();
    // ...
});

// ✗ Wrong: sharing executor across threads
var executor = serviceProvider.GetRequiredService<ISqlExecutor>();
await Parallel.ForEachAsync(items, async (item, ct) =>
{
    await executor.ExecuteNonQueryAsync(...); // Race condition!
});
```
