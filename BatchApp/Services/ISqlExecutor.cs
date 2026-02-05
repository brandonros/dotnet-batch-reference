using System.Data;
using Microsoft.Data.SqlClient;

namespace BatchApp.Services;

/// <summary>
/// Low-level SQL execution with built-in connection management and resilience.
/// Handles connection lifecycle, reconnection on failure, and transient retries.
/// </summary>
public interface ISqlExecutor : IAsyncDisposable
{
    /// <summary>
    /// Opens the connection (with retry on transient failures).
    /// </summary>
    Task OpenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a non-query command (INSERT, UPDATE, DELETE, or stored proc with no result).
    /// Includes automatic retry and reconnection on transient/connection failures.
    /// </summary>
    Task<int> ExecuteNonQueryAsync(
        string storedProcedure,
        Action<SqlParameterCollection>? configureParameters = null,
        int? commandTimeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a command and returns a scalar value.
    /// Includes automatic retry and reconnection on transient/connection failures.
    /// </summary>
    Task<T?> ExecuteScalarAsync<T>(
        string storedProcedure,
        Action<SqlParameterCollection>? configureParameters = null,
        int? commandTimeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a command and processes results via a reader callback.
    /// Includes automatic retry and reconnection on transient/connection failures.
    /// </summary>
    Task<T> ExecuteReaderAsync<T>(
        string storedProcedure,
        Func<SqlDataReader, Task<T>> readFunc,
        Action<SqlParameterCollection>? configureParameters = null,
        int? commandTimeout = null,
        CancellationToken cancellationToken = default);
}
