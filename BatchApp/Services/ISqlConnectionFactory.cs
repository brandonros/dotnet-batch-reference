using Microsoft.Data.SqlClient;

namespace BatchApp.Services;

public interface ISqlConnectionFactory
{
    SqlConnection CreateConnection();
}
