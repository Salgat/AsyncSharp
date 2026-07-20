# AsyncSharp benchmarks

This project compares the current AsyncSharp source with the closest commonly used .NET alternatives:

- `SemaphoreSlim` and `ReaderWriterLockSlim` from the .NET base class library
- `Nito.AsyncEx.Coordination` 5.1.2
- `Microsoft.VisualStudio.Threading.Only` 18.7.23

The benchmark harness uses BenchmarkDotNet 0.15.8 and targets .NET 8.

See [RESULTS.md](RESULTS.md) for the latest checked-in comparison run.

## Run

Run a quick development comparison:

```powershell
dotnet run --project AsyncSharp.Benchmarks -c Release -- --job short
```

Run only one area:

```powershell
dotnet run --project AsyncSharp.Benchmarks -c Release -- --job short --filter "*Semaphore*"
dotnet run --project AsyncSharp.Benchmarks -c Release -- --job short --filter "*Mutex*"
dotnet run --project AsyncSharp.Benchmarks -c Release -- --job short --filter "*ReaderWriter*"
```

Omit `--job short` for a full BenchmarkDotNet run suitable for recording results. Run Release builds without a debugger on an otherwise idle, fixed-power machine.

BenchmarkDotNet writes the full CSV, Markdown, HTML, and log output under `BenchmarkDotNet.Artifacts/`. Those machine-generated files are ignored by Git; retain curated results in `RESULTS.md`.

## What is measured

- Uncontended synchronous and asynchronous acquire/release cost
- Disposable-lease acquisition where libraries expose that API
- Prequeued semaphore batch release and serialized mutex queue drain
- Reader, writer, and upgradeable-reader acquisition
- Reader and writer queue transitions under contention
- AsyncSharp's atomic four-permit acquisition versus explicitly labelled sequential, non-atomic emulations

## Interpretation caveats

- `SemaphoreSlim` does not guarantee waiter ordering. Nito uses FIFO queues. AsyncSharp's default semaphore policy is `FirstInFirstOutUnfair`; its `Unfair` mode is reported separately for uncontended cost, and strict FIFO is reported separately for queued release.
- `Monitor` and `ReaderWriterLockSlim` are synchronous, thread-affine lower-bound baselines, not async replacements. `Monitor` is also recursive, unlike the compared mutex APIs.
- Nito's reader/writer lock has fixed writer preference and no upgradeable-reader API.
- Microsoft.VisualStudio.Threading carries ambient ownership, nesting, diagnostics, and deadlock-handling semantics that the other implementations do not.
- Nito resolves its `netstandard2.0` asset in this .NET 8 benchmark while Microsoft.VisualStudio.Threading supplies a native `net8.0` asset.
- The BCL and Nito multi-permit rows perform four sequential waits and are not atomic. They measure the cost of the closest available emulation, not equivalent behavior.
- Queue-drain results measure a prequeued transition, not sustained parallel throughput. They include the common task-array and `Task.WhenAll` orchestration cost; compare libraries only within the same waiter-count row.
- Results describe the measured runtime, operating system, processor, and power state. They are not universal performance guarantees.
