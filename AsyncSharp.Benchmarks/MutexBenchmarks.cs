using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Order;
using NitoLock = Nito.AsyncEx.AsyncLock;
using SharpMutex = global::AsyncSharp.AsyncMutex;
using VsSemaphore = Microsoft.VisualStudio.Threading.AsyncSemaphore;

namespace AsyncSharp.Benchmarks;

[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class MutexUncontendedBenchmarks
{
    private const int Operations = 256;

    private readonly object _monitor = new();
    private readonly SemaphoreSlim _bcl = new(1, 1);
    private readonly NitoLock _nito = new();
    private readonly SharpMutex _sharp = new();
    private readonly VsSemaphore _vs = new(1);

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("Sync core")]
    public void Bcl_Monitor()
    {
        for (var i = 0; i < Operations; ++i)
        {
            lock (_monitor)
            {
            }
        }
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("Sync core")]
    public void Bcl_SemaphoreSlim()
    {
        for (var i = 0; i < Operations; ++i)
        {
            _bcl.Wait();
            _bcl.Release();
        }
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("Sync core")]
    public void AsyncSharp_Sync()
    {
        for (var i = 0; i < Operations; ++i)
        {
            _sharp.Lock();
            _sharp.Unlock();
        }
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("Sync lease")]
    public void Nito_SyncLease()
    {
        for (var i = 0; i < Operations; ++i)
        {
            using (_nito.Lock())
            {
            }
        }
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("Sync lease")]
    public void AsyncSharp_SyncLease()
    {
        for (var i = 0; i < Operations; ++i)
        {
            using (_sharp.LockAndUnlock())
            {
            }
        }
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("Async core")]
    public async Task Bcl_Async()
    {
        for (var i = 0; i < Operations; ++i)
        {
            await _bcl.WaitAsync().ConfigureAwait(false);
            _bcl.Release();
        }
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("Async core")]
    public async Task AsyncSharp_Async()
    {
        for (var i = 0; i < Operations; ++i)
        {
            await _sharp.LockAsync().ConfigureAwait(false);
            _sharp.Unlock();
        }
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("Async lease")]
    public async Task Nito_AsyncLease()
    {
        for (var i = 0; i < Operations; ++i)
        {
            using (await _nito.LockAsync())
            {
            }
        }
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("Async lease")]
    public async Task AsyncSharp_AsyncLease()
    {
        for (var i = 0; i < Operations; ++i)
        {
            using (await _sharp.LockAndUnlockAsync().ConfigureAwait(false))
            {
            }
        }
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("Async lease")]
    public async Task VsThreading_AsyncLease()
    {
        for (var i = 0; i < Operations; ++i)
        {
            using (await _vs.EnterAsync())
            {
            }
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _bcl.Dispose();
        _sharp.Dispose();
        _vs.Dispose();
    }
}

[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class MutexContentionBenchmarks
{
    private readonly SemaphoreSlim _bcl = new(1, 1);
    private readonly NitoLock _nito = new();
    private readonly SharpMutex _sharp = new();
    private readonly VsSemaphore _vs = new(1);

    [Params(16, 64)]
    public int WaiterCount { get; set; }

    [Benchmark]
    [BenchmarkCategory("Queued handoffs")]
    public async Task Bcl_QueuedHandoffs()
    {
        await _bcl.WaitAsync().ConfigureAwait(false);
        var waiters = new Task[WaiterCount];
        for (var i = 0; i < waiters.Length; ++i)
        {
            waiters[i] = UseBclOnceAsync();
        }

        _bcl.Release();
        await Task.WhenAll(waiters).ConfigureAwait(false);
    }

    [Benchmark]
    [BenchmarkCategory("Queued handoffs")]
    public async Task Nito_QueuedHandoffs()
    {
        var owner = await _nito.LockAsync();
        var waiters = new Task[WaiterCount];
        for (var i = 0; i < waiters.Length; ++i)
        {
            waiters[i] = UseNitoOnceAsync();
        }

        owner.Dispose();
        await Task.WhenAll(waiters).ConfigureAwait(false);
    }

    [Benchmark]
    [BenchmarkCategory("Queued handoffs")]
    public async Task AsyncSharp_QueuedHandoffs()
    {
        await _sharp.LockAsync().ConfigureAwait(false);
        var waiters = new Task[WaiterCount];
        for (var i = 0; i < waiters.Length; ++i)
        {
            waiters[i] = UseAsyncSharpOnceAsync();
        }

        _sharp.Unlock();
        await Task.WhenAll(waiters).ConfigureAwait(false);
    }

    [Benchmark]
    [BenchmarkCategory("Queued handoffs")]
    public async Task VsThreading_QueuedHandoffs()
    {
        var owner = await _vs.EnterAsync();
        var waiters = new Task[WaiterCount];
        for (var i = 0; i < waiters.Length; ++i)
        {
            waiters[i] = UseVsThreadingOnceAsync();
        }

        owner.Dispose();
        await Task.WhenAll(waiters).ConfigureAwait(false);
    }

    private async Task UseBclOnceAsync()
    {
        await _bcl.WaitAsync().ConfigureAwait(false);
        _bcl.Release();
    }

    private async Task UseNitoOnceAsync()
    {
        using (await _nito.LockAsync())
        {
        }
    }

    private async Task UseAsyncSharpOnceAsync()
    {
        await _sharp.LockAsync().ConfigureAwait(false);
        _sharp.Unlock();
    }

    private async Task UseVsThreadingOnceAsync()
    {
        using (await _vs.EnterAsync())
        {
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _bcl.Dispose();
        _sharp.Dispose();
        _vs.Dispose();
    }
}
