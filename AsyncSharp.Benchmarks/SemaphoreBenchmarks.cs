using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Order;
using NitoSemaphore = Nito.AsyncEx.AsyncSemaphore;
using SharpSemaphore = global::AsyncSharp.AsyncSemaphore;
using VsSemaphore = Microsoft.VisualStudio.Threading.AsyncSemaphore;

namespace AsyncSharp.Benchmarks;

[MemoryDiagnoser]
[InvocationCount(256)]
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

    [Benchmark(OperationsPerInvoke = Operations, Baseline = true)]
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

    [Benchmark(OperationsPerInvoke = Operations, Baseline = true)]
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

    [Benchmark(OperationsPerInvoke = Operations, Baseline = true)]
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
[InvocationCount(64)]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class SemaphoreContentionBenchmarks
{
    private const int MaximumWaiters = 256;

    private readonly SemaphoreSlim _bcl = new(0, MaximumWaiters);
    private readonly NitoSemaphore _nito = new(0);
    private readonly SharpSemaphore _sharpDefault = new(0, MaximumWaiters);
    private readonly SharpSemaphore _sharpStrictFifo =
        new(0, MaximumWaiters, SharpSemaphore.WaiterPriority.FirstInFirstOut);

    [Params(1, 16, 64, MaximumWaiters)]
    public int WaiterCount { get; set; }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Queued core batch release")]
    public async Task Bcl_BatchRelease()
    {
        var waiters = new Task[WaiterCount];
        for (var i = 0; i < waiters.Length; ++i)
        {
            waiters[i] = _bcl.WaitAsync();
        }

        BenchmarkTaskAssertions.EnsureAllPending(waiters);
        _bcl.Release(WaiterCount);
        await Task.WhenAll(waiters).ConfigureAwait(false);
    }

    [Benchmark]
    [BenchmarkCategory("Queued core batch release")]
    public async Task Nito_BatchRelease()
    {
        var waiters = new Task[WaiterCount];
        for (var i = 0; i < waiters.Length; ++i)
        {
            waiters[i] = _nito.WaitAsync();
        }

        BenchmarkTaskAssertions.EnsureAllPending(waiters);
        _nito.Release(WaiterCount);
        await Task.WhenAll(waiters).ConfigureAwait(false);
    }

    [Benchmark]
    [BenchmarkCategory("Queued core batch release")]
    public async Task AsyncSharp_Default_BatchRelease()
    {
        var waiters = new Task[WaiterCount];
        for (var i = 0; i < waiters.Length; ++i)
        {
            waiters[i] = _sharpDefault.WaitAsync();
        }

        BenchmarkTaskAssertions.EnsureAllPending(waiters);
        _sharpDefault.Release(WaiterCount);
        await Task.WhenAll(waiters).ConfigureAwait(false);
    }

    [Benchmark]
    [BenchmarkCategory("Queued core batch release")]
    public async Task AsyncSharp_StrictFifo_BatchRelease()
    {
        var waiters = new Task[WaiterCount];
        for (var i = 0; i < waiters.Length; ++i)
        {
            waiters[i] = _sharpStrictFifo.WaitAsync();
        }

        BenchmarkTaskAssertions.EnsureAllPending(waiters);
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
[InvocationCount(1)]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class SemaphoreReleaseCallBenchmarks
{
    private const int MaximumWaiters = 256;

    private SemaphoreSlim? _bcl;
    private Task[]? _bclWaiters;
    private NitoSemaphore? _nito;
    private Task[]? _nitoWaiters;
    private SharpSemaphore? _sharpDefault;
    private Task[]? _sharpDefaultWaiters;
    private SharpSemaphore? _sharpStrictFifo;
    private Task[]? _sharpStrictFifoWaiters;

    [Params(1, 16, 64, MaximumWaiters)]
    public int WaiterCount { get; set; }

    [IterationSetup(Target = nameof(Bcl_ReleaseCall))]
    public void SetupBcl()
    {
        _bcl = new SemaphoreSlim(0, MaximumWaiters);
        _bclWaiters = new Task[WaiterCount];
        for (var i = 0; i < _bclWaiters.Length; ++i)
        {
            _bclWaiters[i] = _bcl.WaitAsync();
            BenchmarkTaskAssertions.EnsurePending(_bclWaiters[i]);
        }
    }

    [Benchmark]
    public void Bcl_ReleaseCall()
        => _bcl!.Release(WaiterCount);

    [IterationCleanup(Target = nameof(Bcl_ReleaseCall))]
    public void CleanupBcl()
    {
        Task.WhenAll(_bclWaiters!).GetAwaiter().GetResult();
        _bcl!.Dispose();
    }

    [IterationSetup(Target = nameof(Nito_ReleaseCall))]
    public void SetupNito()
    {
        _nito = new NitoSemaphore(0);
        _nitoWaiters = new Task[WaiterCount];
        for (var i = 0; i < _nitoWaiters.Length; ++i)
        {
            _nitoWaiters[i] = _nito.WaitAsync();
            BenchmarkTaskAssertions.EnsurePending(_nitoWaiters[i]);
        }
    }

    [Benchmark(Baseline = true)]
    public void Nito_ReleaseCall()
        => _nito!.Release(WaiterCount);

    [IterationCleanup(Target = nameof(Nito_ReleaseCall))]
    public void CleanupNito()
        => Task.WhenAll(_nitoWaiters!).GetAwaiter().GetResult();

    [IterationSetup(Target = nameof(AsyncSharp_Default_ReleaseCall))]
    public void SetupAsyncSharpDefault()
    {
        _sharpDefault = new SharpSemaphore(0, MaximumWaiters);
        _sharpDefaultWaiters = new Task[WaiterCount];
        for (var i = 0; i < _sharpDefaultWaiters.Length; ++i)
        {
            _sharpDefaultWaiters[i] = _sharpDefault.WaitAsync();
            BenchmarkTaskAssertions.EnsurePending(_sharpDefaultWaiters[i]);
        }
    }

    [Benchmark]
    public void AsyncSharp_Default_ReleaseCall()
        => _sharpDefault!.Release(WaiterCount);

    [IterationCleanup(Target = nameof(AsyncSharp_Default_ReleaseCall))]
    public void CleanupAsyncSharpDefault()
    {
        Task.WhenAll(_sharpDefaultWaiters!).GetAwaiter().GetResult();
        _sharpDefault!.Dispose();
    }

    [IterationSetup(Target = nameof(AsyncSharp_StrictFifo_ReleaseCall))]
    public void SetupAsyncSharpStrictFifo()
    {
        _sharpStrictFifo = new SharpSemaphore(
            0,
            MaximumWaiters,
            SharpSemaphore.WaiterPriority.FirstInFirstOut);
        _sharpStrictFifoWaiters = new Task[WaiterCount];
        for (var i = 0; i < _sharpStrictFifoWaiters.Length; ++i)
        {
            _sharpStrictFifoWaiters[i] = _sharpStrictFifo.WaitAsync();
            BenchmarkTaskAssertions.EnsurePending(_sharpStrictFifoWaiters[i]);
        }
    }

    [Benchmark]
    public void AsyncSharp_StrictFifo_ReleaseCall()
        => _sharpStrictFifo!.Release(WaiterCount);

    [IterationCleanup(Target = nameof(AsyncSharp_StrictFifo_ReleaseCall))]
    public void CleanupAsyncSharpStrictFifo()
    {
        Task.WhenAll(_sharpStrictFifoWaiters!).GetAwaiter().GetResult();
        _sharpStrictFifo!.Dispose();
    }

}

[MemoryDiagnoser]
[InvocationCount(1)]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class SemaphoreEnqueueBenchmarks
{
    private const int MaximumWaiters = 256;

    private SemaphoreSlim? _bcl;
    private Task[]? _bclWaiters;
    private NitoSemaphore? _nito;
    private Task[]? _nitoWaiters;
    private SharpSemaphore? _sharpDefault;
    private Task[]? _sharpDefaultWaiters;
    private SharpSemaphore? _sharpStrictFifo;
    private Task[]? _sharpStrictFifoWaiters;

    [Params(1, 16, 64, MaximumWaiters)]
    public int WaiterCount { get; set; }

    [IterationSetup(Target = nameof(Bcl_EnqueueBatch))]
    public void SetupBcl()
    {
        _bcl = new SemaphoreSlim(0, MaximumWaiters);
        _bclWaiters = new Task[WaiterCount];
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Core waiter enqueue")]
    public void Bcl_EnqueueBatch()
    {
        for (var i = 0; i < _bclWaiters!.Length; ++i)
        {
            _bclWaiters[i] = _bcl!.WaitAsync();
        }
    }

    [IterationCleanup(Target = nameof(Bcl_EnqueueBatch))]
    public void CleanupBcl()
    {
        BenchmarkTaskAssertions.EnsureAllPending(_bclWaiters!);
        _bcl!.Release(WaiterCount);
        Task.WhenAll(_bclWaiters!).GetAwaiter().GetResult();
        _bcl.Dispose();
    }

    [IterationSetup(Target = nameof(Nito_EnqueueBatch))]
    public void SetupNito()
    {
        _nito = new NitoSemaphore(0);
        _nitoWaiters = new Task[WaiterCount];
    }

    [Benchmark]
    [BenchmarkCategory("Core waiter enqueue")]
    public void Nito_EnqueueBatch()
    {
        for (var i = 0; i < _nitoWaiters!.Length; ++i)
        {
            _nitoWaiters[i] = _nito!.WaitAsync();
        }
    }

    [IterationCleanup(Target = nameof(Nito_EnqueueBatch))]
    public void CleanupNito()
    {
        BenchmarkTaskAssertions.EnsureAllPending(_nitoWaiters!);
        _nito!.Release(WaiterCount);
        Task.WhenAll(_nitoWaiters!).GetAwaiter().GetResult();
    }

    [IterationSetup(Target = nameof(AsyncSharp_Default_EnqueueBatch))]
    public void SetupAsyncSharpDefault()
    {
        _sharpDefault = new SharpSemaphore(0, MaximumWaiters);
        _sharpDefaultWaiters = new Task[WaiterCount];
    }

    [Benchmark]
    [BenchmarkCategory("Core waiter enqueue")]
    public void AsyncSharp_Default_EnqueueBatch()
    {
        for (var i = 0; i < _sharpDefaultWaiters!.Length; ++i)
        {
            _sharpDefaultWaiters[i] = _sharpDefault!.WaitAsync();
        }
    }

    [IterationCleanup(Target = nameof(AsyncSharp_Default_EnqueueBatch))]
    public void CleanupAsyncSharpDefault()
    {
        BenchmarkTaskAssertions.EnsureAllPending(_sharpDefaultWaiters!);
        _sharpDefault!.Release(WaiterCount);
        Task.WhenAll(_sharpDefaultWaiters!).GetAwaiter().GetResult();
        _sharpDefault.Dispose();
    }

    [IterationSetup(Target = nameof(AsyncSharp_StrictFifo_EnqueueBatch))]
    public void SetupAsyncSharpStrictFifo()
    {
        _sharpStrictFifo = new SharpSemaphore(
            0,
            MaximumWaiters,
            SharpSemaphore.WaiterPriority.FirstInFirstOut);
        _sharpStrictFifoWaiters = new Task[WaiterCount];
    }

    [Benchmark]
    [BenchmarkCategory("Core waiter enqueue")]
    public void AsyncSharp_StrictFifo_EnqueueBatch()
    {
        for (var i = 0; i < _sharpStrictFifoWaiters!.Length; ++i)
        {
            _sharpStrictFifoWaiters[i] = _sharpStrictFifo!.WaitAsync();
        }
    }

    [IterationCleanup(Target = nameof(AsyncSharp_StrictFifo_EnqueueBatch))]
    public void CleanupAsyncSharpStrictFifo()
    {
        BenchmarkTaskAssertions.EnsureAllPending(_sharpStrictFifoWaiters!);
        _sharpStrictFifo!.Release(WaiterCount);
        Task.WhenAll(_sharpStrictFifoWaiters!).GetAwaiter().GetResult();
        _sharpStrictFifo.Dispose();
    }
}

[MemoryDiagnoser]
[InvocationCount(1)]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class SemaphoreCancellationBatchBenchmarks
{
    private const int MaximumWaiters = 256;

    private SemaphoreSlim? _bcl;
    private CancellationTokenSource? _bclCancellation;
    private Task[]? _bclWaiters;
    private NitoSemaphore? _nito;
    private CancellationTokenSource? _nitoCancellation;
    private Task[]? _nitoWaiters;
    private SharpSemaphore? _sharpDefault;
    private CancellationTokenSource? _sharpDefaultCancellation;
    private Task[]? _sharpDefaultWaiters;
    private SharpSemaphore? _sharpStrictFifo;
    private CancellationTokenSource? _sharpStrictFifoCancellation;
    private Task[]? _sharpStrictFifoWaiters;

    [Params(1, 16, 64, MaximumWaiters)]
    public int WaiterCount { get; set; }

    [IterationSetup(Target = nameof(Bcl_CancelAndDrainBatch))]
    public void SetupBcl()
    {
        _bcl = new SemaphoreSlim(0, MaximumWaiters);
        _bclCancellation = new CancellationTokenSource();
        _bclWaiters = QueueCancelableWaiters(
            WaiterCount,
            _bclCancellation.Token,
            token => _bcl.WaitAsync(token));
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Cancel and drain prequeued waiters")]
    public Task Bcl_CancelAndDrainBatch()
        => CancelAndDrainAsync(_bclCancellation!, _bclWaiters!);

    [IterationCleanup(Target = nameof(Bcl_CancelAndDrainBatch))]
    public void CleanupBcl()
    {
        _bclCancellation!.Dispose();
        _bcl!.Dispose();
    }

    [IterationSetup(Target = nameof(Nito_CancelAndDrainBatch))]
    public void SetupNito()
    {
        _nito = new NitoSemaphore(0);
        _nitoCancellation = new CancellationTokenSource();
        _nitoWaiters = QueueCancelableWaiters(
            WaiterCount,
            _nitoCancellation.Token,
            token => _nito.WaitAsync(token));
    }

    [Benchmark]
    [BenchmarkCategory("Cancel and drain prequeued waiters")]
    public Task Nito_CancelAndDrainBatch()
        => CancelAndDrainAsync(_nitoCancellation!, _nitoWaiters!);

    [IterationCleanup(Target = nameof(Nito_CancelAndDrainBatch))]
    public void CleanupNito()
        => _nitoCancellation!.Dispose();

    [IterationSetup(Target = nameof(AsyncSharp_Default_CancelAndDrainBatch))]
    public void SetupAsyncSharpDefault()
    {
        _sharpDefault = new SharpSemaphore(0, MaximumWaiters);
        _sharpDefaultCancellation = new CancellationTokenSource();
        _sharpDefaultWaiters = QueueCancelableWaiters(
            WaiterCount,
            _sharpDefaultCancellation.Token,
            token => _sharpDefault.WaitAsync(token));
    }

    [Benchmark]
    [BenchmarkCategory("Cancel and drain prequeued waiters")]
    public Task AsyncSharp_Default_CancelAndDrainBatch()
        => CancelAndDrainAsync(_sharpDefaultCancellation!, _sharpDefaultWaiters!);

    [IterationCleanup(Target = nameof(AsyncSharp_Default_CancelAndDrainBatch))]
    public void CleanupAsyncSharpDefault()
    {
        _sharpDefaultCancellation!.Dispose();
        _sharpDefault!.Dispose();
    }

    [IterationSetup(Target = nameof(AsyncSharp_StrictFifo_CancelAndDrainBatch))]
    public void SetupAsyncSharpStrictFifo()
    {
        _sharpStrictFifo = new SharpSemaphore(
            0,
            MaximumWaiters,
            SharpSemaphore.WaiterPriority.FirstInFirstOut);
        _sharpStrictFifoCancellation = new CancellationTokenSource();
        _sharpStrictFifoWaiters = QueueCancelableWaiters(
            WaiterCount,
            _sharpStrictFifoCancellation.Token,
            token => _sharpStrictFifo.WaitAsync(token));
    }

    [Benchmark]
    [BenchmarkCategory("Cancel and drain prequeued waiters")]
    public Task AsyncSharp_StrictFifo_CancelAndDrainBatch()
        => CancelAndDrainAsync(
            _sharpStrictFifoCancellation!,
            _sharpStrictFifoWaiters!);

    [IterationCleanup(Target = nameof(AsyncSharp_StrictFifo_CancelAndDrainBatch))]
    public void CleanupAsyncSharpStrictFifo()
    {
        _sharpStrictFifoCancellation!.Dispose();
        _sharpStrictFifo!.Dispose();
    }

    private static Task[] QueueCancelableWaiters(
        int waiterCount,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task> waitAsync)
    {
        var waiters = new Task[waiterCount];
        for (var i = 0; i < waiters.Length; ++i)
        {
            waiters[i] = waitAsync(cancellationToken);
        }

        BenchmarkTaskAssertions.EnsureAllPending(waiters);
        return waiters;
    }

    private static async Task CancelAndDrainAsync(
        CancellationTokenSource cancellation,
        Task[] waiters)
    {
        cancellation.Cancel();
        await BenchmarkTaskAssertions.AwaitCanceledAsync(waiters).ConfigureAwait(false);
    }
}

[MemoryDiagnoser]
[InvocationCount(256)]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class SemaphoreFastPathOptionsBenchmarks
{
    private const int Operations = 256;

    private readonly CancellationTokenSource _neverCanceled = new();
    private readonly SharpSemaphore _sharp = new(1, 1);

    [Benchmark(OperationsPerInvoke = Operations, Baseline = true)]
    public async Task NoToken_InfiniteTimeout()
    {
        for (var i = 0; i < Operations; ++i)
        {
            await _sharp.WaitAsync().ConfigureAwait(false);
            _sharp.Release();
        }
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    public async Task CancelableToken_InfiniteTimeout()
    {
        var cancellationToken = _neverCanceled.Token;
        for (var i = 0; i < Operations; ++i)
        {
            await _sharp.WaitAsync(cancellationToken).ConfigureAwait(false);
            _sharp.Release();
        }
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    public async Task NoToken_FiniteTimeout()
    {
        for (var i = 0; i < Operations; ++i)
        {
            await _sharp.WaitAsync(1, TimeSpan.FromMinutes(1), CancellationToken.None)
                .ConfigureAwait(false);
            _sharp.Release();
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _neverCanceled.Dispose();
        _sharp.Dispose();
    }
}

[MemoryDiagnoser]
[InvocationCount(64)]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class SemaphoreMixedPermitContentionBenchmarks
{
    private const int PermitCount = 4;

    private readonly SharpSemaphore _strictFifo =
        new(0, PermitCount, SharpSemaphore.WaiterPriority.FirstInFirstOut);
    private readonly SharpSemaphore _default = new(0, PermitCount);
    private readonly SharpSemaphore _unfair =
        new(0, PermitCount, SharpSemaphore.WaiterPriority.Unfair);
    private readonly SharpSemaphore _lowToHigh =
        new(0, PermitCount, SharpSemaphore.WaiterPriority.LowToHigh);
    private readonly SharpSemaphore _highToLow =
        new(0, PermitCount, SharpSemaphore.WaiterPriority.HighToLow);

    [Params(1, 16, 64, 256)]
    public int WaiterCount { get; set; }

    [Benchmark(Baseline = true)]
    public Task StrictFifo_MixedPermits_PartialRelease()
        => DrainMixedPermitQueueAsync(_strictFifo);

    [Benchmark]
    public Task Default_MixedPermits_PartialRelease()
        => DrainMixedPermitQueueAsync(_default);

    [Benchmark]
    public Task Unfair_MixedPermits_PartialRelease()
        => DrainMixedPermitQueueAsync(_unfair);

    [Benchmark]
    public Task LowToHigh_MixedPermits_PartialRelease()
        => DrainMixedPermitQueueAsync(_lowToHigh);

    [Benchmark]
    public Task HighToLow_MixedPermits_PartialRelease()
        => DrainMixedPermitQueueAsync(_highToLow);

    private async Task DrainMixedPermitQueueAsync(SharpSemaphore semaphore)
    {
        var waiters = new Task[WaiterCount];
        var totalPermits = 0;
        for (var i = 0; i < waiters.Length; ++i)
        {
            var permits = i % PermitCount == 0 ? PermitCount : 1;
            totalPermits += permits;
            waiters[i] = semaphore.WaitAsync(permits);
        }

        BenchmarkTaskAssertions.EnsureAllPending(waiters);
        for (var permit = 0; permit < totalPermits; ++permit)
        {
            semaphore.Release();
        }

        await Task.WhenAll(waiters).ConfigureAwait(false);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _strictFifo.Dispose();
        _default.Dispose();
        _unfair.Dispose();
        _lowToHigh.Dispose();
        _highToLow.Dispose();
    }
}

[MemoryDiagnoser]
[InvocationCount(256)]
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
