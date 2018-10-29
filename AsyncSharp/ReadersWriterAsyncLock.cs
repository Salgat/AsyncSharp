using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AsyncSharp
{
    /// <summary>
    /// Provides a readers–writer lock with both synchronous and asynchronous lock acquire methods.
    /// </summary>
    public class ReadersWriterAsyncLock
    {
        public sealed class UpgradeableReaderAsyncLock : IDisposable
        {
            private readonly ReadersWriterAsyncLock _readersWriterAsyncLock;
            private readonly Action _disposeAction;
            private readonly int _readerCount;

            public int ReaderCount => _readerCount;

            internal UpgradeableReaderAsyncLock(ReadersWriterAsyncLock readersWriterAsyncLock, int readerCount, Action disposeAction)
            {
                _readersWriterAsyncLock = readersWriterAsyncLock;
                _readerCount = readerCount;
                _disposeAction = disposeAction;
            }

            public IDisposable UpgradeToWriter()
                => UpgradeToWriter(CancellationToken.None);

            public IDisposable UpgradeToWriter(CancellationToken cancellationToken)
                => _readersWriterAsyncLock.AcquireReaders(_readersWriterAsyncLock.MaxReaders - _readerCount, CancellationToken.None);
            
            public Task<IDisposable> UpgradeToWriterAsync()
                => UpgradeToWriterAsync(CancellationToken.None);

            public Task<IDisposable> UpgradeToWriterAsync(CancellationToken cancellationToken)
                => _readersWriterAsyncLock.AcquireReadersAsync(_readersWriterAsyncLock.MaxReaders - _readerCount, CancellationToken.None);

            public void Dispose()
            {
                _disposeAction();
            }
        }

        public int MaxReaders { get; }

        private readonly AsyncSemaphore _asyncSemaphore;

        public ReadersWriterAsyncLock() : this(int.MaxValue, true) { }
        
        public ReadersWriterAsyncLock(bool fair) : this(int.MaxValue, fair) { }

        public ReadersWriterAsyncLock(int maxReaders) : this(maxReaders, true) { }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="maxReaders">Maximum number of readers that can acquire the lock simultaenously.</param>
        /// <param name="fair">If true, no new readers can acquire the lock until the writer's requested lock is acquired. 
        /// Use this if writer starvation due to high contention is a concern.</param>
        public ReadersWriterAsyncLock(int maxReaders, bool fair)
        {
            MaxReaders = maxReaders;
            _asyncSemaphore = new AsyncSemaphore(maxReaders, maxReaders, fair);
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
            if (readerCount > MaxReaders)
            {
                throw new ArgumentOutOfRangeException($"'{nameof(readerCount)}' cannot exceed '{nameof(MaxReaders)}'.");
            }

            return new UpgradeableReaderAsyncLock(this, readerCount, _asyncSemaphore.WaitAndRelease(1, cancellationToken).Dispose);
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
            => new UpgradeableReaderAsyncLock(this, 1, (await _asyncSemaphore.WaitAndReleaseAsync(readerCount, cancellationToken).ConfigureAwait(false)).Dispose);

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
    }
}
