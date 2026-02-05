using Microsoft.Data.SqlClient;

namespace BatchApp.Data;

public interface ISqlConnectionFactory
{
    SqlConnection CreateConnection();
}
