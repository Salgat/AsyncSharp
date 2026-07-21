# AsyncSharp benchmarks

This project compares the current AsyncSharp source with the closest commonly used .NET alternatives:

- `SemaphoreSlim` and `ReaderWriterLockSlim` from the .NET base class library
- `Nito.AsyncEx.Coordination` 5.1.2
- `Microsoft.VisualStudio.Threading.Only` 18.7.23

The harness uses BenchmarkDotNet 0.15.8 and targets .NET 8. See [RESULTS.md](RESULTS.md) for the latest checked-in measurement snapshot.

## Reproducible profiles

The harness owns three explicit profiles. Do not pass BenchmarkDotNet's `--job` option; select a profile instead.

| Profile | Launches | Warmups per launch | Measurements per launch | Target time per iteration | Use |
|---|---:|---:|---:|---:|---|
| `Full` | 3 | 16 | 5 | 100 ms | Recording a durable comparison |
| `Short` | 1 | 16 | 3 | 25 ms | Directional development feedback |
| `Dry` | 1 | 1 | 1 cold-start iteration | n/a | Discovery and smoke validation only |

Full therefore records 15 measured samples across three fresh process launches. Its 100 ms target and minimum follow BenchmarkDotNet's guidance for stable microbenchmark iterations, while remaining bounded. Sixteen warmups per launch allow tiered compilation to settle. Benchmarks with an explicit `InvocationCount` intentionally use that fixed workload instead; a single unusually slow invocation can exceed the target.

Uncontended microbenchmarks also avoid adaptive invocation calibration: each iteration executes 256 benchmark-method invocations containing 256 operations apiece, or 65,536 logical acquire/release operations. This keeps allocation-heavy comparator rows from expanding into hundreds of gigabytes of transient allocation while still producing more than one million warmup operations per launch.

Stateful end-to-end contention scenarios use their own fixed workloads. Semaphore, mutex, mixed-permit, and reader/writer queue-drain cases execute exactly 64 complete scenarios per iteration with an unroll factor of one. Isolated enqueue, release, and cancellation batches execute once per iteration, as does each sustained 200-operation mixed reader/writer scenario. BenchmarkDotNet continues to normalize reported time and allocation to one logical operation or complete scenario; these bounds prevent a single iteration from creating millions of waiters or retaining hundreds of megabytes of transient state.

`Full` is the default. Every invocation writes to a new directory under:

```text
BenchmarkDotNet.Artifacts/runs/<UTC timestamp>-<short commit>-<profile>[-<run-id>]/
```

The UTC timestamp, Git commit, and profile are always present. Use `--run-id baseline-before-fast-path` to append an optional descriptive label. The harness refuses to reuse an existing path, so a validation cannot overwrite an earlier report.

When neither `--filter` nor `--list` is supplied, the harness adds `--filter *` and runs every benchmark instead of opening BenchmarkDotNet's interactive selector. Explicit filters and listing commands are passed through unchanged.

Each run directory starts with a `run-manifest.json` that records the HEAD commit, whether the source tree is dirty, a SHA-256 snapshot of `AsyncSharp/**` and `AsyncSharp.Benchmarks/**`, runtime, OS, CPU, logical processor count, process affinity, the host's active Windows power scheme before the run, BenchmarkDotNet version and arguments, and the explicit job settings. Every profile explicitly configures BenchmarkDotNet's `HighPerformance` power plan, which is recorded separately from the pre-run host scheme. Git and host power-scheme detection degrade to `unknown` when unavailable; the manifest records but does not enforce processor affinity.

Run the complete durable suite from the repository root:

```powershell
dotnet run --project AsyncSharp.Benchmarks -c Release -- --profile Full
```

Run a shorter comparison or a discovery-only validation:

```powershell
dotnet run --project AsyncSharp.Benchmarks -c Release -- --profile Short
dotnet run --project AsyncSharp.Benchmarks -c Release -- --profile Dry --list flat
```

BenchmarkDotNet arguments such as filters still pass through normally:

```powershell
dotnet run --project AsyncSharp.Benchmarks -c Release -- --profile Short --filter "*Semaphore*"
dotnet run --project AsyncSharp.Benchmarks -c Release -- --profile Short --filter "*Mutex*"
dotnet run --project AsyncSharp.Benchmarks -c Release -- --profile Short --filter "*ReaderWriter*"
```

Run Release builds without a debugger on an otherwise idle machine with a fixed power policy. For durable comparisons, keep the runtime, package versions, processor affinity, and machine configuration unchanged and retain the complete run-scoped CSV, Markdown, HTML, and log output.

## What is measured

