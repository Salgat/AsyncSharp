# Benchmark results

> Current engineering snapshot: the complete `Full` profile finished all 177 benchmark cases in 2 minutes 48 seconds. The fixed workloads make this run reproducible and quick, but many measured iterations are shorter than BenchmarkDotNet's requested 100 ms minimum. Allocation results and large effects are useful; close latency rankings are not publication-grade.

## Run identity

- Artifact: `BenchmarkDotNet.Artifacts/runs/20260721-152300688-ac0a160eb8a0-full-post-spin-monitor-full-v6/`
- Started: 2026-07-21 15:23:00 UTC
- HEAD at launch: `ac0a160eb8a0bfb06a396099d60a3727212d304e`
- Working tree: dirty; recorded source snapshot SHA-256 `ae4c324a51d2251e4ef2e6d2fb4482be04e29932760bf5cb311a3b1f7e125aef`
- OS: Windows 11 25H2 (`10.0.26200.8655`)
- CPU: AMD Ryzen 9 7950X3D, 16 physical / 32 logical cores
- Runtime: .NET 8.0.29, x64 RyuJIT, concurrent workstation GC
- SDK: .NET SDK 10.0.302
- BenchmarkDotNet: 0.15.8
- Power: `HighPerformance`; the host was already using the High performance scheme
- Process affinity: `0xFFFFFFFF` (all 32 logical processors)
- Full job: throughput, 3 launches, 16 warmups and 5 measured iterations per launch, 100 ms requested target/minimum, unroll factor 1, `MemoryDiagnoser`
- Comparators: .NET BCL, Nito.AsyncEx.Coordination 5.1.2, and Microsoft.VisualStudio.Threading.Only 18.7.23
- Completion: 177/177 cases, zero nonzero benchmark-process exits, global time 00:02:48

Uncontended classes execute 256 benchmark-method invocations containing 256 operations each, or 65,536 logical operations per iteration. Stateful queue-drain classes execute 64 complete scenarios per iteration; isolated enqueue, release, and cancellation batches execute once, as does each 200-operation mixed reader/writer scenario. These explicit bounds replaced adaptive calibration that had expanded fast allocation-heavy cases into hundreds of gigabytes of transient allocation.

## Outcome against the optimization gates

| Gate | Result | Status |
|---|---|---|
| 0 B/op for uncontended core `Wait`/`WaitAsync` and `Lock`/`LockAsync` | Semaphore: 57.47 ns / 59.93 ns; mutex: 60.77 ns / 60.77 ns; all four allocate 0 B | Pass |
| At least 50% lower uncontended default-path latency | Versus the checked-in historical Short snapshot: semaphore sync -63.7%, semaphore async -81.7%, mutex sync -60.0%, mutex async -80.9% | Directional pass |
| At least 60% lower strict-FIFO queued allocation | 64 waiters: -77.79%; 256 waiters: -77.89% | Pass |
| No worse than 5x elapsed growth from 64 to 256 strict-FIFO waiters | 11.7785 us to 48.2062 us, or 4.093x | Pass |
| No statistically credible regression above 10% in an existing equivalent scenario | No retained pre-change Full run exists. The historical `Unfair` sync row moved nominally from 47.35 ns to 58.27 ns, but the profiles and iteration quality are not comparable enough to decide credibility. | Unproven |

The percentage comparisons above use the retained historical `Short` results and the pre-change queue-only Short run on the same machine. They establish direction and allocation changes, not a paired Full-before/Full-after statistical claim. The absolute 0 B and scaling gates are measured directly by this Full run.

## AsyncSemaphore

### Uncontended acquisition

| Category | Implementation | Mean | Allocated |
|---|---|---:|---:|
| Async core | BCL `SemaphoreSlim` | 21.45 ns | 0 B |
| Async core | Nito | 45.04 ns | 0 B |
| Async core | AsyncSharp `Unfair` | 56.59 ns | 0 B |
| Async core | AsyncSharp default | 59.93 ns | 0 B |
| Sync core | BCL `SemaphoreSlim` | 20.40 ns | 0 B |
| Sync core | Nito | 39.93 ns | 0 B |
| Sync core | AsyncSharp default | 57.47 ns | 0 B |
| Sync core | AsyncSharp `Unfair` | 58.27 ns | 0 B |
| Async lease | VS Threading | 55.82 ns | 0 B |
| Async lease | Nito | 60.47 ns | 384 B |
| Async lease | AsyncSharp | 42.73 ns | 200 B |

