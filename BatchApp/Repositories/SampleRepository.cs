using System.Data;
using BatchApp.Services;
using Microsoft.Data.SqlClient;

namespace BatchApp.Repositories;

/// <summary>
/// Repository implementation for sample batch operations.
/// All resilience (retries, reconnection) is handled by ISqlExecutor.
/// This layer only defines the stored procedure contracts.
/// </summary>
public sealed class SampleRepository : ISampleRepository
{
    private readonly ISqlExecutor _executor;

    public SampleRepository(ISqlExecutor executor)
    {
        _executor = executor;
    }

    // DATADOG: [Trace] attribute here
    public async Task DoSomethingAsync(string someValue, CancellationToken cancellationToken = default)
    {
        await _executor.ExecuteNonQueryAsync(
            "[dbo].[DoSomething]",
            parameters =>
            {
                // Explicit SqlDbType prevents implicit conversion bugs
                parameters.Add("@SomeValue", SqlDbType.NVarChar, 100).Value = someValue;
            },
            commandTimeout: 30,
            cancellationToken);
    }

    // DATADOG: [Trace] attribute here
    public async Task<int> GetSomethingCountAsync(int categoryId, CancellationToken cancellationToken = default)
    {
        var result = await _executor.ExecuteScalarAsync<int>(
            "[dbo].[GetSomethingCount]",
            parameters =>
            {
                parameters.Add("@CategoryId", SqlDbType.Int).Value = categoryId;
            },
            commandTimeout: 30,
            cancellationToken);

        return result;
    }

    // DATADOG: [Trace] attribute here
    public async Task<SampleRecord?> GetSomethingByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _executor.ExecuteReaderAsync(
            "[dbo].[GetSomethingById]",
            async reader =>
            {
                if (await reader.ReadAsync(cancellationToken))
                {
                    return new SampleRecord(
                        Id: reader.GetInt32(reader.GetOrdinal("Id")),
                        Name: reader.GetString(reader.GetOrdinal("Name")),
                        CreatedAt: reader.GetDateTime(reader.GetOrdinal("CreatedAt"))
                    );
                }
                return null;
            },
            parameters =>
            {
                parameters.Add("@Id", SqlDbType.Int).Value = id;
            },
            commandTimeout: 30,
            cancellationToken);
    }
}
