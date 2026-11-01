namespace KasaMonitor.Workers.Nilm;

/// <summary>Runs CPU-heavy NILM work (nightly clustering, training) on its own below-normal-priority thread.</summary>
public static class LowPriority
{
    public static Task<T> Run<T>(Func<T> work, string name)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = name,
        };
        thread.Start();
        return tcs.Task;
    }
}
