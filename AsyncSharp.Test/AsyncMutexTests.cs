using System;
using System.Collections.Generic;
using System.Text;
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
            var mutex = new AsyncMutex();
            mutex.Lock();
            mutex.Unlock();
        }

        [Fact]
        public async Task LockAsync()
        {
            var mutex = new AsyncMutex();
            await mutex.LockAsync();
            mutex.Unlock();
        }

        [Fact]
        public void Lock_Timeout()
        {
            var mutex = new AsyncMutex();
            mutex.Lock();

            var start = DateTime.UtcNow;
            var capturedLock = mutex.Lock(100);

            Assert.False(capturedLock);
            Assert.True(DateTime.UtcNow - start > TimeSpan.FromMilliseconds(100));
        }

        [Fact]
        public async Task LockAsync_Timeout()
        {
            var mutex = new AsyncMutex();
            mutex.Lock();

            var start = DateTime.UtcNow;
            var capturedLock = await mutex.LockAsync(100);

            Assert.False(capturedLock);
            Assert.True(DateTime.UtcNow - start > TimeSpan.FromMilliseconds(100));
        }

        [Fact]
        public void Lock_CancellationToken()
        {
            var mutex = new AsyncMutex();
            mutex.Lock();

            var start = DateTime.UtcNow;
            using (var cancellationTokenSource = new CancellationTokenSource(100))
            {
                Assert.Throws<OperationCanceledException>(()
                    => mutex.Lock(cancellationTokenSource.Token));
                Assert.True(DateTime.UtcNow - start > TimeSpan.FromMilliseconds(100));
            }
        }

        [Fact]
        public async Task LockAsync_CancellationToken()
        {
            var mutex = new AsyncMutex();
            mutex.Lock();

            var start = DateTime.UtcNow;
            using (var cancellationTokenSource = new CancellationTokenSource(100))
            {
                await Assert.ThrowsAsync<OperationCanceledException>(()
                    => mutex.LockAsync(cancellationTokenSource.Token));
                Assert.True(DateTime.UtcNow - start > TimeSpan.FromMilliseconds(100));
            }
        }

        [Fact]
        public void LockAndUnlock()
        {
            var mutex = new AsyncMutex();
            using (mutex.LockAndUnlock())
            {
                Assert.False(mutex.Lock(0));
            }
        }

        [Fact]
        public async Task LockAndUnlockAsync()
        {
            var mutex = new AsyncMutex();
            using (await mutex.LockAndUnlockAsync())
            {
                Assert.False(await mutex.LockAsync(0));
            }
        }
    }
}