- Uncontended synchronous and asynchronous core acquire/release cost
- Disposable-lease acquisition where libraries expose that API, grouped separately from core operations
- AsyncSharp fast-path cost with no token, a live cancelable token, and a finite timeout
- End-to-end semaphore batch release and serialized mutex queue drain at 1, 16, 64, and 256 waiters
- Isolated semaphore enqueue and release-call latency at 1, 16, 64, and 256 waiters, with setup or cleanup outside the timed operation
- Cancellation of 1, 16, 64, and 256 prequeued cancellable semaphore waiters
- Reader, writer, and upgradeable-reader acquisition, plus prequeued transitions at 1, 16, 64, and 256 waiters
- Sustained reader/writer workloads with 20 workers and ten operations per worker, producing exact 90/10 and 50/50 reader/writer mixes
- Mixed one- and four-permit semaphore queues at 1, 16, 64, and 256 waiters, released one permit at a time under strict FIFO, default, `Unfair`, `LowToHigh`, and `HighToLow` policies
- AsyncSharp's atomic four-permit acquisition versus explicitly labelled sequential, non-atomic emulations

Semantically comparable groups declare a BenchmarkDotNet baseline and report ratio columns. Feature-cost and lower-bound rows that are not interchangeable remain explicitly labelled instead of presenting a ratio as equivalence.

## Interpretation caveats

- `Dry` is a cold-start smoke mode. Its timing, rankings, error-free appearance, and one-time allocation costs must never be treated as performance results.
- `Short` has only three measured iterations from one launch. It can identify large signals but cannot defend close rankings; record release claims only from `Full`, which uses 15 measurements across three launches.
- Queue-drain benchmarks measure a complete batch: task-array creation, waiter enqueue, pending-state validation, release, continuation scheduling, and `Task.WhenAll`. Compare libraries only within the same waiter count.
- `SemaphoreEnqueueBenchmarks` creates the semaphore and task array during iteration setup, times only the batch of `WaitAsync` calls and array stores, then verifies that every task is pending and releases and drains the queue during iteration cleanup.
- `SemaphoreReleaseCallBenchmarks` prequeues and validates waiters in iteration setup, measures one `Release(waiterCount)` call, and waits for completion during iteration cleanup.
- `SemaphoreCancellationBatchBenchmarks` prequeues and validates cancellable waiters in iteration setup, then measures `CancellationTokenSource.Cancel()` through completion and canceled-state validation of the entire waiter batch. Iteration cleanup only disposes per-iteration resources. It compares equivalent one-permit cancellable waits; it does not represent timeout storms or cancel/grant races.
- Uncontended rows use 256 benchmark-method invocations per iteration and report one of the 65,536 inner operations. Isolated enqueue, release, and cancellation batches use one invocation per iteration. Stateful end-to-end queue-drain cases use 64 invocations per iteration, while sustained mixed reader/writer traffic uses one. These explicit bounds keep adaptive calibration from multiplying allocation-heavy or mutable scenarios into excessive work. Because fixed workloads can finish before the profile's 100 ms target, use the 15 cross-launch samples, disclose broad error or multimodal distributions, and compare only equivalent scenarios and parameter values.
- The sustained reader/writer benchmarks intentionally yield inside the protected region to create overlap. They include task scheduling and represent two controlled mixes, not every application.
- `SemaphoreSlim` does not guarantee waiter ordering. Nito uses FIFO queues. AsyncSharp exposes several policies; `LowToHigh` and `HighToLow` appear only in the AsyncSharp mixed-permit policy comparison because competitors do not expose equivalent ordering controls.
- `Monitor` and `ReaderWriterLockSlim` are synchronous, thread-affine lower-bound baselines, not async replacements. `Monitor` is also recursive, unlike the compared mutex APIs.
- Nito's reader/writer lock has fixed writer preference and no upgradeable-reader API.
- Microsoft.VisualStudio.Threading carries ambient ownership, nesting, diagnostics, and deadlock-handling semantics that the other implementations do not. Its prequeued reader/writer rows also require `HideLocks()` while independent waiters are created.
- AsyncSharp and Nito both use `netstandard2.0` library assets in this .NET 8 benchmark. Microsoft.VisualStudio.Threading supplies a native `net8.0` asset.
- The BCL and Nito multi-permit rows perform four sequential waits and are not atomic. They measure the closest available emulation, not equivalent behavior.
- Empty critical sections intentionally isolate synchronization overhead. Real protected work reduces these relative differences.
- Results describe one source revision, runtime, operating system, processor, and machine state. They are not universal package rankings.

## Recording results

Before updating [RESULTS.md](RESULTS.md):

1. Start from a clean tree and record both the AsyncSharp and benchmark-harness revisions.
2. Run the `Full` profile with a descriptive `--run-id` on the controlled machine.
3. Review `run-manifest.json`, errors, standard deviations, ratios, allocation columns, and the full log; do not compare runs whose source hash or environmental metadata differs unintentionally, and do not copy a ranking that is inside the run's uncertainty.
4. Preserve the complete run-scoped artifacts and curate the human-readable snapshot from that same directory.
5. Never replace a `Full` snapshot with output from `Short` or `Dry`.
