using Microsoft.EntityFrameworkCore;

namespace KasaMonitor.Data.Repositories;

/// <summary>
/// Retention deletes in batches of <see cref="Size"/> rows, each its own short statement: on SQL Server a single DELETE of
/// more than ~5,000 rows escalates to a table lock held for the whole delete (the writers of the table wait).
/// </summary>
internal static class BatchDelete
{
    public const int Size = 5000;

    /// <param name="ordered">The rows to delete, in a stable order (served by an index), queried again for every batch.</param>
    public static async Task<int> RunAsync<T>(Func<IQueryable<T>> ordered, CancellationToken ct, int size = Size)
    {
        var total = 0;
        while (true)
        {
            var deleted = await ordered().Take(size).ExecuteDeleteAsync(ct);
            total += deleted;
            if (deleted < size) return total;
            await Task.Yield();
        }
    }
}
