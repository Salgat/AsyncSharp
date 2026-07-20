# Benchmark results

> These are directional `ShortRun` results, not publication-grade measurements. Each benchmark has only three measured iterations, so large differences are useful signals but close rankings are not statistically defensible. Run the default BenchmarkDotNet job on an idle machine before making release claims.

## Run identity

- Date: 2026-07-20
- AsyncSharp source: `c9634d3`
- OS: Windows 11 25H2 (`10.0.26200.8655`)
- CPU: AMD Ryzen 9 7950X3D, 16 physical / 32 logical cores
- SDK: .NET SDK 10.0.302
- Measured runtime: .NET 8.0.29, x64 RyuJIT, concurrent workstation GC
- BenchmarkDotNet: 0.15.8, `ShortRun` (1 launch, 3 warmups, 3 measured iterations)
- Comparators: .NET BCL, Nito.AsyncEx.Coordination 5.1.2, and Microsoft.VisualStudio.Threading.Only 18.7.23

The uncontended rows are normalized to one acquire/release operation with `OperationsPerInvoke = 256`. Queue-drain rows measure one complete batch of 16 or 64 prequeued waiters, including the common task-array and `Task.WhenAll` orchestration.

## Executive summary

- AsyncSharp's most consistent concern in these results is successful-fast-path allocation. Its default semaphore and mutex async core operations allocate 904 B per acquire/release while the BCL core counterparts and Nito's semaphore core path report 0 B in this workload.
- AsyncSharp's default semaphore async path measured about 21x slower than Nito. The explicitly throughput-oriented `Unfair` policy roughly halved the cost, but remained about 11x slower.
- Semaphore queue draining was one of the largest gaps: the default AsyncSharp policy took about 20-23x the BCL time and allocated roughly 12x as much memory.
- Mutex queued handoffs were closer than the uncontended results: AsyncSharp took about 2.6x the BCL time at both tested queue sizes.
- The reader/writer lock was more competitive against Microsoft.VisualStudio.Threading: asynchronous reads were roughly tied, AsyncSharp writes and upgrades had lower nominal means, and AsyncSharp had lower nominal means in all four measured queue transitions. Nito was still generally faster and allocated less.
- AsyncSharp's atomic multi-permit acquisition and upgradeable-reader API provide semantics that the faster baselines do not necessarily offer. Those rows are feature-cost comparisons, not interchangeable-operation rankings.

## Semaphore

### Uncontended acquisition

| Scenario | Implementation | Mean | Allocated |
|---|---|---:|---:|
| Async core | Nito | 15.44 ns | 0 B |
| Async core | BCL `SemaphoreSlim` | 16.65 ns | 0 B |
| Async core | AsyncSharp `Unfair` | 173.72 ns | 536 B |
| Async core | AsyncSharp default | 327.48 ns | 904 B |
| Async lease | VS Threading | 14.63 ns | 0 B |
| Async lease | Nito | 71.20 ns | 384 B |
| Async lease | AsyncSharp | 345.65 ns | 1,120 B |
| Sync core | Nito | 15.85 ns | 0 B |
| Sync core | BCL `SemaphoreSlim` | 16.84 ns | 0 B |
| Sync core | AsyncSharp `Unfair` | 47.35 ns | 56 B |
| Sync core | AsyncSharp default | 158.32 ns | 352 B |

### Prequeued batch release

| Waiters | Implementation | Mean | Allocated |
|---:|---|---:|---:|
| 16 | BCL `SemaphoreSlim` | 529.5 ns | 1.60 KB |
| 16 | Nito | 802.2 ns | 2.73 KB |
| 16 | AsyncSharp strict FIFO | 9.700 us | 19.63 KB |
| 16 | AsyncSharp default | 10.620 us | 19.63 KB |
| 64 | BCL `SemaphoreSlim` | 2.235 us | 6.10 KB |
| 64 | Nito | 3.409 us | 10.60 KB |
| 64 | AsyncSharp strict FIFO | 44.692 us | 77.49 KB |
| 64 | AsyncSharp default | 51.933 us | 77.48 KB |

For this all-single-permit workload, strict FIFO and the default `FirstInFirstOutUnfair` policy have equivalent ordering semantics. Their ShortRun ordering should not be interpreted as a reliable policy-performance difference.

### Four-permit acquisition

| Implementation | Semantics | Mean | Allocated |
|---|---|---:|---:|
| BCL `SemaphoreSlim` | Four sequential waits; non-atomic | 41.93 ns | 0 B |
| Nito | Four sequential waits; non-atomic | 42.01 ns | 0 B |
| AsyncSharp | One atomic four-permit wait | 302.84 ns | 904 B |

AsyncSharp was about 7.2x slower here, but it is the only row that atomically acquires all four permits. The sequential alternatives can partially acquire permits and deadlock under contention if used as a substitute.

## Mutex

### Uncontended acquisition

| Scenario | Implementation | Mean | Allocated |
|---|---|---:|---:|
| Sync core lower bound | BCL `Monitor` | 4.35 ns | 0 B |
| Sync core | BCL `SemaphoreSlim` | 17.13 ns | 0 B |
| Sync core | AsyncSharp | 151.80 ns | 352 B |
| Sync lease | Nito | 50.31 ns | 320 B |
| Sync lease | AsyncSharp | 171.95 ns | 496 B |
| Async core | BCL `SemaphoreSlim` | 16.00 ns | 0 B |
| Async core | AsyncSharp | 318.31 ns | 904 B |
| Async lease | VS Threading | 13.64 ns | 0 B |
| Async lease | Nito | 56.62 ns | 320 B |
| Async lease | AsyncSharp | 335.99 ns | 1,120 B |

