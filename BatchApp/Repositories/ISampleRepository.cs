namespace BatchApp.Repositories;

/// <summary>
/// Repository for sample batch operations.
/// Replace with your actual domain repository interface.
/// </summary>
public interface ISampleRepository
{
    /// <summary>
    /// Executes [dbo].[DoSomething] stored procedure.
    /// </summary>
    Task DoSomethingAsync(string someValue, CancellationToken cancellationToken = default);

    /// <summary>
    /// Example: Retrieves a count from [dbo].[GetSomethingCount].
    /// </summary>
    Task<int> GetSomethingCountAsync(int categoryId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Example: Retrieves a record from [dbo].[GetSomethingById].
    /// </summary>
    Task<SampleRecord?> GetSomethingByIdAsync(int id, CancellationToken cancellationToken = default);
}

/// <summary>
/// Example record returned by a stored procedure.
/// </summary>
public sealed record SampleRecord(int Id, string Name, DateTime CreatedAt);
