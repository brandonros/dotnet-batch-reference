using Microsoft.Data.SqlClient;

namespace BatchApp.Data;

public sealed class SqlConnectionFactory : ISqlConnectionFactory
{
    private readonly string _connectionString;

    public SqlConnectionFactory(string connectionString)
    {
        // TIMEOUT: Connection timeout is set in the connection string itself:
        // "Server=...;Connect Timeout=15;..." (seconds to wait for connection from pool)
        // This is separate from CommandTimeout which controls query execution time
        _connectionString = connectionString;
    }

    public SqlConnection CreateConnection()
    {
        return new SqlConnection(_connectionString);
    }
}