`Monitor` is a synchronous, recursive, thread-affine lower bound and cannot be held across an `await`; it is not an AsyncMutex replacement.

### Prequeued serialized handoffs

| Waiters | Implementation | Mean | Allocated |
|---:|---|---:|---:|
| 16 | VS Threading | 7.026 us | 4.88 KB |
| 16 | BCL `SemaphoreSlim` | 7.129 us | 3.47 KB |
| 16 | Nito | 8.144 us | 8.66 KB |
| 16 | AsyncSharp | 18.406 us | 23.35 KB |
| 64 | BCL `SemaphoreSlim` | 35.063 us | 13.27 KB |
| 64 | Nito | 35.518 us | 33.09 KB |
| 64 | VS Threading | 38.371 us | 20.31 KB |
| 64 | AsyncSharp | 90.951 us | 89.81 KB |

The close BCL, Nito, and VS Threading rankings are within the uncertainty of this ShortRun. The defensible result is the larger AsyncSharp gap, about 2.6x versus the BCL at both queue sizes.

## Reader/writer lock

### Uncontended async acquisition

| Scenario | Implementation | Mean | Allocated |
|---|---|---:|---:|
| Read | Nito | 52.70 ns | 320 B |
| Read | AsyncSharp | 329.41 ns | 1,120 B |
| Read | VS Threading | 334.58 ns | 208 B |
| Write | Nito | 87.59 ns | 496 B |
| Write | AsyncSharp | 334.02 ns | 1,120 B |
| Write | VS Threading | 806.36 ns | 417 B |
| Upgrade | AsyncSharp | 870.00 ns | 2,952 B |
| Upgrade | VS Threading | 1,498.45 ns | 625 B |

AsyncSharp's read result is effectively tied with VS Threading in this run, not a meaningful win. AsyncSharp measured about 2.4x faster for writes and 1.7x faster for upgrades, but allocated about 2.7x and 4.7x as much, respectively. Nito has no upgradeable-reader API.

Synchronous lower bounds were 10.04 ns/0 B for a BCL read, 10.41 ns/0 B for a BCL write, and 15.93 ns/0 B for a BCL upgrade. AsyncSharp measured 163.81 ns/496 B, 182.12 ns/496 B, and 507.18 ns/1,152 B for those operations. `ReaderWriterLockSlim` is thread-affine and is not an async replacement.

### Prequeued transitions

| Transition | Waiters | Implementation | Mean | Allocated |
|---|---:|---|---:|---:|
| Readers after writer | 16 | Nito | 9.317 us | 8.83 KB |
| Readers after writer | 16 | AsyncSharp | 13.115 us | 25.32 KB |
| Readers after writer | 16 | VS Threading | 30.264 us | 9.07 KB |
| Readers after writer | 64 | Nito | 23.619 us | 33.22 KB |
| Readers after writer | 64 | AsyncSharp | 52.576 us | 97.05 KB |
| Readers after writer | 64 | VS Threading | 88.747 us | 33.82 KB |
| Writers after reader | 16 | Nito | 8.255 us | 12.78 KB |
| Writers after reader | 16 | AsyncSharp | 17.934 us | 25.56 KB |
| Writers after reader | 16 | VS Threading | 24.109 us | 9.93 KB |
| Writers after reader | 64 | Nito | 39.850 us | 49.59 KB |
| Writers after reader | 64 | AsyncSharp | 76.849 us | 98.04 KB |
| Writers after reader | 64 | VS Threading | 90.236 us | 38.05 KB |

AsyncSharp trailed Nito by a nominal 1.4-2.2x across these transitions, while its nominal means were 1.2-2.3x lower than VS Threading's. AsyncSharp allocated roughly 2-3x as much memory as either comparator. VS Threading's reader/writer lock also provides ambient ownership, nesting, diagnostics, and deadlock-management semantics that add work absent from the other implementations.

## Limits of this comparison

- Queue-drain tests enqueue waiters from one caller and then release an owner. They measure queue transition and handoff cost, not sustained parallel producer throughput.
- The BCL does not guarantee `SemaphoreSlim` waiter ordering. Nito uses FIFO queues. AsyncSharp exposes multiple policies.
- Nito's reader/writer lock has fixed writer preference, while AsyncSharp exposes configurable policies. Sustained mixed traffic was not measured.
- Nito resolves its `netstandard2.0` asset in this .NET 8 benchmark; Microsoft.VisualStudio.Threading supplies a native `net8.0` asset.
- Empty critical sections intentionally isolate synchronization overhead. Real workloads with longer protected work will reduce these relative differences.
- Cancellation storms, timeouts, mixed permit counts, and sustained mixed reader/writer traffic are not included in this first suite.
- Package versions and results are a dated snapshot, not universal library rankings.

## Reproduce

Quick comparison:

```powershell
dotnet run --project AsyncSharp.Benchmarks -c Release -- --job short
```

Longer run for durable conclusions:

```powershell
dotnet run --project AsyncSharp.Benchmarks -c Release
```

BenchmarkDotNet writes detailed reports under `BenchmarkDotNet.Artifacts/results/`.
