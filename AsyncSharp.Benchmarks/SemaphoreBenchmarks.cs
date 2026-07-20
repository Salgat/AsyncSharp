using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Order;
using NitoSemaphore = Nito.AsyncEx.AsyncSemaphore;
using SharpSemaphore = global::AsyncSharp.AsyncSemaphore;
using VsSemaphore = Microsoft.VisualStudio.Threading.AsyncSemaphore;

namespace AsyncSharp.Benchmarks;

[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class SemaphoreUncontendedBenchmarks
{
    private const int Operations = 256;

    private readonly SemaphoreSlim _bcl = new(1, 1);
    private readonly NitoSemaphore _nito = new(1);
    private readonly SharpSemaphore _sharpDefault = new(1, 1);
    private readonly SharpSemaphore _sharpUnfair =
        new(1, 1, SharpSemaphore.WaiterPriority.Unfair);
    private readonly VsSemaphore _vs = new(1);

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("Sync core")]
    public void Bcl_Sync()
    {
        for (var i = 0; i < Operations; ++i)
        {
            _bcl.Wait();
            _bcl.Release();
        }
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("Sync core")]
    public void Nito_Sync()
    {
        for (var i = 0; i < Operations; ++i)
        {
            _nito.Wait();
            _nito.Release();
        }
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("Sync core")]
    public void AsyncSharp_Default_Sync()
    {
        for (var i = 0; i < Operations; ++i)
        {
            _sharpDefault.Wait();
            _sharpDefault.Release();
        }
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("Sync core")]
    public void AsyncSharp_Unfair_Sync()
    {
        for (var i = 0; i < Operations; ++i)
        {
            _sharpUnfair.Wait();
            _sharpUnfair.Release();
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
    public async Task Nito_Async()
    {
        for (var i = 0; i < Operations; ++i)
        {
            await _nito.WaitAsync().ConfigureAwait(false);
            _nito.Release();
        }
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("Async core")]
    public async Task AsyncSharp_Default_Async()
    {
        for (var i = 0; i < Operations; ++i)
        {
            await _sharpDefault.WaitAsync().ConfigureAwait(false);
            _sharpDefault.Release();
        }
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("Async core")]
    public async Task AsyncSharp_Unfair_Async()
    {
        for (var i = 0; i < Operations; ++i)
        {
            await _sharpUnfair.WaitAsync().ConfigureAwait(false);
            _sharpUnfair.Release();
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
            using (await _sharpDefault.WaitAndReleaseAsync().ConfigureAwait(false))
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
        _sharpDefault.Dispose();
        _sharpUnfair.Dispose();
        _vs.Dispose();
    }
}

[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class SemaphoreContentionBenchmarks
{
    private const int MaximumWaiters = 64;

    private readonly SemaphoreSlim _bcl = new(0, MaximumWaiters);
    private readonly NitoSemaphore _nito = new(0);
    private readonly SharpSemaphore _sharpDefault = new(0, MaximumWaiters);
    private readonly SharpSemaphore _sharpStrictFifo =
        new(0, MaximumWaiters, SharpSemaphore.WaiterPriority.FirstInFirstOut);

    [Params(16, MaximumWaiters)]
    public int WaiterCount { get; set; }

    [Benchmark]
    [BenchmarkCategory("Queued batch release")]
    public async Task Bcl_BatchRelease()
    {
        var waiters = new Task[WaiterCount];
        for (var i = 0; i < waiters.Length; ++i)
        {
            waiters[i] = _bcl.WaitAsync();
        }

        _bcl.Release(WaiterCount);
        await Task.WhenAll(waiters).ConfigureAwait(false);
    }

    [Benchmark]
    [BenchmarkCategory("Queued batch release")]
    public async Task Nito_BatchRelease()
    {
        var waiters = new Task[WaiterCount];
        for (var i = 0; i < waiters.Length; ++i)
        {
            waiters[i] = _nito.WaitAsync();
        }

        _nito.Release(WaiterCount);
        await Task.WhenAll(waiters).ConfigureAwait(false);
    }

    [Benchmark]
    [BenchmarkCategory("Queued batch release")]
    public async Task AsyncSharp_Default_BatchRelease()
    {
        var waiters = new Task[WaiterCount];
        for (var i = 0; i < waiters.Length; ++i)
        {
            waiters[i] = _sharpDefault.WaitAsync();
        }

        _sharpDefault.Release(WaiterCount);
        await Task.WhenAll(waiters).ConfigureAwait(false);
    }

    [Benchmark]
    [BenchmarkCategory("Queued batch release")]
    public async Task AsyncSharp_StrictFifo_BatchRelease()
    {
        var waiters = new Task[WaiterCount];
        for (var i = 0; i < waiters.Length; ++i)
        {
            waiters[i] = _sharpStrictFifo.WaitAsync();
        }

        _sharpStrictFifo.Release(WaiterCount);
        await Task.WhenAll(waiters).ConfigureAwait(false);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _bcl.Dispose();
        _sharpDefault.Dispose();
        _sharpStrictFifo.Dispose();
    }
}

[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class SemaphoreMultiPermitBenchmarks
{
    private const int Operations = 256;
    private const int PermitCount = 4;

    private readonly SemaphoreSlim _bcl = new(PermitCount, PermitCount);
    private readonly NitoSemaphore _nito = new(PermitCount);
    private readonly SharpSemaphore _sharp = new(PermitCount, PermitCount);

    [Benchmark(OperationsPerInvoke = Operations)]
    public async Task Bcl_Sequential_NonAtomic()
    {
        for (var operation = 0; operation < Operations; ++operation)
        {
            for (var permit = 0; permit < PermitCount; ++permit)
            {
                await _bcl.WaitAsync().ConfigureAwait(false);
            }

            _bcl.Release(PermitCount);
        }
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    public async Task Nito_Sequential_NonAtomic()
    {
        for (var operation = 0; operation < Operations; ++operation)
        {
            for (var permit = 0; permit < PermitCount; ++permit)
            {
                await _nito.WaitAsync().ConfigureAwait(false);
            }

            _nito.Release(PermitCount);
        }
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    public async Task AsyncSharp_Atomic()
    {
        for (var operation = 0; operation < Operations; ++operation)
        {
            await _sharp.WaitAsync(PermitCount).ConfigureAwait(false);
            _sharp.Release(PermitCount);
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _bcl.Dispose();
        _sharp.Dispose();
    }
}
