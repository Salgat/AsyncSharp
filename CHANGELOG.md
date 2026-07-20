# Changelog

## 1.4.3 - 2026-07-19

### Fixed

- Hardened `AsyncSemaphore` release arithmetic, waiter cleanup, queue progression, and zero-count acquisition behavior.
- Made pending synchronous and asynchronous waits complete predictably when a semaphore is disposed.
- Made disposable acquisitions idempotent and safe across `ReleaseAll` resets.
- Corrected multi-count upgradeable-reader accounting and eliminated writer/upgrade deadlocks with single-owner, lifecycle-aware upgrades.
- Added consistent validation for semaphore priorities, reader limits, and upgradeable-reader counts.

### Changed

- Expanded bounded regression and concurrency coverage for cancellation, disposal, ordering, and lease cleanup.
- Added package compatibility validation against 1.4.2 and a standalone local-package smoke consumer.
- Hardened CI with pinned actions, Windows and Linux validation, job timeouts, hang diagnostics, and tag-scoped NuGet publishing.
