﻿using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace AsyncSharp.Test
{
    public class AsyncMutexTests
    {
        [Fact]
        public void Lock()
        {
            using var mutex = new AsyncMutex();
            mutex.Lock();
            mutex.Unlock();
        }

        [Fact]
        public async Task LockAsync()
        {
            using var mutex = new AsyncMutex();
            await mutex.LockAsync();
            mutex.Unlock();
        }

        [Fact]
        public void Lock_Timeout()
        {
            using var mutex = new AsyncMutex();
            mutex.Lock();

            var start = Environment.TickCount;
            var capturedLock = mutex.Lock(TimeSpan.FromMilliseconds(100));

            Assert.False(capturedLock);
            Assert.True(Environment.TickCount - start >= 90);
        }

        [Fact]
        public async Task LockAsync_Timeout()
        {
            using var mutex = new AsyncMutex();
            mutex.Lock();

            var start = Environment.TickCount;
            var capturedLock = await mutex.LockAsync(TimeSpan.FromMilliseconds(100));

            Assert.False(capturedLock);
            Assert.True(Environment.TickCount - start >= 90);
        }

        [Fact]
        public void Lock_CancellationToken()
        {
            using var mutex = new AsyncMutex();
            mutex.Lock();

            var start = Environment.TickCount;
            using (var cancellationTokenSource = new CancellationTokenSource(100))
            {
                Assert.Throws<OperationCanceledException>(()
                    => mutex.Lock(cancellationTokenSource.Token));
                Assert.True(Environment.TickCount - start >= 90);
            }
        }

        [Fact]
        public async Task LockAsync_CancellationToken()
        {
            using var mutex = new AsyncMutex();
            mutex.Lock();

            var start = Environment.TickCount;
            using (var cancellationTokenSource = new CancellationTokenSource(100))
            {
                await Assert.ThrowsAsync<OperationCanceledException>(()
                    => mutex.LockAsync(cancellationTokenSource.Token));
                Assert.True(Environment.TickCount - start >= 90);
            }
        }

        [Fact]
        public void LockAndUnlock()
        {
            using var mutex = new AsyncMutex();
            using (mutex.LockAndUnlock())
            {
                Assert.False(mutex.Lock(TimeSpan.FromMilliseconds(0)));
            }
        }

        [Fact]
        public async Task LockAndUnlockAsync()
        {
            using var mutex = new AsyncMutex();
            using (await mutex.LockAndUnlockAsync())
            {
                Assert.False(await mutex.LockAsync(TimeSpan.FromMilliseconds(0)));
            }
        }
    }
}
