namespace AsyncSharp.Benchmarks;

internal static class BenchmarkTaskAssertions
{
    public static void EnsurePending(Task waiter)
    {
        if (waiter.IsCompleted)
        {
            throw new InvalidOperationException(
                "Benchmark setup expected every waiter to remain pending.");
        }
    }

    public static void EnsureAllPending(Task[] waiters)
    {
        foreach (var waiter in waiters)
        {
            EnsurePending(waiter);
        }
    }

    public static async Task AwaitCanceledAsync(Task[] waiters)
    {
        try
        {
            await Task.WhenAll(waiters).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        foreach (var waiter in waiters)
        {
            if (!waiter.IsCanceled)
            {
                throw new InvalidOperationException(
                    "Cancellation benchmark expected every waiter to be canceled.");
            }
        }
    }
}