AsyncSharp's async lease is nominally faster than the two lease comparators in this run and reduces historical allocation from 1,120 B to 200 B. The short fixed iterations still make the exact latency ranking directional.

### Fast-path options and atomic permits

| Scenario | Mean | Allocated |
|---|---:|---:|
| No token, infinite wait | 59.71 ns | 0 B |
| Live non-canceled token, infinite wait | 46.56 ns | 88 B |
| No token, finite timeout | 81.28 ns | 128 B |
| AsyncSharp atomic four-permit wait | 63.04 ns | 0 B |
| BCL four sequential waits, non-atomic | 56.22 ns | 0 B |
| Nito four sequential waits, non-atomic | 83.14 ns | 0 B |

The live-token row being nominally faster than the no-token row is a short-iteration anomaly; the allocation columns are the useful signal. Only AsyncSharp's multi-permit row is atomic. The BCL and Nito rows can partially acquire permits and are not semantic replacements.

### End-to-end prequeued batch release

Each cell is mean time / allocation for one complete batch, including task-array creation, synchronous enqueue validation, release, continuation drain, and `Task.WhenAll`.

| Waiters | BCL | Nito | AsyncSharp strict FIFO | AsyncSharp default FIFO-unfair |
|---:|---:|---:|---:|---:|
| 1 | 0.120 us / 200 B | 0.263 us / 272 B | 0.509 us / 488 B | 0.556 us / 488 B |
| 16 | 0.971 us / 1,640 B | 2.031 us / 2,792 B | 3.724 us / 4,568 B | 4.158 us / 4,568 B |
| 64 | 2.780 us / 6,248 B | 7.927 us / 10,856 B | 11.779 us / 17,624 B | 16.203 us / 17,624 B |
| 256 | 10.797 us / 24,680 B | 31.425 us / 43,112 B | 48.206 us / 69,848 B | 87.499 us / 69,848 B |

Strict-FIFO allocation fell from 77.49 KiB to 17,624 B at 64 waiters and from 308.51 KiB to 69,848 B at 256 waiters. Those are 77.79% and 77.89% reductions. Strict-FIFO elapsed time also fell directionally by 75.5% and 67.2% in those historical comparisons.

### Isolated queue stages

These fixtures isolate different operations and must not be added together. Setup and cleanup intentionally move outside the timed region for enqueue and release-call rows.

| Stage | Waiters | Strict FIFO | Default FIFO-unfair |
|---|---:|---:|---:|
| Enqueue pending waiters | 64 | 12.560 us / 17,072 B | 15.357 us / 17,072 B |
| Enqueue pending waiters | 256 | 42.620 us / 67,760 B | 74.946 us / 67,760 B |
| Release call only | 64 | 6.069 us / 112 B | 5.713 us / 112 B |
| Release call only | 256 | 12.869 us / 112 B | 12.293 us / 112 B |
| End-to-end continuation drain | 64 | 11.779 us / 17,624 B | 16.203 us / 17,624 B |
| End-to-end continuation drain | 256 | 48.206 us / 69,848 B | 87.499 us / 69,848 B |
| Cancel and drain | 64 | 298.00 us / 159,768 B | 297.89 us / 159,680 B |
| Cancel and drain | 256 | 1,180.89 us / 631,056 B | 1,109.81 us / 631,056 B |

For context, Nito's cancel-and-drain rows measured 72.91 us / 7,000 B at 64 and 494.25 us / 25,480 B at 256. The BCL measured 597.15 us / 52,200 B and 1,429.67 us / 204,312 B. Cancellation remains allocation-heavy in AsyncSharp and is a follow-up opportunity distinct from the ordinary non-cancelable path.

### Mixed one- and four-permit queues

