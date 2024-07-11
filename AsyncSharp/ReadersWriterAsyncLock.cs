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
    public class ReadersWriterAsyncLock
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
            FirstInFirstOutUnfair
        }

        public sealed class UpgradeableReaderAsyncLock : IDisposable
        {
            internal readonly ReadersWriterAsyncLock _readersWriterAsyncLock;
            private readonly Action _disposeAction;

            internal UpgradeableReaderAsyncLock(ReadersWriterAsyncLock readersWriterAsyncLock, Action disposeAction)
            {
                _readersWriterAsyncLock = readersWriterAsyncLock;
                _disposeAction = disposeAction;
            }

            public IDisposable UpgradeToWriter()
                => UpgradeToWriter(CancellationToken.None);

            public IDisposable UpgradeToWriter(CancellationToken cancellationToken)
                => _readersWriterAsyncLock.AcquireReaders(_readersWriterAsyncLock.MaxReaders - 1, cancellationToken);
            
            public Task<IDisposable> UpgradeToWriterAsync()
                => UpgradeToWriterAsync(CancellationToken.None);

            public Task<IDisposable> UpgradeToWriterAsync(CancellationToken cancellationToken)
                => _readersWriterAsyncLock.AcquireReadersAsync(_readersWriterAsyncLock.MaxReaders - 1, cancellationToken);

            public void Dispose()
            {
                _disposeAction();
            }
        }

        public int MaxReaders { get; }

        internal readonly AsyncSemaphore _asyncSemaphore;

        /// <summary>
        /// Allows for int.MaxValue readers with fair ordering of lock acquisition.
        /// </summary>
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

        public ReadersWriterAsyncLock(int maxReaders, LockPriority lockPriority)
        {
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
                default:
                    throw new ArgumentException($"{nameof(LockPriority)} value '{lockPriority}' not recognized.");
            }
            _asyncSemaphore = new AsyncSemaphore(maxReaders, maxReaders, waiterPriority);
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

            return new UpgradeableReaderAsyncLock(this, _asyncSemaphore.WaitAndRelease(1, cancellationToken).Dispose);
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
            => new UpgradeableReaderAsyncLock(this, (await _asyncSemaphore.WaitAndReleaseAsync(readerCount, cancellationToken).ConfigureAwait(false)).Dispose);

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
