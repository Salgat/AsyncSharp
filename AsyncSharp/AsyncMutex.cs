using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AsyncSharp
{
    /// <summary>
    /// Async friendly mutex. Unlike a normal CLR lock, this is not re-entrant.
    /// </summary>
    public class AsyncMutex
    {
        private readonly AsyncSemaphore _asyncSemaphore = new AsyncSemaphore(1, 1, true);

        public AsyncMutex() { }

        /// <summary>
        /// Synchronously acquires lock.
        /// </summary>
        public void Lock()
            => _asyncSemaphore.Wait();

        /// <summary>
        /// Synchronously acquires lock.
        /// </summary>
        /// <param name="timeout"></param>
        public bool Lock(int timeout)
            => _asyncSemaphore.Wait(1, timeout);

        /// <summary>
        /// Synchronously acquires lock.
        /// </summary>
        /// <param name="cancellationToken"></param>
        public void Lock(CancellationToken cancellationToken)
            => _asyncSemaphore.Wait(cancellationToken);

        /// <summary>
        /// Synchronously acquires lock.
        /// </summary>
        /// <param name="timeout"></param>
        /// <param name="cancellationToken"></param>
        public bool Lock(int timeout, CancellationToken cancellationToken)
            => _asyncSemaphore.Wait(1, timeout, cancellationToken);

        /// <summary>
        /// Asynchronously acquires lock.
        /// </summary>
        /// <returns></returns>
        public Task LockAsync()
            => _asyncSemaphore.WaitAsync();

        /// <summary>
        /// Asynchronously acquires lock.
        /// </summary>
        /// <param name="timeout"></param>
        /// <returns></returns>
        public Task<bool> LockAsync(int timeout)
            => _asyncSemaphore.WaitAsync(1, timeout);

        /// <summary>
        /// Asynchronously acquires lock.
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public Task LockAsync(CancellationToken cancellationToken)
            => _asyncSemaphore.WaitAsync(cancellationToken);

        /// <summary>
        /// Asynchronously acquires lock.
        /// </summary>
        /// <param name="timeout"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public Task<bool> LockAsync(int timeout, CancellationToken cancellationToken)
            => _asyncSemaphore.WaitAsync(1, timeout, cancellationToken);

        /// <summary>
        /// Releases lock.
        /// </summary>
        public void Unlock()
            => _asyncSemaphore.Release();
        
        /// <summary>
        /// Synchronously acquires lock, then on dispose releases lock.
        /// </summary>
        /// <returns>Disposable object that releases lock on dispose.</returns>
        public IDisposable LockAndUnlock()
            => _asyncSemaphore.WaitAndRelease();

        /// <summary>
        /// Synchronously acquires lock, then on dispose releases lock.
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns>Disposable object that releases lock on dispose.</returns>
        public IDisposable LockAndUnlock(CancellationToken cancellationToken)
            => _asyncSemaphore.WaitAndRelease(cancellationToken);

        /// <summary>
        /// Asynchronously acquires lock, then on dispose releases lock.
        /// </summary>
        /// <returns>Disposable object that releases lock on dispose.</returns>
        public Task<IDisposable> LockAndUnlockAsync()
            => _asyncSemaphore.WaitAndReleaseAsync();

        /// <summary>
        /// Asynchronously acquires lock, then on dispose releases lock.
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns>Disposable object that releases lock on dispose.</returns>
        public Task<IDisposable> LockAndUnlockAsync(CancellationToken cancellationToken)
            => _asyncSemaphore.WaitAndReleaseAsync(cancellationToken);
    }
}
