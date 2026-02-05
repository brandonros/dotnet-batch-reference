using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace BatchApp.Services;

/// <summary>
/// Low-level SQL execution with built-in connection management and resilience.
/// One instance per parallel worker - not thread-safe, not shared across threads.
/// </summary>
public sealed class SqlExecutor : ISqlExecutor
{
    private readonly SqlConnection _connection;
    private readonly ILogger<SqlExecutor> _logger;
    private readonly ResiliencePipeline _resiliencePipeline;

    private const int DefaultCommandTimeout = 30;
    private const int MaxRetryAttempts = 3;
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(2);

    public SqlExecutor(ISqlConnectionFactory connectionFactory, ILogger<SqlExecutor> logger)
    {
        _connection = connectionFactory.CreateConnection();
        _logger = logger;
        _resiliencePipeline = BuildResiliencePipeline();
    }

    private ResiliencePipeline BuildResiliencePipeline()
    {
        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<SqlException>(IsRetryable),
                MaxRetryAttempts = MaxRetryAttempts,
                DelayGenerator = args =>
                {
                    var delay = TimeSpan.FromMilliseconds(
                        InitialDelay.TotalMilliseconds * Math.Pow(2, args.AttemptNumber));
                    if (delay > MaxDelay) delay = MaxDelay;

                    // Jitter ±25% to prevent thundering herd
                    var jitter = delay.TotalMilliseconds * 0.25 * (Random.Shared.NextDouble() * 2 - 1);
                    return ValueTask.FromResult<TimeSpan?>(delay + TimeSpan.FromMilliseconds(jitter));
                },
                OnRetry = async args =>
                {
                    var sqlEx = args.Outcome.Exception as SqlException;

                    _logger.LogWarning(
                        args.Outcome.Exception,
                        "SQL operation failed (Error {ErrorNumber}), retry {AttemptNumber}/{MaxAttempts} after {Delay}ms",
                        sqlEx?.Number,
                        args.AttemptNumber + 1,
                        MaxRetryAttempts,
                        args.RetryDelay.TotalMilliseconds);

                    if (sqlEx != null && IsConnectionError(sqlEx))
                    {
                        try
                        {
                            await ReconnectAsync(args.Context.CancellationToken);
                        }
                        catch (Exception reconnectEx)
                        {
                            // Reconnect failed - log and continue with retry anyway.
                            // Database might recover during backoff delay.
                            _logger.LogWarning(
                                reconnectEx,
                                "Reconnection attempt failed, will retry operation anyway");
                        }
                    }
                }
            })
            .Build();
    }

    // DATADOG: [Trace] attribute here
    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        await _resiliencePipeline.ExecuteAsync(
            async ct => await _connection.OpenAsync(ct),
            cancellationToken);
    }

    // DATADOG: [Trace] attribute here
    public async Task<int> ExecuteNonQueryAsync(
        string storedProcedure,
        Action<SqlParameterCollection>? configureParameters = null,
        int? commandTimeout = null,
        CancellationToken cancellationToken = default)
    {
        return await _resiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var command = CreateCommand(storedProcedure, commandTimeout);
            configureParameters?.Invoke(command.Parameters);
            return await command.ExecuteNonQueryAsync(ct);
        }, cancellationToken);
    }

    // DATADOG: [Trace] attribute here
    public async Task<T?> ExecuteScalarAsync<T>(
        string storedProcedure,
        Action<SqlParameterCollection>? configureParameters = null,
        int? commandTimeout = null,
        CancellationToken cancellationToken = default)
    {
        return await _resiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var command = CreateCommand(storedProcedure, commandTimeout);
            configureParameters?.Invoke(command.Parameters);
            var result = await command.ExecuteScalarAsync(ct);
            return result == DBNull.Value ? default : (T?)result;
        }, cancellationToken);
    }

    // DATADOG: [Trace] attribute here
    public async Task<T> ExecuteReaderAsync<T>(
        string storedProcedure,
        Func<SqlDataReader, Task<T>> readFunc,
        Action<SqlParameterCollection>? configureParameters = null,
        int? commandTimeout = null,
        CancellationToken cancellationToken = default)
    {
        return await _resiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var command = CreateCommand(storedProcedure, commandTimeout);
            configureParameters?.Invoke(command.Parameters);
            await using var reader = await command.ExecuteReaderAsync(ct);
            return await readFunc(reader);
        }, cancellationToken);
    }

    private SqlCommand CreateCommand(string storedProcedure, int? commandTimeout)
    {
        return new SqlCommand(storedProcedure, _connection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = commandTimeout ?? DefaultCommandTimeout
        };
    }

    private async Task ReconnectAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Attempting to reconnect to database...");
        await _connection.CloseAsync();
        await _connection.OpenAsync(cancellationToken);
        _logger.LogInformation("Successfully reconnected to database");
    }

    #region Error Classification

    private static bool IsRetryable(SqlException ex)
    {
        foreach (SqlError error in ex.Errors)
        {
            if (IsConnectionErrorNumber(error.Number) || IsTransientErrorNumber(error.Number))
                return true;
        }
        return false;
    }

    private static bool IsConnectionError(SqlException ex)
    {
        foreach (SqlError error in ex.Errors)
        {
            if (IsConnectionErrorNumber(error.Number))
                return true;
        }
        return false;
    }

    private static bool IsConnectionErrorNumber(int errorNumber) => errorNumber switch
    {
        -2 => true,     // Command timeout
        -1 => true,     // Network error
        0 => true,      // Connection closed
        53 => true,     // Cannot connect
        233 => true,    // Named pipes error
        4060 => true,   // Cannot open database
        10054 => true,  // Connection forcibly closed
        10060 => true,  // Connection timed out
        40501 => true,  // Azure: Service busy
        40613 => true,  // Azure: Database unavailable
        49918 => true,  // Azure: Too many requests
        49919 => true,  // Azure: Resource limit
        49920 => true,  // Azure: Database limit
        _ => false
    };

    private static bool IsTransientErrorNumber(int errorNumber) => errorNumber switch
    {
        1205 => true,   // Deadlock victim
        1222 => true,   // Lock timeout
        3960 => true,   // Snapshot conflict
        _ => false
    };

    #endregion

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }
}
