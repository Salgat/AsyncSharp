# Changelog

## 2.0.0 - 2026-07-21

### Fixed

- Hardened `AsyncSemaphore` release arithmetic, waiter cleanup, queue progression, and zero-count acquisition behavior.
- Made pending synchronous and asynchronous waits complete predictably when a semaphore is disposed.
- Made disposable acquisitions idempotent and safe across `ReleaseAll` resets.
- Corrected multi-count upgradeable-reader accounting and eliminated writer/upgrade deadlocks with single-owner, lifecycle-aware upgrades.
- Added consistent validation for semaphore priorities, reader limits, and upgradeable-reader counts.

### Changed

- Reworked `AsyncSemaphore` acquisition so uncontended core synchronous and asynchronous waits complete without allocation while preserving Task-based APIs and `netstandard2.0` support.
- Replaced array-shifting waiter lists with intrusive priority buckets, making FIFO insertion, head removal, and cancellation unlinking constant-time.
- Removed ordinary non-cancelable async-wait timeout infrastructure and replaced state-lock retry polling with bounded spinning followed by signal-driven monitor parking.
- Replaced closure-backed disposable acquisitions with compact, idempotent leases that preserve release-epoch behavior across resets and disposal.
- Propagated the semaphore improvements through `AsyncMutex` and `ReadersWriterAsyncLock` without introducing separate synchronization engines.
- Added reproducible Full, Short, and Dry benchmark profiles, isolated queue-stage and sustained reader/writer workloads, timestamped source manifests, and curated Full-run results.
- Expanded deterministic ordering, priority, cancellation, timeout, disposal, `ReleaseAll`, lease, and reader/writer regression coverage.
- Expanded bounded regression and concurrency coverage for cancellation, disposal, ordering, and lease cleanup.
- Added package compatibility validation against 1.4.2 and a standalone local-package smoke consumer.
- Hardened CI with pinned actions, Windows and Linux validation, job timeouts, hang diagnostics, and tag-scoped NuGet publishing.

There are no intentional public API breaks in 2.0.0; the major version marks the scope of the internal performance and scheduling overhaul.