| Waiters | Strict FIFO | Default | HighToLow | Unfair | LowToHigh | Allocated |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 0.766 us | 0.745 us | 0.740 us | 0.830 us | 0.878 us | 704 B |
| 16 | 7.003 us | 7.045 us | 6.751 us | 6.560 us | 6.813 us | 6,512 B |
| 64 | 22.660 us | 28.803 us | 27.012 us | 27.652 us | 28.261 us | 25,616 B |
| 256 | 90.040 us | 196.808 us | 145.026 us | 189.474 us | 155.250 us | 102,032 B |

These policy rows are AsyncSharp-only comparisons. Strict numeric priority remains part of the semantics: an unsatisfied higher-priority bucket blocks lower-priority buckets.

## AsyncMutex

### Uncontended

| Category | Implementation | Mean | Allocated |
|---|---|---:|---:|
| Async core | BCL `SemaphoreSlim` | 20.83 ns | 0 B |
| Async core | AsyncSharp | 60.77 ns | 0 B |
| Sync core lower bound | BCL `Monitor` | 10.40 ns | 0 B |
| Sync core | BCL `SemaphoreSlim` | 19.99 ns | 0 B |
| Sync core | AsyncSharp | 60.77 ns | 0 B |
| Async lease | AsyncSharp | 49.42 ns | 200 B |
| Async lease | VS Threading | 49.92 ns | 0 B |
| Async lease | Nito | 50.04 ns | 320 B |
| Sync lease | Nito | 47.90 ns | 320 B |
| Sync lease | AsyncSharp | 84.28 ns | 40 B |

The core `Lock` and `LockAsync` paths now allocate 0 B. Directionally, their historical means fell by 60.0% and 80.9%. Async and sync lease allocation fell by 82.1% and 91.9%, to 200 B and 40 B.

### Prequeued serialized handoffs

| Scenario | Waiters | AsyncSharp | Comparator(s) |
|---|---:|---:|---:|
| Core handoffs | 64 | 87.594 us / 29,504 B | BCL 50.758 us / 13,592 B |
| Core handoffs | 256 | 336.872 us / 117,056 B | BCL 237.891 us / 53,528 B |
| Lease handoffs | 64 | 105.129 us / 39,960 B | Nito 69.492 us / 33,888 B; VS 77.901 us / 20,800 B |
| Lease handoffs | 256 | 351.203 us / 158,232 B | Nito 214.582 us / 133,728 B; VS 210.585 us / 83,776 B |

At the historically recorded 16- and 64-waiter lease sizes, AsyncSharp allocation is about 56.6% lower than before. Timing uncertainty is broad enough that close handoff rankings should not be inferred.

`Monitor` is a synchronous, recursive, thread-affine lower bound and cannot be held across an `await`; it is not an AsyncMutex replacement.

## ReadersWriterAsyncLock

### Uncontended async acquisition

| Scenario | AsyncSharp | Nito | VS Threading |
|---|---:|---:|---:|
| Read | 43.91 ns / 200 B | 52.12 ns / 320 B | 279.62 ns / 208 B |
| Write | 45.08 ns / 200 B | 88.80 ns / 496 B | 653.56 ns / 417 B |
| Upgrade | 140.69 ns / 240 B | n/a | 1,243.97 ns / 625 B |

The fixed-workload warnings apply here as well. Several queued and mixed rows have broad variance, so treat the large differences and allocation reductions as signals, not close rankings.

Synchronous BCL lower bounds measured 14.77 ns / 0 B for reads, 11.93 ns / 0 B for writes, and 23.77 ns / 0 B for upgrades. AsyncSharp measured 83.41 ns / 40 B, 82.62 ns / 40 B, and 110.48 ns / 96 B. `ReaderWriterLockSlim` is thread-affine and is not an async replacement.

### Prequeued transitions

| Transition | Waiters | AsyncSharp | Nito | VS Threading |
|---|---:|---:|---:|---:|
| Readers after writer | 64 | 59.358 us / 34.59 KiB | 52.386 us / 33.26 KiB | 194.017 us / 33.82 KiB |
| Readers after writer | 256 | 256.070 us / 136.59 KiB | 210.156 us / 130.77 KiB | 567.994 us / 132.82 KiB |
| Writers after reader | 64 | 114.316 us / 39.02 KiB | 92.575 us / 49.59 KiB | 201.748 us / 38.05 KiB |
| Writers after reader | 256 | 313.587 us / 154.52 KiB | 388.859 us / 196.59 KiB | 388.105 us / 150.55 KiB |

