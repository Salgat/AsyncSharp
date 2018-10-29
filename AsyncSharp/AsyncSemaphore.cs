using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace AsyncSharp
{
    /// <summary>
    /// Provides an async friendly Semaphore with the intention of providing more features than SemaphoreSlim, including
    /// waiting on multiple count, atomically release all waiters, disposable wait and release, and fairness of the order
    /// that waiters are released based on the order of their wait.
    /// </summary>
    public class AsyncSemaphore
    {
        private volatile int _currentCount; // Count available to acquire (Waits asking for more than available are blocked)
        private readonly int _maxCount; // Max count that can be acquired
        private readonly bool _fair; // Whether ordering is respected for queued Acquire requests
        private readonly object _lock = new object(); // Grants exclusive access to _currentCount and _queuedAcquireRequests

        // Keeps track of all acquire waiters. Wait/WaitAsync can only add entries, and Release/ReleaseAll can only remove entries.
        private readonly IList<IQueuedAcquire> _queuedAcquireRequests = new List<IQueuedAcquire>();

        private interface IQueuedAcquire
        {
            int Count { get; }
            void GrantAcquire();
        }

        private sealed class QueuedSynchronousAcquire : IQueuedAcquire, IDisposable
        {
            public int Count { get; }
            private readonly ManualResetEventSlim _waitHandle = new ManualResetEventSlim(false);

            public QueuedSynchronousAcquire(int count)
            {
                Count = count;
            }

            public bool Wait(int timeout, CancellationToken cancellationToken)
            {
                if (timeout == int.MaxValue)
                {
                    _waitHandle.Wait(cancellationToken);
                    return true;
                }
                else if (timeout > 0)
                {
                    return _waitHandle.Wait(timeout, cancellationToken);
                }
                else
                {
                    return _waitHandle.Wait(0, cancellationToken);
                }
            }
            
            public void GrantAcquire()
            {
                // The waiter is blocking on a lock to this object
                _waitHandle.Set();
            }

            public void Dispose()
            {
                _waitHandle.Dispose();
            }
        }

        private sealed class QueuedAsynchronousAcquire : IQueuedAcquire
        {
            public int Count { get; }
            public Task WaiterTask => _taskCompletionSource.Task;
            private readonly TaskCompletionSource<bool> _taskCompletionSource = 
                new TaskCompletionSource<bool>(TaskContinuationOptions.RunContinuationsAsynchronously);

            public QueuedAsynchronousAcquire(int count)
            {
                Count = count;
            }

            public void GrantAcquire()
            {
                // The waiter is blocking on this TaskCompletionSource's Task
                var result = _taskCompletionSource.TrySetResult(true);
                Debug.Assert(result);
            }
        }

        private sealed class DisposeAction : IDisposable
        {
            private readonly Action _action;

            public DisposeAction(Action action)
            {
                _action = action;
            }

            public void Dispose()
            {
                _action();
            }
        }

        /// <summary>
        /// The amount of count available to acquire. Waits that exceed the CurrentCount block until enough count is released.
        /// </summary>
        public int CurrentCount => _currentCount;

        /// <summary>
        /// The largest available count allowed. CurrentCount cannot exceed this value.
        /// </summary>
        public int MaxCount => _maxCount;

        public AsyncSemaphore() : this(1, 1, false) { }

        public AsyncSemaphore(int startingCount) : this (startingCount, startingCount, false) { }

        public AsyncSemaphore(int startingCount, int maxCount) : this(startingCount, maxCount, false) { }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="startingCount">The amount of count immediately available to acquire.</param>
        /// <param name="maxCount">The maxmimum value that CurrentCount can reach.</param>
        /// <param name="fair">Whether pending Waits are treated with fairness. If true, order of Waits is respected for acquiring count. 
        /// Use this if starvation due to high contention is a concern.</param>
        public AsyncSemaphore(int startingCount, int maxCount, bool fair)
        {
            Debug.Assert(startingCount <= maxCount);

            _currentCount = startingCount;
            _maxCount = maxCount;
            _fair = fair;
        }

        public IDisposable WaitAndReleaseAll()
            => WaitAndReleaseAll(CancellationToken.None);

        public IDisposable WaitAndReleaseAll(CancellationToken cancellationToken)
            => WaitAndRelease(_maxCount, cancellationToken);

        public IDisposable WaitAndRelease()
            => WaitAndRelease(1);

        public IDisposable WaitAndRelease(CancellationToken cancellationToken)
            => WaitAndRelease(1, cancellationToken);

        public IDisposable WaitAndRelease(int count)
            => WaitAndRelease(count, CancellationToken.None);
        
        /// <summary>
        /// Blocks until the specified count is acquired, returning a IDisposable that releases the same count on dispose.
        /// </summary>
        /// <param name="count"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public IDisposable WaitAndRelease(int count, CancellationToken cancellationToken)
        {
            Wait(count, cancellationToken);
            return new DisposeAction(() => Release(count));
        }

        /// <summary>
        /// Succeeds when max count is acquired. Used to provide exclusive acquire access to AsyncSharp.
        /// </summary>
        public void WaitAll()
            => Wait(_maxCount);

        public void Wait()
            => Wait(1);

        public void Wait(CancellationToken cancellationToken)
            => Wait(1, cancellationToken);

        public void Wait(int count)
            => Wait(count, CancellationToken.None);

        public bool Wait(int count, int timeout)
            => Wait(count, timeout, CancellationToken.None);

        public void Wait(int count, CancellationToken cancellationToken)
            => Wait(count, int.MaxValue, cancellationToken);
        
        /// <summary>
        /// Synchronously blocks until either a successful acquire, a timeout, or a cancellation occurs.
        /// </summary>
        /// <param name="count"></param>
        /// <param name="timeout"></param>
        /// <param name="cancellationToken"></param>
        /// <returns>true if the Wait successfully acquired the count.</returns>
        public bool Wait(int count, int timeout, CancellationToken cancellationToken)
        {
            var startTime = (uint)Environment.TickCount;
            if (count > _maxCount)
            {
                throw new ArgumentOutOfRangeException($"Requested count '{count}' to acquire must be less than maximum configured count of '{_maxCount}'.");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            // TODO: Add support for CancellationToken where a callback sets the queued waiter as failed then releases it
            var acquiredSuccess = false;
            QueuedSynchronousAcquire queuedAcquire = null;
            try
            {
                lock (_lock)
                {
                    var currentCount = _currentCount;
                    if (currentCount >= count)
                    {
                        // Count available, immediately grant
                        _currentCount = currentCount - count;
                        return true;
                    }

                    // Count not available yet, add waiter to queue
                    queuedAcquire = new QueuedSynchronousAcquire(count);
                    _queuedAcquireRequests.Add(queuedAcquire);
                }

                var timeToWait = timeout - (int)((uint)Environment.TickCount - startTime);
                acquiredSuccess = queuedAcquire.Wait(timeToWait, cancellationToken);
                return acquiredSuccess;
            }
            finally
            {
                if (queuedAcquire != null)
                {
                    if (!acquiredSuccess)
                    {
                        lock (_lock)
                        {
                            RemoveFailedWaiter(queuedAcquire);
                        }
                    }
                    queuedAcquire.Dispose();
                }
            }
        }

        /// <summary>
        /// If a waiter failed to acquire count, it needs to be removed from the queue. 
        /// If it's already removed from the queue, it means that it already consumed count 
        /// which needs to be released again.
        /// </summary>
        /// <param name="queuedAcquire">The IQueudAcquire to remove from the queue.</param>
        private void RemoveFailedWaiter(IQueuedAcquire queuedAcquire)
        {
            if (!_queuedAcquireRequests.Remove(queuedAcquire))
            {
                Release(queuedAcquire.Count);
            }
        }
        
        public Task<IDisposable> WaitAndReleaseAllAsync()
            => WaitAndReleaseAllAsync(CancellationToken.None);

        public Task<IDisposable> WaitAndReleaseAllAsync(CancellationToken cancellationToken)
            => WaitAndReleaseAsync(_maxCount, cancellationToken);

        public Task<IDisposable> WaitAndReleaseAsync()
            => WaitAndReleaseAsync(1, CancellationToken.None);

        public Task<IDisposable> WaitAndReleaseAsync(CancellationToken cancellationToken)
            => WaitAndReleaseAsync(1, cancellationToken);

        public Task<IDisposable> WaitAndReleaseAsync(int count)
            => WaitAndReleaseAsync(count, CancellationToken.None);

        /// <summary>
        /// Blocks until the specified count is acquired, returning a IDisposable that releases the same count on dispose.
        /// </summary>
        /// <param name="count"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<IDisposable> WaitAndReleaseAsync(int count, CancellationToken cancellationToken)
        {
            await WaitAsync(count, cancellationToken).ConfigureAwait(false);
            return new DisposeAction(() => Release(count));
        }
            
        public Task WaitAsync()
            => WaitAsync(1);

        public Task WaitAsync(int count)
            => WaitAsync(count, CancellationToken.None);

        public Task<bool> WaitAsync(int count, int timeout)
            => WaitAsync(count, timeout, CancellationToken.None);

        public Task WaitAsync(CancellationToken cancellationToken)
            => WaitAsync(1, cancellationToken);

        public Task WaitAsync(int count, CancellationToken cancellationToken)
            => WaitAsync(count, int.MaxValue, cancellationToken);

        /// <summary>
        /// Asynchronously blocks until either a successful acquire, a timeout, or a cancellation occurs.
        /// </summary>
        /// <param name="count"></param>
        /// <param name="timeout"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<bool> WaitAsync(int count, int timeout, CancellationToken cancellationToken)
        {
            if (count > _maxCount)
            {
                throw new ArgumentOutOfRangeException($"Requested count '{count}' to acquire must be less than maximum configured count of '{_maxCount}'.");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            QueuedAsynchronousAcquire queuedAcquire;
            lock (_lock)
            {
                var currentCount = _currentCount;
                if (currentCount >= count)
                {
                    // Count available, immediately grant
                    _currentCount = currentCount - count;
                    return true;
                }

                // Count not available yet, add waiter to queue
                queuedAcquire = new QueuedAsynchronousAcquire(count);
                _queuedAcquireRequests.Add(queuedAcquire);
            }
            
            using (var cancellationTokenSource = cancellationToken.CanBeCanceled ?
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, default(CancellationToken)) :
                new CancellationTokenSource())
            {
                var queuedAcquireTask = queuedAcquire.WaiterTask;
                var waitCompleted = await Task.WhenAny(queuedAcquireTask, Task.Delay(timeout, cancellationTokenSource.Token)).ConfigureAwait(false);
                if (queuedAcquireTask == waitCompleted)
                {
                    // Ensure Task.Delay is cleaned up
                    cancellationTokenSource.Cancel();
                    return true;
                }
            }

            lock (_lock)
            {
                RemoveFailedWaiter(queuedAcquire);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }

        public void Release()
            => Release(1);
        
        public void Release(int count)
        {
            lock (_lock)
            {
                var currentCount = _currentCount + count;
                if (currentCount > _maxCount)
                {
                    throw new ArgumentOutOfRangeException(
                        $"Release of '{count}' would result in a {nameof(CurrentCount)} of '{currentCount}', " +
                        $"which exceeds the maximum count of '{_maxCount}'.");
                }

                var queuePosition = 0;
                while (queuePosition < _queuedAcquireRequests.Count)
                {
                    var queueItem = _queuedAcquireRequests[queuePosition];
                    if (currentCount >= queueItem.Count)
                    {
                        // Count available to acquire
                        queueItem.GrantAcquire();
                        currentCount -= queueItem.Count;
                        _queuedAcquireRequests.RemoveAt(queuePosition);
                    }
                    else if (!_fair)
                    {
                        ++queuePosition;
                    }
                    else
                    {
                        // Next queued item requires more count and fairness prevents us from checking any later waiters
                        break;
                    }
                }

                _currentCount = currentCount;
            }
        }

        public void ReleaseAll()
            => ReleaseAll(_maxCount);

        /// <summary>
        /// Succesfully completes all pending Waits and resets CurrentCount to the newCount provided.
        /// </summary>
        /// <param name="newCount">The value to reset CurrentCount to.</param>
        public void ReleaseAll(int newCount)
        {
            if (newCount < 0 || newCount > _maxCount)
            {
                throw new ArgumentOutOfRangeException($"The '{nameof(newCount)}' provided to '{nameof(ReleaseAll)}' " +
                    $"must be a non-negative number not exceed '{nameof(MaxCount)}'.");
            }

            lock (_lock)
            {
                for (var i = 0; i < _queuedAcquireRequests.Count; ++i)
                {
                    _queuedAcquireRequests[i].GrantAcquire();
                }
                _currentCount = newCount;
            }
        }
    }
}
