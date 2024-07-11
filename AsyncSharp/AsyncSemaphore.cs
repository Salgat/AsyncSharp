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
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
        public enum WaiterPriority 
        { 
            /// <summary>
            /// Lowest count Waiters are prioritized.
            /// </summary>
            LowToHigh,

            /// <summary>
            /// Highest count Waiters are prioritized.
            /// </summary>
            HighToLow,

            /// <summary>
            /// Waiters are prioritized by the order they were added, regardless of count.
            /// </summary>
            FirstInFirstOut,

            /// <summary>
            /// Waiters are prioritized by the order they were added, but waiters can be skipped 
            /// if a later waiter is able to be released by the available count.
            /// </summary>
            FirstInFirstOutUnfair
        }

        private int _currentCount; // Count available to acquire (Waits asking for more than available are blocked)
        private readonly int _maxCount; // Max count that can be acquired
        private readonly WaiterPriority _priority;
        private readonly object _lock = new object(); // Grants exclusive access to _currentCount and _queuedAcquireRequests

        // Keeps track of all acquire waiters. Wait/WaitAsync can only add entries, and Release/ReleaseAll and failed Wait/WaitAsync can only remove entries.
        internal readonly List<IQueuedAcquire> _queuedAcquireRequests = new List<IQueuedAcquire>();

        internal interface IQueuedAcquire
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
                if (timeout == Timeout.Infinite)
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
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

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
        /// Use this if starvation due to high contention is a concern. If this is false, ordering is still respected except in cases 
        /// where a release cannot free up the next waiter, but can free up a later waiter with a lower count request.</param>
        public AsyncSemaphore(int startingCount, int maxCount, bool fair)
        {
            if (startingCount > maxCount)
            {
                throw new ArgumentOutOfRangeException(nameof(startingCount), $"Starting count '{startingCount}' cannot exceed max count '{maxCount}'.");
            }
            if (startingCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(startingCount), $"Starting count '{startingCount}' must be a positive number.");
            }

            _currentCount = startingCount;
            _maxCount = maxCount;
            _priority = fair ? WaiterPriority.FirstInFirstOut : WaiterPriority.FirstInFirstOutUnfair;
        }

        public AsyncSemaphore(int startingCount, int maxCount, WaiterPriority waiterPriority)
        {
            if (startingCount > maxCount)
            {
                throw new ArgumentOutOfRangeException(nameof(startingCount), $"Starting count '{startingCount}' cannot exceed max count '{maxCount}'.");
            }
            if (startingCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(startingCount), $"Starting count '{startingCount}' must be a positive number.");
            }

            _currentCount = startingCount;
            _maxCount = maxCount;
            _priority = waiterPriority;
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
            => Wait(count, Timeout.Infinite, cancellationToken);
        
        /// <summary>
        /// Synchronously blocks until either a successful acquire, a timeout, or a cancellation occurs.
        /// </summary>
        /// <param name="count"></param>
        /// <param name="timeout"></param>
        /// <param name="cancellationToken"></param>
        /// <returns>true if the Wait successfully acquired the count.</returns>
        public bool Wait(int count, int timeout, CancellationToken cancellationToken)
        {
            if (count > _maxCount)
            {
                throw new ArgumentOutOfRangeException($"Requested count '{count}' to acquire must be less than maximum configured count of '{_maxCount}'.");
            }
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException($"Requested count '{count}' to acquire must be a non-negative number");
            }
            if (timeout < -1) // Timeout.Infinite == -1
            {
                throw new ArgumentOutOfRangeException($"Requested timeout '{timeout}' must be greater than or equal to -1.");
            }
            cancellationToken.ThrowIfCancellationRequested();

            var startTime = (uint)Environment.TickCount;
            var acquiredSuccess = false;
            QueuedSynchronousAcquire queuedAcquire = null;
            try
            {
                lock (_lock)
                {
                    if(_currentCount >= count)
                    {
                        // Count available, immediately grant
                        _currentCount -= count;
                        return true;
                    }

                    // Count not available yet, add waiter to queue
                    queuedAcquire = new QueuedSynchronousAcquire(count);
                    AddToRequests(queuedAcquire);
                }

                if (timeout == Timeout.Infinite)
                {
                    acquiredSuccess = queuedAcquire.Wait(Timeout.Infinite, cancellationToken);
                }
                else
                {
                    var timeToWait = Math.Max(0, timeout - (int)((uint)Environment.TickCount - startTime));
                    acquiredSuccess = queuedAcquire.Wait(timeToWait, cancellationToken);
                }
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
            => WaitAsync(count, Timeout.Infinite, cancellationToken);

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
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException($"Requested count '{count}' to acquire must be a non-negative number");
            }
            if (timeout < -1) // Timeout.Infinite == -1
            {
                throw new ArgumentOutOfRangeException($"Requested timeout '{timeout}' must be greater than or equal to -1.");
            }
            cancellationToken.ThrowIfCancellationRequested();

            QueuedAsynchronousAcquire queuedAcquire;
            lock (_lock)
            {
                if (_currentCount >= count)
                {
                    // Count available, immediately grant
                    _currentCount -= count;
                    return true;
                }

                // Count not available yet, add waiter to queue
                queuedAcquire = new QueuedAsynchronousAcquire(count);
                AddToRequests(queuedAcquire);
            }
            
            using (var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
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
            if (timeout == Timeout.Infinite) throw new TimeoutException("Timeout argument was infinite but failed to wait on semaphore.");
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
                while (currentCount != 0 && queuePosition < _queuedAcquireRequests.Count)
                {
                    var queueItem = _queuedAcquireRequests[queuePosition];
                    if (currentCount >= queueItem.Count)
                    {
                        // Count available to acquire
                        queueItem.GrantAcquire();
                        currentCount -= queueItem.Count;
                        _queuedAcquireRequests.Remove(queueItem);
                    }
                    else if (_priority == WaiterPriority.FirstInFirstOutUnfair)
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
                foreach (var queuedAcquireRequest in _queuedAcquireRequests)
                {
                    queuedAcquireRequest.GrantAcquire();
                }
                _queuedAcquireRequests.Clear();
                _currentCount = newCount;
            }
        }

        private void AddToRequests(IQueuedAcquire queuedAcquire)
        {
            int index;
            switch (_priority)
            {
                case WaiterPriority.LowToHigh:
                    if (queuedAcquire.Count == 1)
                    {
                        // 1 is the lowest valid count (a count of 0 skips the waiter and immediately grants acquire)
                        _queuedAcquireRequests.Add(queuedAcquire);
                    }
                    else
                    {
                        index = _queuedAcquireRequests.BinarySearch(queuedAcquire, _queuedAcquireLowToHighComparer);
                        _queuedAcquireRequests.Insert(index >= 0 ? index : ~index, queuedAcquire);
                    }
                    break;
                case WaiterPriority.HighToLow:
                    index = _queuedAcquireRequests.BinarySearch(queuedAcquire, _queuedAcquireHighToLowComparer);
                    _queuedAcquireRequests.Insert(index >= 0 ? index : ~index, queuedAcquire);
                    break;
                case WaiterPriority.FirstInFirstOut:
                case WaiterPriority.FirstInFirstOutUnfair:
                    _queuedAcquireRequests.Add(queuedAcquire);
                    break;
                default:
                    throw new NotImplementedException($"Priority value {_priority} not recognized.");
            }
        }

        private static readonly QueuedAcquireLowToHighComparer _queuedAcquireLowToHighComparer = new QueuedAcquireLowToHighComparer();
        private class QueuedAcquireLowToHighComparer : IComparer<IQueuedAcquire>
        {
            public int Compare(IQueuedAcquire x, IQueuedAcquire y)
            {
                return x.Count.CompareTo(y.Count);
            }
        }

        private static readonly QueuedAcquireHighToLowComparer _queuedAcquireHighToLowComparer = new QueuedAcquireHighToLowComparer();
        private class QueuedAcquireHighToLowComparer : IComparer<IQueuedAcquire>
        {
            public int Compare(IQueuedAcquire x, IQueuedAcquire y)
            {
                return y.Count.CompareTo(x.Count);
            }
        }
    }
}
