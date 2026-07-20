/*
MIT License

Copyright (c) 2024 Austin Salgat

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

using System;
using System.Threading;
using System.Threading.Tasks;

namespace AsyncSharp
{
    /// <summary>
    /// Provides a readers–writer lock with both synchronous and asynchronous lock acquire methods.
    /// </summary>
    public class ReadersWriterAsyncLock : IDisposable
    {
        public enum LockPriority
        {
            /// <summary>
            /// Readers always have priority for acquiring the lock.
            /// </summary>
            Readers,

            /// <summary>
            /// Writers always have priority for acquiring the lock.
            /// </summary>
            Writers,

            /// <summary>
            /// Lock acquisition is done in the order the requests were made, regardless of whether a reader or writer.
            /// </summary>
            FirstInFirstOut,

            /// <summary>
            /// Lock acquisition is done in the order the requests were made, regardless of whether a reader or writer,
            /// but a writer request will be skipped for any pending readers if any other reader is still holding a lock.
            /// </summary>
            FirstInFirstOutUnfair,

            /// <summary>
            /// Lock acquisition does not respect ordering, and priority may not be respected.
            /// </summary>
            Unfair
        }

        public sealed class UpgradeableReaderAsyncLock : IDisposable
        {
            private enum UpgradeState
            {
                Ready,
                AcquiringUpgrade,
                Upgraded,
                Disposed
            }

            private sealed class UpgradedWriterLock : IDisposable
            {
                private UpgradeableReaderAsyncLock _owner;
                private readonly int _acquiredCount;

                public UpgradedWriterLock(UpgradeableReaderAsyncLock owner, int acquiredCount)
                {
                    _owner = owner;
                    _acquiredCount = acquiredCount;
                }

                public void Dispose()
                {
                    var owner = Interlocked.Exchange(ref _owner, null);
                    if (owner == null) return;

                    owner.ReleaseUpgrade(_acquiredCount);
                }
            }

            internal readonly ReadersWriterAsyncLock _readersWriterAsyncLock;
            private readonly int _readerCount;
            private readonly object _stateLock = new object();
            private UpgradeState _state = UpgradeState.Ready;

            internal UpgradeableReaderAsyncLock(ReadersWriterAsyncLock readersWriterAsyncLock, int readerCount)
            {
                _readersWriterAsyncLock = readersWriterAsyncLock;
                _readerCount = readerCount;
            }

            public IDisposable UpgradeToWriter()
                => UpgradeToWriter(CancellationToken.None);

            public IDisposable UpgradeToWriter(CancellationToken cancellationToken)
            {
                var countToAcquire = BeginUpgrade();
                var acquired = false;
                try
                {
                    _readersWriterAsyncLock.AcquireUpgrade(countToAcquire, cancellationToken);
                    acquired = true;

                    var upgradedWriterLock = new UpgradedWriterLock(this, countToAcquire);
                    CompleteUpgrade();
                    return upgradedWriterLock;
                }
                catch
                {
                    try
                    {
                        if (acquired)
                        {
                            _readersWriterAsyncLock.ReleaseUpgrade(countToAcquire);
                        }
                    }
                    finally
                    {
                        FailUpgrade();
                    }

                    throw;
                }
            }

            public Task<IDisposable> UpgradeToWriterAsync()
                => UpgradeToWriterAsync(CancellationToken.None);

            public async Task<IDisposable> UpgradeToWriterAsync(CancellationToken cancellationToken)
            {
                var countToAcquire = BeginUpgrade();
                var acquired = false;
                try
                {
                    await _readersWriterAsyncLock.AcquireUpgradeAsync(countToAcquire, cancellationToken).ConfigureAwait(false);
                    acquired = true;

                    var upgradedWriterLock = new UpgradedWriterLock(this, countToAcquire);
                    CompleteUpgrade();
                    return upgradedWriterLock;
                }
                catch
                {
                    try
                    {
                        if (acquired)
                        {
                            _readersWriterAsyncLock.ReleaseUpgrade(countToAcquire);
                        }
                    }
                    finally
                    {
                        FailUpgrade();
                    }

                    throw;
                }
            }

            public void Dispose()
            {
                lock (_stateLock)
                {
                    if (_state == UpgradeState.Disposed) return;
                    if (_state == UpgradeState.AcquiringUpgrade || _state == UpgradeState.Upgraded)
                    {
                        throw new InvalidOperationException(
                            "Cannot dispose an upgradeable reader while a writer upgrade is pending or active. " +
                            "Dispose the upgraded writer before disposing the upgradeable reader.");
                    }

                    _state = UpgradeState.Disposed;
                }

                _readersWriterAsyncLock.ReleaseUpgradeableReader(_readerCount);
            }

            private int BeginUpgrade()
            {
                lock (_stateLock)
                {
                    if (_state == UpgradeState.Disposed)
                    {
                        throw new ObjectDisposedException(nameof(UpgradeableReaderAsyncLock));
                    }
                    if (_state != UpgradeState.Ready)
                    {
                        throw new InvalidOperationException(
                            "A writer upgrade is already pending or active for this upgradeable reader.");
                    }

                    _state = UpgradeState.AcquiringUpgrade;
                    return _readersWriterAsyncLock.MaxReaders - _readerCount;
                }
            }

            private void CompleteUpgrade()
            {
                lock (_stateLock)
                {
                    if (_state != UpgradeState.AcquiringUpgrade)
                    {
                        throw new InvalidOperationException("The upgradeable reader is not awaiting a writer upgrade.");
                    }

                    _state = UpgradeState.Upgraded;
                }
            }

            private void FailUpgrade()
            {
                lock (_stateLock)
                {
                    if (_state == UpgradeState.AcquiringUpgrade)
                    {
                        _state = UpgradeState.Ready;
                    }
                }
            }

            private void ReleaseUpgrade(int acquiredCount)
            {
                try
                {
                    _readersWriterAsyncLock.ReleaseUpgrade(acquiredCount);
                }
                finally
                {
                    lock (_stateLock)
                    {
                        if (_state == UpgradeState.Upgraded)
                        {
                            _state = UpgradeState.Ready;
                        }
                    }
                }
            }
        }

        private const int UpgradePriority = int.MaxValue;

        public int MaxReaders { get; }

        internal readonly AsyncSemaphore _asyncSemaphore;
        private readonly AsyncSemaphore _upgradeableReaderSemaphore;

        /// <summary>
        /// Allows for int.MaxValue readers with fair ordering of lock acquisition.
        /// </summary>
        public ReadersWriterAsyncLock() : this(int.MaxValue, true) { }
        
        public ReadersWriterAsyncLock(bool fair) : this(int.MaxValue, fair) { }

        public ReadersWriterAsyncLock(int maxReaders) : this(maxReaders, true) { }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="maxReaders">Maximum number of readers that can acquire the lock simultaneously.</param>
        /// <param name="fair">If true, no new readers can acquire the lock until the writer's requested lock is acquired.
        /// Use this if writer starvation due to high contention is a concern.</param>
        public ReadersWriterAsyncLock(int maxReaders, bool fair)
        {
            ValidateMaxReaders(maxReaders);
            MaxReaders = maxReaders;
            _asyncSemaphore = new AsyncSemaphore(maxReaders, maxReaders, fair);
            _upgradeableReaderSemaphore = new AsyncSemaphore(
                1,
                1,
                AsyncSemaphore.WaiterPriority.FirstInFirstOut);
        }

        public ReadersWriterAsyncLock(int maxReaders, LockPriority lockPriority)
        {
            ValidateMaxReaders(maxReaders);
            MaxReaders = maxReaders;
            AsyncSemaphore.WaiterPriority waiterPriority;
            switch (lockPriority)
            {
                case LockPriority.Writers:
                    waiterPriority = AsyncSemaphore.WaiterPriority.HighToLow;
                    break;
                case LockPriority.Readers:
                    waiterPriority = AsyncSemaphore.WaiterPriority.LowToHigh;
                    break;
                case LockPriority.FirstInFirstOut:
                    waiterPriority = AsyncSemaphore.WaiterPriority.FirstInFirstOut;
                    break;
                case LockPriority.FirstInFirstOutUnfair:
                    waiterPriority = AsyncSemaphore.WaiterPriority.FirstInFirstOutUnfair;
                    break;
                case LockPriority.Unfair:
                    waiterPriority = AsyncSemaphore.WaiterPriority.Unfair;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(lockPriority),
                        lockPriority,
                        $"{nameof(LockPriority)} value '{lockPriority}' is not recognized.");
            }
            _asyncSemaphore = new AsyncSemaphore(maxReaders, maxReaders, waiterPriority);
            _upgradeableReaderSemaphore = new AsyncSemaphore(
                1,
                1,
                AsyncSemaphore.WaiterPriority.FirstInFirstOut);
        }

        #region Readers

        #region Synchronous

        public IDisposable AcquireReader()
            => AcquireReaders(1, CancellationToken.None);

        public IDisposable AcquireReader(CancellationToken cancellationToken)
            => AcquireReaders(1, cancellationToken);

        public IDisposable AcquireReaders(int count, CancellationToken cancellationToken)
            => _asyncSemaphore.WaitAndRelease(count, cancellationToken);

        public UpgradeableReaderAsyncLock AcquireUpgradeableReader()
            => AcquireUpgradeableReaders(1, CancellationToken.None);
        
        public UpgradeableReaderAsyncLock AcquireUpgradeableReader(CancellationToken cancellationToken)
            => AcquireUpgradeableReaders(1, cancellationToken);

        public UpgradeableReaderAsyncLock AcquireUpgradeableReaders(int readerCount)
            => AcquireUpgradeableReaders(readerCount, CancellationToken.None);

        public UpgradeableReaderAsyncLock AcquireUpgradeableReaders(int readerCount, CancellationToken cancellationToken)
        {
            ValidateReaderCount(readerCount);

            _upgradeableReaderSemaphore.Wait(cancellationToken);
            var readerAcquired = false;
            try
            {
                _asyncSemaphore.Wait(readerCount, cancellationToken);
                readerAcquired = true;
                return new UpgradeableReaderAsyncLock(this, readerCount);
            }
            catch
            {
                try
                {
                    if (readerAcquired)
                    {
                        _asyncSemaphore.Release(readerCount);
                    }
                }
                finally
                {
                    _upgradeableReaderSemaphore.Release();
                }

                throw;
            }
        }

        #endregion

        #region Asynchronous

        public Task<IDisposable> AcquireReaderAsync()
            => AcquireReadersAsync(1, CancellationToken.None);

        public Task<IDisposable> AcquireReaderAsync(CancellationToken cancellationToken)
            => AcquireReadersAsync(1, cancellationToken);

        public Task<IDisposable> AcquireReadersAsync(int count, CancellationToken cancellationToken)
            => _asyncSemaphore.WaitAndReleaseAsync(count, cancellationToken);

        public Task<UpgradeableReaderAsyncLock> AcquireUpgradeableReaderAsync()
            => AcquireUpgradeableReadersAsync(1, CancellationToken.None);

        public Task<UpgradeableReaderAsyncLock> AcquireUpgradeableReaderAsync(CancellationToken cancellationToken)
            => AcquireUpgradeableReadersAsync(1, cancellationToken);

        public Task<UpgradeableReaderAsyncLock> AcquireUpgradeableReadersAsync(int readerCount)
            => AcquireUpgradeableReadersAsync(readerCount, CancellationToken.None);

        public async Task<UpgradeableReaderAsyncLock> AcquireUpgradeableReadersAsync(int readerCount, CancellationToken cancellationToken)
        {
            ValidateReaderCount(readerCount);

            await _upgradeableReaderSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            var readerAcquired = false;
            try
            {
                await _asyncSemaphore.WaitAsync(readerCount, cancellationToken).ConfigureAwait(false);
                readerAcquired = true;
                return new UpgradeableReaderAsyncLock(this, readerCount);
            }
            catch
            {
                try
                {
                    if (readerAcquired)
                    {
                        _asyncSemaphore.Release(readerCount);
                    }
                }
                finally
                {
                    _upgradeableReaderSemaphore.Release();
                }

                throw;
            }
        }

        #endregion

        #endregion

        #region Writers

        public IDisposable AcquireWriter()
            => AcquireWriter(CancellationToken.None);

        public IDisposable AcquireWriter(CancellationToken cancellationToken)
            => _asyncSemaphore.WaitAndReleaseAll(cancellationToken);

        public Task<IDisposable> AcquireWriterAsync()
            => AcquireWriterAsync(CancellationToken.None);

        public Task<IDisposable> AcquireWriterAsync(CancellationToken cancellationToken)
            => _asyncSemaphore.WaitAndReleaseAllAsync(cancellationToken);

        #endregion

        public void Dispose()
        {
            try
            {
                _upgradeableReaderSemaphore.Dispose();
            }
            finally
            {
                _asyncSemaphore.Dispose();
            }
        }

        private static void ValidateMaxReaders(int maxReaders)
        {
            if (maxReaders <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxReaders),
                    $"'{nameof(maxReaders)}' must be greater than zero.");
            }
        }

        private void ValidateReaderCount(int readerCount)
        {
            if (readerCount <= 0 || readerCount > MaxReaders)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(readerCount),
                    $"'{nameof(readerCount)}' must be greater than zero and cannot exceed '{nameof(MaxReaders)}'.");
            }
        }

        private void AcquireUpgrade(int count, CancellationToken cancellationToken)
        {
            var acquired = _asyncSemaphore.Wait(
                count,
                AsyncSemaphore.InfiniteTimeSpan,
                UpgradePriority,
                cancellationToken);
            if (!acquired)
            {
                throw new InvalidOperationException("An infinite writer upgrade wait completed without acquiring the requested count.");
            }
        }

        private async Task AcquireUpgradeAsync(int count, CancellationToken cancellationToken)
        {
            var acquired = await _asyncSemaphore.WaitAsync(
                count,
                AsyncSemaphore.InfiniteTimeSpan,
                UpgradePriority,
                cancellationToken).ConfigureAwait(false);
            if (!acquired)
            {
                throw new InvalidOperationException("An infinite writer upgrade wait completed without acquiring the requested count.");
            }
        }

        private void ReleaseUpgrade(int count)
        {
            _asyncSemaphore.Release(count);
        }

        private void ReleaseUpgradeableReader(int readerCount)
        {
            try
            {
                _asyncSemaphore.Release(readerCount);
            }
            finally
            {
                _upgradeableReaderSemaphore.Release();
            }
        }
    }
}