The 1- and 16-waiter rows are retained in the artifact. AsyncSharp's 64-to-256 scaling is 4.31x for readers after a writer and 2.74x for writers after a reader.

### Sustained reader/writer traffic

Each invocation starts 20 workers, runs ten operations per worker, and yields inside the protected region. The mixes contain exactly 200 operations.

| Mix | AsyncSharp | Nito | VS Threading |
|---|---:|---:|---:|
| 90% reads / 10% writes | 0.4477 ms / 141.58 KiB | 0.3705 ms / 112.58 KiB | 1.1080 ms / 106.33 KiB |
| 50% reads / 50% writes | 0.3239 ms / 136.70 KiB | 0.3424 ms / 137.59 KiB | 0.9434 ms / 129.16 KiB |

Replacing the ordinary async state-lock retry delay with bounded spinning followed by signal-driven `Monitor` parking removed the tens-of-milliseconds stall. Versus v4, AsyncSharp is about 131x faster at 90/10 and 143x faster at 50/50, reductions of 99.24% and 99.30%. It is now 1.21x slower than Nito and 2.47x faster than VS Threading at 90/10; at 50/50 it is 1.06x faster than Nito and 2.91x faster than VS Threading. Allocation is 25.8% above Nito at 90/10 and 0.6% below Nito at 50/50. The mixed rows remain variable, so the close Nito comparisons are directional.

## Measurement limits

- Every class uses an explicit invocation bound. BenchmarkDotNet therefore reports minimum-iteration warnings because observed iterations can be much shorter than the Full job's requested 100 ms. The run still supplies 15 measured samples across three fresh launches, but its latency values are engineering-directional rather than publication-grade.
- The completed v6 run is the curated artifact because it satisfies the requirement that the full suite finish in minutes. The earlier v3 run used adaptive microbenchmark calibration and took 26 minutes 31 seconds, about 20 minutes of which came from allocation-heavy reader/writer microbenchmarks. No deadlock occurred in either run.
- The tree was intentionally uncommitted while measuring. The manifest records its exact source snapshot hash. Results must not be attributed to the HEAD commit alone.
- Executable and benchmark sources were unchanged from the preceding v5 run; only `RESULTS.md` changed within the manifest's hashed scope. Several timing rows nevertheless moved by more than 10% while allocations stayed stable, reinforcing that close timing differences are run-to-run noise.
- There is no retained pre-change Full run, so the final no-regression gate cannot be statistically proved. Historical timing deltas compare different profiles and are only directional; allocation deltas and current absolute values are more trustworthy.
- Release-call rows use one invocation per iteration and are especially noisy. Several reports contain outlier removal or multimodal warnings. Do not use close rankings from those rows.
- Queue-drain rows measure queue transition and continuation cost from one producer, not sustained parallel producer throughput.
- The BCL does not guarantee `SemaphoreSlim` waiter order. Nito uses FIFO queues. AsyncSharp exposes five policies and atomic multi-permit acquisition.
- Microsoft.VisualStudio.Threading includes ambient ownership, nesting, diagnostics, and deadlock-management semantics absent from the other implementations.
- AsyncSharp and Nito use `netstandard2.0` assets in this .NET 8 process; Microsoft.VisualStudio.Threading supplies a native `net8.0` asset.
- Empty critical sections isolate synchronization overhead. Real protected work reduces relative differences.

## Reproduce

Run the complete bounded Full suite from the repository root:

```powershell
dotnet run --project AsyncSharp.Benchmarks -c Release -- --profile Full --run-id local-full
```

Run a shorter development pass or a smoke-only discovery pass:

```powershell
dotnet run --project AsyncSharp.Benchmarks -c Release -- --profile Short
dotnet run --project AsyncSharp.Benchmarks -c Release -- --profile Dry --list flat
```

Every invocation writes to a new timestamp-and-commit directory under `BenchmarkDotNet.Artifacts/runs/` and records its manifest before executing. `Short` and `Dry` runs never overwrite or replace this curated Full snapshot.
