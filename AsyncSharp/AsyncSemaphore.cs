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
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace AsyncSharp
{
    /// <summary>
    /// Provides an async friendly Semaphore with the intention of providing more features than SemaphoreSlim, including
    /// waiting on multiple count, atomically release all waiters, disposable wait and release, and fairness of the order
    /// that waiters are released based on the order of their wait.
    /// </summary>
    public class AsyncSemaphore : IDisposable
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
            FirstInFirstOutUnfair,

            /// <summary>
            /// Ordering of waiters is not respected, and priority may sometimes not be respected
            /// when performance is improved. This option is dangerous, since it tends to prioritize
            /// newer waiters, allowing for easier starvation of waiters.
            /// </summary>
            Unfair
        }

        public static TimeSpan InfiniteTimeSpan => Timeout.InfiniteTimeSpan;
        public static int DefaultPriority => 0;

        private static readonly TimeSpan MaxSupportedTimeout =
            TimeSpan.FromMilliseconds(int.MaxValue);
        private static readonly TimeSpan StateLockRetryDelay =
            TimeSpan.FromMilliseconds(10);
        private static readonly Task<bool> AcquiredTask = Task.FromResult(true);

        private volatile int _currentCount; // Count available to acquire (Waits asking for more than available are blocked)
        private readonly int _maxCount; // Max count that can be acquired
        private readonly WaiterPriority _priority;
        private readonly object _lock = new object(); // Grants exclusive access to _currentCount and _queuedAcquireRequests
        private volatile bool _disposed; // Set once during Dispose (write guarded by _lock, read lock-free by CheckIfDisposed)
        private object _releaseEpoch = new object(); // Replaced by ReleaseAll so leases from an earlier reset cannot release stale count.

        // Keeps track of all acquire waiters. Wait/WaitAsync can only add entries, and Release/ReleaseAll and failed Wait/WaitAsync can only remove entries.
        internal readonly Dictionary<int, WaiterBucket> _queuedAcquireRequests = new Dictionary<int, WaiterBucket>();
        internal readonly SortedSet<int> _activePriorities = new SortedSet<int>(_priorityHighToLowComparer);
        private readonly List<int> _pendingPriorityRemovals = new List<int>(); // This is not local to the function to avoid extra allocations
        private readonly WaiterBucket _defaultPriorityBucket = new WaiterBucket(DefaultPriority);
        private int _queuedWaiterCount;

        internal int QueuedWaiterCount
        {
            get
            {
                lock (_lock)
                {
                    return _queuedWaiterCount;
                }
            }
        }

        internal enum QueuedAcquireOutcome
        {
            Pending,
            Granted,
            ResetGranted,
            Failed,
            Canceled
        }

        internal readonly struct AcquireResult
        {
            public static AcquireResult NotAcquired => default;

            public bool Acquired { get; }
            public bool ReleaseRequired { get; }
            public object ReleaseEpoch { get; }
            public Exception Failure { get; }

            private AcquireResult(bool acquired, bool releaseRequired, object releaseEpoch, Exception failure)
            {
                Acquired = acquired;
                ReleaseRequired = releaseRequired;
                ReleaseEpoch = releaseEpoch;
                Failure = failure;
            }

            public static AcquireResult Granted(object releaseEpoch, bool releaseRequired)
                => new AcquireResult(true, releaseRequired, releaseEpoch, null);

            public static AcquireResult Failed(Exception failure)
                => new AcquireResult(false, false, null, failure);
        }

        internal sealed class WaiterBucket
        {
            public int Priority { get; }
            public QueuedAcquire Head { get; private set; }
            public QueuedAcquire Tail { get; private set; }
            public int Count { get; private set; }

            public WaiterBucket(int priority)
            {
                Priority = priority;
            }

            public void AddLast(QueuedAcquire queuedAcquire)
            {
                Debug.Assert(queuedAcquire.Bucket == null);
                queuedAcquire.Bucket = this;
                queuedAcquire.Previous = Tail;
                queuedAcquire.Next = null;
                if (Tail == null)
                {
                    Head = queuedAcquire;
                }
                else
                {
                    Tail.Next = queuedAcquire;
                }

                Tail = queuedAcquire;
                ++Count;
            }

            public void InsertAfter(QueuedAcquire existing, QueuedAcquire queuedAcquire)
            {
                Debug.Assert(existing != null && existing.Bucket == this);
                Debug.Assert(queuedAcquire.Bucket == null);
                var next = existing.Next;
                queuedAcquire.Bucket = this;
                queuedAcquire.Previous = existing;
                queuedAcquire.Next = next;
                existing.Next = queuedAcquire;
                if (next == null)
                {
                    Tail = queuedAcquire;
                }
                else
                {
                    next.Previous = queuedAcquire;
                }

                ++Count;
            }

            public void AddFirst(QueuedAcquire queuedAcquire)
            {
                Debug.Assert(queuedAcquire.Bucket == null);
                queuedAcquire.Bucket = this;
                queuedAcquire.Previous = null;
                queuedAcquire.Next = Head;
                if (Head == null)
                {
                    Tail = queuedAcquire;
                }
                else
                {
                    Head.Previous = queuedAcquire;
                }

                Head = queuedAcquire;
                ++Count;
            }

            public void Remove(QueuedAcquire queuedAcquire)
            {
                Debug.Assert(queuedAcquire.Bucket == this);
                var previous = queuedAcquire.Previous;
                var next = queuedAcquire.Next;
                if (previous == null)
                {
                    Head = next;
                }
                else
                {
                    previous.Next = next;
                }

                if (next == null)
                {
                    Tail = previous;
                }
                else
                {
                    next.Previous = previous;
                }

                queuedAcquire.Previous = null;
                queuedAcquire.Next = null;
                queuedAcquire.Bucket = null;
                --Count;
            }
        }

        internal abstract class QueuedAcquire
        {
            public int Count { get; }
            public QueuedAcquireOutcome Outcome { get; private set; } = QueuedAcquireOutcome.Pending;
            public AcquireResult Result { get; private set; }
            public WaiterBucket Bucket { get; set; }
            public QueuedAcquire Previous { get; set; }
            public QueuedAcquire Next { get; set; }

            protected QueuedAcquire(int count)
            {
                Count = count;
            }

            public void GrantAcquire(object releaseEpoch, bool releaseRequired)
            {
                Debug.Assert(Outcome == QueuedAcquireOutcome.Pending);
                Outcome = releaseRequired ? QueuedAcquireOutcome.Granted : QueuedAcquireOutcome.ResetGranted;
                Result = AcquireResult.Granted(releaseEpoch, releaseRequired);
                Complete(Result);
            }

            public void FailAcquire(Exception exception)
            {
                Debug.Assert(Outcome == QueuedAcquireOutcome.Pending);
                Outcome = QueuedAcquireOutcome.Failed;
                Result = AcquireResult.Failed(exception);
                Complete(Result);
            }

            public void CancelAcquire()
            {
                Debug.Assert(Outcome == QueuedAcquireOutcome.Pending
                    || Outcome == QueuedAcquireOutcome.Granted
                    || Outcome == QueuedAcquireOutcome.ResetGranted);

                var wasPending = Outcome == QueuedAcquireOutcome.Pending;
                Outcome = QueuedAcquireOutcome.Canceled;
                if (wasPending)
                {
                    Result = AcquireResult.NotAcquired;
                    Complete(Result);
                }
            }

            protected abstract void Complete(AcquireResult result);
        }

        private sealed class QueuedSynchronousAcquire : QueuedAcquire, IDisposable
        {
            private readonly ManualResetEventSlim _waitHandle = new ManualResetEventSlim(false);

            public QueuedSynchronousAcquire(int count) : base(count) { }

            public AcquireResult Wait(TimeSpan timeout, CancellationToken cancellationToken)
            {
                bool completed;
                if (timeout == Timeout.InfiniteTimeSpan)
                {
                    _waitHandle.Wait(cancellationToken);
                    completed = true;
                }
                else if (timeout > TimeSpan.Zero)
                {
                    completed = _waitHandle.Wait(timeout, cancellationToken);
                }
                else
                {
                    completed = _waitHandle.Wait(0, cancellationToken);
                }

                return completed ? Result : AcquireResult.NotAcquired;
            }

            protected override void Complete(AcquireResult result)
            {
                _waitHandle.Set();
            }

            public void Dispose()
            {
                _waitHandle.Dispose();
            }
        }

        private sealed class QueuedAsynchronousAcquire : QueuedAcquire
        {
            public Task<AcquireResult> WaiterTask => _taskCompletionSource.Task;
            private readonly TaskCompletionSource<AcquireResult> _taskCompletionSource =
                new TaskCompletionSource<AcquireResult>(TaskCreationOptions.RunContinuationsAsynchronously);

            public QueuedAsynchronousAcquire(int count) : base(count) { }

            protected override void Complete(AcquireResult result)
            {
                var completed = result.Failure == null
                    ? _taskCompletionSource.TrySetResult(result)
                    : _taskCompletionSource.TrySetException(result.Failure);
                Debug.Assert(completed);
            }
        }

        private sealed class SemaphoreLease : IDisposable
        {
            private AsyncSemaphore _owner;
            private readonly int _count;
            private readonly object _releaseEpoch;
            private readonly bool _releaseRequired;

            public SemaphoreLease(AsyncSemaphore owner, int count, AcquireResult acquireResult)
            {
                _owner = owner;
                _count = count;
                _releaseEpoch = acquireResult.ReleaseEpoch;
                _releaseRequired = acquireResult.ReleaseRequired;
            }

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                if (owner != null)
                {
                    owner.ReleaseLease(_count, _releaseEpoch, _releaseRequired);
                }
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
        /// Creates a semaphore with the provided starting and maximum count, choosing between fair (FirstInFirstOut)
        /// and unfair (FirstInFirstOutUnfair) ordering of waiters.
        /// </summary>
        /// <param name="startingCount">The amount of count immediately available to acquire.</param>
        /// <param name="maxCount">The maximum value that CurrentCount can reach.</param>
        /// <param name="fair">Whether pending Waits are treated with fairness. If true, order of Waits is respected for acquiring count. 
        /// Use this if starvation due to high contention is a concern. If this is false, ordering is still respected except in cases 
        /// where a release cannot free up the next waiter, but can free up a later waiter with a lower count request.</param>
        public AsyncSemaphore(int startingCount, int maxCount, bool fair)
            : this(startingCount, maxCount, fair ? WaiterPriority.FirstInFirstOut : WaiterPriority.FirstInFirstOutUnfair)
        {
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
            if (!Enum.IsDefined(typeof(WaiterPriority), waiterPriority))
            {
                throw new ArgumentOutOfRangeException(nameof(waiterPriority), waiterPriority, "The waiter priority value is not recognized.");
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
            var acquireResult = WaitCore(count, Timeout.InfiniteTimeSpan, DefaultPriority, cancellationToken);
            return CreateLease(count, acquireResult);
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

        public bool Wait(int count, TimeSpan timeout)
            => Wait(count, timeout, CancellationToken.None);

        public void Wait(int count, CancellationToken cancellationToken)
            => Wait(count, TimeSpan.FromMilliseconds(-1), cancellationToken);

        public bool Wait(int count, TimeSpan timeout, CancellationToken cancellationToken)
            => Wait(count, timeout, DefaultPriority, cancellationToken);

        /// <summary>
        /// Synchronously blocks until either a successful acquire, a timeout, or a cancellation occurs.
        /// </summary>
        /// <param name="count"></param>
        /// <param name="timeout"></param>
        /// <param name="cancellationToken"></param>
        /// <param name="priority">The priority of this waiter as compared to other pending acquires. The higher the priority, the earlier it will be handled.</param>
        /// <returns>true if the Wait successfully acquired the count.</returns>
        public bool Wait(int count, TimeSpan timeout, int priority, CancellationToken cancellationToken)
        {
            return WaitCore(count, timeout, priority, cancellationToken).Acquired;
        }

        private AcquireResult WaitCore(int count, TimeSpan timeout, int priority, CancellationToken cancellationToken)
        {
            ValidateWaitArguments(count, timeout, cancellationToken);

            if (timeout == Timeout.InfiniteTimeSpan && !cancellationToken.CanBeCanceled)
            {
                return WaitCoreInfinite(count, priority);
            }

            var timeoutStopwatch = timeout == Timeout.InfiniteTimeSpan
                ? null
                : Stopwatch.StartNew();
            var acquireResult = AcquireResult.NotAcquired;
            QueuedSynchronousAcquire queuedAcquire = null;
            try
            {
                var lockTaken = false;
                try
                {
                    if (!TryEnterStateLock(
                        timeout,
                        timeoutStopwatch,
                        cancellationToken,
                        ref lockTaken))
                    {
                        return AcquireResult.NotAcquired;
                    }

                    if (CanAcquireImmediatelyLocked(count))
                    {
                        _currentCount -= count;
                        return AcquireResult.Granted(_releaseEpoch, true);
                    }

                    queuedAcquire = new QueuedSynchronousAcquire(count);
                    AddToRequests(queuedAcquire, priority);
                    DrainWaitersLocked();
                }
                finally
                {
                    if (lockTaken)
                    {
                        Monitor.Exit(_lock);
                    }
                }

                acquireResult = queuedAcquire.Wait(
                    GetRemainingTimeout(timeout, timeoutStopwatch),
                    cancellationToken);
                ThrowIfAcquireFailed(acquireResult);
                return acquireResult;
            }
            finally
            {
                if (queuedAcquire != null)
                {
                    if (!acquireResult.Acquired)
                    {
                        lock (_lock)
                        {
                            CancelWaiterLocked(queuedAcquire, priority);
                        }
                    }
                    queuedAcquire.Dispose();
                }
            }
        }

        private AcquireResult WaitCoreInfinite(int count, int priority)
        {
            var acquireResult = AcquireResult.NotAcquired;
            QueuedSynchronousAcquire queuedAcquire = null;
            try
            {
                lock (_lock)
                {
                    CheckIfDisposed();
                    if (CanAcquireImmediatelyLocked(count))
                    {
                        _currentCount -= count;
                        return AcquireResult.Granted(_releaseEpoch, true);
                    }

                    queuedAcquire = new QueuedSynchronousAcquire(count);
                    AddToRequests(queuedAcquire, priority);
                    DrainWaitersLocked();
                }

                acquireResult = queuedAcquire.Wait(Timeout.InfiniteTimeSpan, CancellationToken.None);
                ThrowIfAcquireFailed(acquireResult);
                return acquireResult;
            }
            finally
            {
                if (queuedAcquire != null)
                {
                    if (!acquireResult.Acquired)
                    {
                        lock (_lock)
                        {
                            CancelWaiterLocked(queuedAcquire, priority);
                        }
                    }

                    queuedAcquire.Dispose();
                }
            }
        }

        private bool CanAcquireImmediatelyLocked(int count)
            => _currentCount >= count
                && (_priority == WaiterPriority.Unfair || _queuedWaiterCount == 0);

        private void ValidateWaitArguments(int count, TimeSpan timeout, CancellationToken cancellationToken)
        {
            CheckIfDisposed();
            if (count > _maxCount)
            {
                throw new ArgumentOutOfRangeException(nameof(count), $"Requested count '{count}' to acquire must be less than or equal to the maximum configured count of '{_maxCount}'.");
            }
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count), $"Requested count '{count}' to acquire must be a non-negative number.");
            }
            // A count of zero still participates in queue ordering and is granted without consuming count.
            if (timeout != Timeout.InfiniteTimeSpan
                && (timeout < TimeSpan.Zero || timeout > MaxSupportedTimeout))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timeout),
                    $"Requested timeout '{timeout}' must be between TimeSpan.Zero and '{MaxSupportedTimeout}', " +
                    "or equal to Timeout.InfiniteTimeSpan.");
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        private static TimeSpan GetRemainingTimeout(TimeSpan timeout, Stopwatch timeoutStopwatch)
        {
            if (timeout == Timeout.InfiniteTimeSpan)
            {
                return Timeout.InfiniteTimeSpan;
            }

            var elapsed = timeoutStopwatch.Elapsed;
            return elapsed >= timeout ? TimeSpan.Zero : timeout - elapsed;
        }

        private bool TryEnterStateLock(
            TimeSpan timeout,
            Stopwatch timeoutStopwatch,
            CancellationToken cancellationToken,
            ref bool lockTaken)
        {
            while (!lockTaken)
            {
                CheckIfDisposed();
                cancellationToken.ThrowIfCancellationRequested();

                var remainingTimeout = GetRemainingTimeout(timeout, timeoutStopwatch);
                if (remainingTimeout == TimeSpan.Zero)
                {
                    Monitor.TryEnter(_lock, 0, ref lockTaken);
                    if (!lockTaken)
                    {
                        CheckIfDisposed();
                        cancellationToken.ThrowIfCancellationRequested();
                        return false;
                    }
                    break;
                }

                var retryDelay = timeout == Timeout.InfiniteTimeSpan
                    || remainingTimeout > StateLockRetryDelay
                        ? StateLockRetryDelay
                        : remainingTimeout;
                Monitor.TryEnter(_lock, retryDelay, ref lockTaken);
            }

            CheckIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            return timeout <= TimeSpan.Zero || timeoutStopwatch.Elapsed < timeout;
        }

        private void EnterStateLockAfterContention(ref bool lockTaken)
        {
            Debug.Assert(!lockTaken);

            var spinWait = new SpinWait();
            while (true)
            {
                Monitor.TryEnter(_lock, ref lockTaken);
                if (lockTaken)
                {
                    return;
                }

                // Use only SpinWait's busy-spin phase. Monitor.Enter provides the
                // signal-driven parking fallback once spinning would yield.
                if (spinWait.NextSpinWillYield)
                {
                    break;
                }

                spinWait.SpinOnce();
            }

            Monitor.Enter(_lock, ref lockTaken);
        }

        private static void ThrowIfAcquireFailed(AcquireResult acquireResult)
        {
            if (acquireResult.Failure != null)
            {
                ExceptionDispatchInfo.Capture(acquireResult.Failure).Throw();
            }
        }

        private IDisposable CreateLease(int count, AcquireResult acquireResult)
        {
            Debug.Assert(acquireResult.Acquired);
            return new SemaphoreLease(this, count, acquireResult);
        }

        private void ReleaseLease(int count, object releaseEpoch, bool releaseRequired)
        {
            if (!releaseRequired)
            {
                return;
            }

            lock (_lock)
            {
                if (!ReferenceEquals(releaseEpoch, _releaseEpoch))
                {
                    return;
                }

                CheckIfDisposed();
                ReleaseUpToLocked(count, true);
            }
        }

        /// <summary>
        /// Acquires up to the provided count. This operation is done as soon as possible, and has the highest priority. Since 
        /// it has no waiters, there is no timeout or cancellation token required.
        /// </summary>
        /// <param name="count"></param>
        /// <returns>Returns the count it was able to acquire. This count still needs to be released as some point in the future.</returns>
        public int AcquireUpTo(int count)
        {
            CheckIfDisposed();
            if (count > _maxCount)
            {
                throw new ArgumentOutOfRangeException(nameof(count), $"Requested count '{count}' to acquire must be less than or equal to the maximum configured count of '{_maxCount}'.");
            }
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count), $"Requested count '{count}' to acquire must be a non-negative number.");
            }
            lock (_lock)
            {
                CheckIfDisposed();
                var countAcquired = Math.Min(_currentCount, count);
                _currentCount -= countAcquired;
                return countAcquired;
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
            var acquireResult = cancellationToken.CanBeCanceled
                ? await WaitAsyncCoreWithTimeoutOrCancellation(
                    count,
                    Timeout.InfiniteTimeSpan,
                    DefaultPriority,
                    cancellationToken).ConfigureAwait(false)
                : await WaitAsyncResultNoTimeout(count, DefaultPriority).ConfigureAwait(false);
            return CreateLease(count, acquireResult);
        }

        public Task WaitAsync()
            => WaitAsyncNoTimeout(1, DefaultPriority);

        public Task WaitAsync(int count)
            => WaitAsyncNoTimeout(count, DefaultPriority);

        public Task<bool> WaitAsync(int count, TimeSpan timeout)
            => WaitAsync(count, timeout, CancellationToken.None);

        public Task WaitAsync(CancellationToken cancellationToken)
            => WaitAsync(1, cancellationToken);

        public Task WaitAsync(int count, CancellationToken cancellationToken)
            => cancellationToken.CanBeCanceled
                ? WaitAsync(count, Timeout.InfiniteTimeSpan, cancellationToken)
                : WaitAsyncNoTimeout(count, DefaultPriority);

        public Task<bool> WaitAsync(int count, TimeSpan timeout, CancellationToken cancellationToken)
            => WaitAsync(count, timeout, DefaultPriority, cancellationToken);

        /// <summary>
        /// Asynchronously blocks until either a successful acquire, a timeout, or a cancellation occurs.
        /// </summary>
        /// <param name="count"></param>
        /// <param name="timeout"></param>
        /// <param name="cancellationToken"></param>
        /// <param name="priority">The priority of this waiter as compared to other pending acquires. The higher the priority, the earlier it will be handled.</param>
        /// <returns></returns>
        public Task<bool> WaitAsync(int count, TimeSpan timeout, int priority, CancellationToken cancellationToken)
        {
            return timeout == Timeout.InfiniteTimeSpan && !cancellationToken.CanBeCanceled
                ? WaitAsyncBooleanNoTimeout(count, priority)
                : WaitAsyncBooleanWithTimeoutOrCancellation(count, timeout, priority, cancellationToken);
        }

        private Task WaitAsyncNoTimeout(int count, int priority)
        {
            try
            {
                ValidateWaitArguments(count, Timeout.InfiniteTimeSpan, CancellationToken.None);
                if (!TryStartAsyncAcquireNoTimeout(count, priority, out var acquireResult, out var waiterTask))
                {
                    return AwaitAcquireWithoutResultAsync(
                        WaitAsyncResultNoTimeoutAfterLockContention(count, priority));
                }

                return acquireResult.Acquired ? Task.CompletedTask : waiterTask;
            }
            catch (Exception exception)
            {
                return Task.FromException(exception);
            }
        }

        private Task<bool> WaitAsyncBooleanNoTimeout(int count, int priority)
        {
            try
            {
                ValidateWaitArguments(count, Timeout.InfiniteTimeSpan, CancellationToken.None);
                if (!TryStartAsyncAcquireNoTimeout(count, priority, out var acquireResult, out var waiterTask))
                {
                    return AwaitAcquireBooleanAsync(
                        WaitAsyncResultNoTimeoutAfterLockContention(count, priority));
                }

                return acquireResult.Acquired
                    ? AcquiredTask
                    : AwaitAcquireBooleanAsync(waiterTask);
            }
            catch (Exception exception)
            {
                return Task.FromException<bool>(exception);
            }
        }

        private Task<AcquireResult> WaitAsyncResultNoTimeout(int count, int priority)
        {
            try
            {
                ValidateWaitArguments(count, Timeout.InfiniteTimeSpan, CancellationToken.None);
                if (!TryStartAsyncAcquireNoTimeout(count, priority, out var acquireResult, out var waiterTask))
                {
                    return WaitAsyncResultNoTimeoutAfterLockContention(count, priority);
                }

                return acquireResult.Acquired
                    ? Task.FromResult(acquireResult)
                    : waiterTask;
            }
            catch (Exception exception)
            {
                return Task.FromException<AcquireResult>(exception);
            }
        }

        private bool TryStartAsyncAcquireNoTimeout(
            int count,
            int priority,
            out AcquireResult acquireResult,
            out Task<AcquireResult> waiterTask)
        {
            acquireResult = AcquireResult.NotAcquired;
            waiterTask = null;
            var lockTaken = false;
            try
            {
                Monitor.TryEnter(_lock, ref lockTaken);
                if (!lockTaken)
                {
                    return false;
                }

                CheckIfDisposed();
                if (CanAcquireImmediatelyLocked(count))
                {
                    _currentCount -= count;
                    acquireResult = AcquireResult.Granted(_releaseEpoch, true);
                    return true;
                }

                var queuedAcquire = new QueuedAsynchronousAcquire(count);
                AddToRequests(queuedAcquire, priority);
                DrainWaitersLocked();
                waiterTask = queuedAcquire.WaiterTask;
                return true;
            }
            finally
            {
                if (lockTaken)
                {
                    Monitor.Exit(_lock);
                }
            }
        }

        private async Task<AcquireResult> WaitAsyncResultNoTimeoutAfterLockContention(int count, int priority)
        {
            QueuedAsynchronousAcquire queuedAcquire;
            var lockTaken = false;
            try
            {
                EnterStateLockAfterContention(ref lockTaken);
                CheckIfDisposed();
                if (CanAcquireImmediatelyLocked(count))
                {
                    _currentCount -= count;
                    return AcquireResult.Granted(_releaseEpoch, true);
                }

                queuedAcquire = new QueuedAsynchronousAcquire(count);
                AddToRequests(queuedAcquire, priority);
                DrainWaitersLocked();
            }
            finally
            {
                if (lockTaken)
                {
                    Monitor.Exit(_lock);
                }
            }

            return await queuedAcquire.WaiterTask.ConfigureAwait(false);
        }

        private static async Task AwaitAcquireWithoutResultAsync(Task<AcquireResult> acquireTask)
        {
            await acquireTask.ConfigureAwait(false);
        }

        private static async Task<bool> AwaitAcquireBooleanAsync(Task<AcquireResult> acquireTask)
        {
            return (await acquireTask.ConfigureAwait(false)).Acquired;
        }

        private async Task<bool> WaitAsyncBooleanWithTimeoutOrCancellation(
            int count,
            TimeSpan timeout,
            int priority,
            CancellationToken cancellationToken)
        {
            return (await WaitAsyncCoreWithTimeoutOrCancellation(
                count,
                timeout,
                priority,
                cancellationToken).ConfigureAwait(false)).Acquired;
        }

        private async Task<AcquireResult> WaitAsyncCoreWithTimeoutOrCancellation(
            int count,
            TimeSpan timeout,
            int priority,
            CancellationToken cancellationToken)
        {
            ValidateWaitArguments(count, timeout, cancellationToken);

            var timeoutStopwatch = timeout == Timeout.InfiniteTimeSpan
                ? null
                : Stopwatch.StartNew();
            var acquireResult = AcquireResult.NotAcquired;
            QueuedAsynchronousAcquire queuedAcquire = null;
            CancellationTokenSource timeoutCancellation = null;
            Task timeoutTask = null;

            try
            {
                var lockTaken = false;
                try
                {
                    while (!lockTaken)
                    {
                        CheckIfDisposed();
                        cancellationToken.ThrowIfCancellationRequested();
                        Monitor.TryEnter(_lock, ref lockTaken);
                        if (lockTaken)
                        {
                            break;
                        }

                        var remainingTimeout = GetRemainingTimeout(timeout, timeoutStopwatch);
                        if (remainingTimeout == TimeSpan.Zero)
                        {
                            return AcquireResult.NotAcquired;
                        }

                        var retryDelay = timeout == Timeout.InfiniteTimeSpan
                            || remainingTimeout > StateLockRetryDelay
                                ? StateLockRetryDelay
                                : remainingTimeout;
                        await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                    }

                    CheckIfDisposed();
                    cancellationToken.ThrowIfCancellationRequested();
                    if (timeout > TimeSpan.Zero && timeoutStopwatch.Elapsed >= timeout)
                    {
                        return AcquireResult.NotAcquired;
                    }

                    if (CanAcquireImmediatelyLocked(count))
                    {
                        _currentCount -= count;
                        acquireResult = AcquireResult.Granted(_releaseEpoch, true);
                        return acquireResult;
                    }

                    // Argument validation guarantees Task.Delay accepts the remaining timeout.
                    // Delay/cancellation state is created only once the operation must queue.
                    timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutTask = Task.Delay(
                        GetRemainingTimeout(timeout, timeoutStopwatch),
                        timeoutCancellation.Token);
                    queuedAcquire = new QueuedAsynchronousAcquire(count);
                    AddToRequests(queuedAcquire, priority);
                    DrainWaitersLocked();
                }
                finally
                {
                    if (lockTaken)
                    {
                        Monitor.Exit(_lock);
                    }
                }

                var queuedAcquireTask = queuedAcquire.WaiterTask;
                if (queuedAcquireTask.IsCompleted)
                {
                    acquireResult = await queuedAcquireTask.ConfigureAwait(false);
                }
                else
                {
                    var completedTask = await Task.WhenAny(queuedAcquireTask, timeoutTask).ConfigureAwait(false);
                    if (completedTask == queuedAcquireTask)
                    {
                        acquireResult = await queuedAcquireTask.ConfigureAwait(false);
                    }
                    else
                    {
                        lock (_lock)
                        {
                            CancelWaiterLocked(queuedAcquire, priority);
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                        if (timeout == Timeout.InfiniteTimeSpan)
                        {
                            throw new TimeoutException("Timeout argument was infinite but failed to wait on semaphore.");
                        }
                        return AcquireResult.NotAcquired;
                    }
                }

                ThrowIfAcquireFailed(acquireResult);
                return acquireResult;
            }
            finally
            {
                if (timeoutCancellation != null)
                {
                    timeoutCancellation.Cancel();
                    timeoutCancellation.Dispose();
                }

                if (queuedAcquire != null && !acquireResult.Acquired)
                {
                    lock (_lock)
                    {
                        CancelWaiterLocked(queuedAcquire, priority);
                    }
                }
            }
        }

        public void Release()
            => Release(1);
        
        public void Release(int count)
        {
            var amountReleased = ReleaseUpTo(count, true);
            if (amountReleased != count)
            {
                throw new InvalidOperationException($"A count of '{count}' was to be released, but only '{amountReleased}' was released.");
            }
        }


        public int ReleaseUpTo(int count) => ReleaseUpTo(count, false);

        /// <summary>
        /// Will attempt to release up to the count provided.
        /// </summary>
        /// <param name="count"></param>
        /// <param name="assertCount">If true, will ensure that the caller is intended to keep track of the exact amount acquired and released.</param>
        /// <returns>The count released.</returns>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        private int ReleaseUpTo(int count, bool assertCount)
        {
            CheckIfDisposed();
            lock (_lock)
            {
                CheckIfDisposed();
                return ReleaseUpToLocked(count, assertCount);
            }
        }

        private int ReleaseUpToLocked(int count, bool assertCount)
        {
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count), $"Requested count '{count}' to release must be a non-negative number.");
            }

            // Subtracting from MaxCount is safe under the core count invariant and avoids
            // overflowing when count is close to Int32.MaxValue.
            var availableCapacity = _maxCount - _currentCount;
            if (assertCount && count > availableCapacity)
            {
                throw new ArgumentOutOfRangeException(nameof(count),
                    $"Release of '{count}' would exceed the maximum count of '{_maxCount}' " +
                    $"from the current count of '{_currentCount}'.");
            }

            var acceptedCount = Math.Min(count, availableCapacity);
            _currentCount += acceptedCount;
            DrainWaitersLocked();
            return acceptedCount;
        }

        private void DrainWaitersLocked()
        {
            if (_queuedWaiterCount == 0)
            {
                return;
            }

            try
            {
                foreach (var priority in _activePriorities)
                {
                    var bucket = _queuedAcquireRequests[priority];
                    var queueItem = bucket.Head;
                    while (queueItem != null)
                    {
                        var next = queueItem.Next;
                        if (_currentCount >= queueItem.Count)
                        {
                            _currentCount -= queueItem.Count;
                            bucket.Remove(queueItem);
                            --_queuedWaiterCount;
                            queueItem.GrantAcquire(_releaseEpoch, true);

                            if (bucket.Count == 0)
                            {
                                _pendingPriorityRemovals.Add(priority);
                                _queuedAcquireRequests.Remove(priority);
                                break;
                            }

                            queueItem = next;
                        }
                        else if (_priority == WaiterPriority.FirstInFirstOutUnfair
                            || _priority == WaiterPriority.Unfair)
                        {
                            // Continue scanning even when no count is available: a later zero-count
                            // waiter is always satisfiable.
                            queueItem = next;
                        }
                        else
                        {
                            break;
                        }
                    }

                    // Numeric priority is strict: lower-priority buckets cannot overtake a
                    // blocked higher-priority bucket. The Unfair mode may still bypass the
                    // queue through its documented immediate-acquire fast path.
                    if (_queuedAcquireRequests.ContainsKey(priority))
                    {
                        break;
                    }
                }
            }
            finally
            {
                foreach (var priority in _pendingPriorityRemovals)
                {
                    _activePriorities.Remove(priority);
                }
                _pendingPriorityRemovals.Clear();
            }
        }

        private void CancelWaiterLocked(QueuedAcquire queuedAcquire, int priority)
        {
            switch (queuedAcquire.Outcome)
            {
                case QueuedAcquireOutcome.Pending:
                    var bucket = queuedAcquire.Bucket;
                    var removed = bucket != null && bucket.Priority == priority;
                    Debug.Assert(removed);
                    if (removed)
                    {
                        bucket.Remove(queuedAcquire);
                        --_queuedWaiterCount;
                        if (bucket.Count == 0)
                        {
                            _queuedAcquireRequests.Remove(priority);
                            _activePriorities.Remove(priority);
                        }
                    }

                    queuedAcquire.CancelAcquire();
                    DrainWaitersLocked();
                    break;

                case QueuedAcquireOutcome.Granted:
                    var acquireResult = queuedAcquire.Result;
                    queuedAcquire.CancelAcquire();
                    ReturnGrantedCountLocked(queuedAcquire.Count, acquireResult);
                    break;

                case QueuedAcquireOutcome.ResetGranted:
                    queuedAcquire.CancelAcquire();
                    break;

                case QueuedAcquireOutcome.Failed:
                case QueuedAcquireOutcome.Canceled:
                    break;

                default:
                    throw new InvalidOperationException($"Queued acquire outcome '{queuedAcquire.Outcome}' is not recognized.");
            }
        }

        private void ReturnGrantedCountLocked(int count, AcquireResult acquireResult)
        {
            if (!acquireResult.ReleaseRequired
                || !ReferenceEquals(acquireResult.ReleaseEpoch, _releaseEpoch)
                || _disposed)
            {
                return;
            }

            var availableCapacity = _maxCount - _currentCount;
            Debug.Assert(count <= availableCapacity);
            _currentCount += Math.Min(count, availableCapacity);
            DrainWaitersLocked();
        }

        public void ReleaseAll()
            => ReleaseAll(_maxCount);

        /// <summary>
        /// Successfully completes all pending Waits and resets CurrentCount to the newCount provided.
        /// </summary>
        /// <remarks>
        /// Every pending waiter is granted regardless of its requested count, so the total count handed out can
        /// exceed <see cref="MaxCount"/>. CurrentCount is then reset to <paramref name="newCount"/>. Disposable leases
        /// granted by, or made stale by, this reset do not release count when disposed. Callers that pair Wait with a
        /// later manual Release must still reconcile their own outstanding counts across this reset boundary.
        /// </remarks>
        /// <param name="newCount">The value to reset CurrentCount to.</param>
        public void ReleaseAll(int newCount)
        {
            CheckIfDisposed();
            if (newCount < 0 || newCount > _maxCount)
            {
                throw new ArgumentOutOfRangeException(nameof(newCount), $"The '{nameof(newCount)}' provided to '{nameof(ReleaseAll)}' " +
                    $"must be a non-negative number not exceeding '{nameof(MaxCount)}'.");
            }

            lock (_lock)
            {
                CheckIfDisposed();
                _releaseEpoch = new object();
                _currentCount = newCount;
                foreach (var bucket in _queuedAcquireRequests.Values)
                {
                    var queuedAcquireRequest = bucket.Head;
                    while (queuedAcquireRequest != null)
                    {
                        var next = queuedAcquireRequest.Next;
                        bucket.Remove(queuedAcquireRequest);
                        --_queuedWaiterCount;
                        queuedAcquireRequest.GrantAcquire(_releaseEpoch, false);
                        queuedAcquireRequest = next;
                    }
                }
                _queuedAcquireRequests.Clear();
                _activePriorities.Clear();
                _pendingPriorityRemovals.Clear();
                Debug.Assert(_queuedWaiterCount == 0);
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return; // Already disposed
                _disposed = true;
                foreach (var bucket in _queuedAcquireRequests.Values)
                {
                    var entry = bucket.Head;
                    while (entry != null)
                    {
                        var next = entry.Next;
                        bucket.Remove(entry);
                        --_queuedWaiterCount;
                        entry.FailAcquire(new ObjectDisposedException(nameof(AsyncSemaphore)));
                        entry = next;
                    }
                }
                _queuedAcquireRequests.Clear();
                _activePriorities.Clear();
                _pendingPriorityRemovals.Clear();
                Debug.Assert(_queuedWaiterCount == 0);
            }
        }

        private void CheckIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(AsyncSemaphore));
            }
        }

        private void AddToRequests(QueuedAcquire queuedAcquire, int priority)
        {
            if (!_queuedAcquireRequests.TryGetValue(priority, out var bucket))
            {
                bucket = priority == DefaultPriority
                    ? _defaultPriorityBucket
                    : new WaiterBucket(priority);
                Debug.Assert(bucket.Count == 0);
                _queuedAcquireRequests[priority] = bucket;
                _activePriorities.Add(priority);
            }

            switch (_priority)
            {
                case WaiterPriority.LowToHigh:
                    InsertByCount(bucket, queuedAcquire, lowToHigh: true);
                    break;
                case WaiterPriority.HighToLow:
                    InsertByCount(bucket, queuedAcquire, lowToHigh: false);
                    break;
                case WaiterPriority.FirstInFirstOut:
                case WaiterPriority.FirstInFirstOutUnfair:
                case WaiterPriority.Unfair:
                    bucket.AddLast(queuedAcquire);
                    break;
                default:
                    throw new InvalidOperationException($"Priority value {_priority} was not validated by the constructor.");
            }

            ++_queuedWaiterCount;
        }

        private static void InsertByCount(WaiterBucket bucket, QueuedAcquire queuedAcquire, bool lowToHigh)
        {
            // Walk backwards so equal-count waiters remain FIFO-stable.
            var insertionPoint = bucket.Tail;
            while (insertionPoint != null)
            {
                var belongsAfter = lowToHigh
                    ? insertionPoint.Count <= queuedAcquire.Count
                    : insertionPoint.Count >= queuedAcquire.Count;
                if (belongsAfter)
                {
                    bucket.InsertAfter(insertionPoint, queuedAcquire);
                    return;
                }

                insertionPoint = insertionPoint.Previous;
            }

            bucket.AddFirst(queuedAcquire);
        }

        private static readonly DescendingComparer _priorityHighToLowComparer = new DescendingComparer();
        private class DescendingComparer : IComparer<int>
        {
            public int Compare(int x, int y)
            {
                // Invert the comparison to sort in descending order
                return y.CompareTo(x);
            }
        }
    }
}
