using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace AutoVolumeControl.Tests
{
    public class AudioThreadTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        [Fact]
        public void RunsAllWorkOnOneMtaThread()
        {
            using var audioThread = new AudioThread();
            var threadIds = new ConcurrentBag<int>();
            var apartments = new ConcurrentBag<ApartmentState>();

            var tasks = Enumerable.Range(0, 20).Select(_ => audioThread.Post(() =>
            {
                threadIds.Add(Thread.CurrentThread.ManagedThreadId);
                apartments.Add(Thread.CurrentThread.GetApartmentState());
            })).ToArray();

            Assert.True(Task.WaitAll(tasks, Timeout));
            Assert.Single(threadIds.Distinct());
            Assert.All(apartments, a => Assert.Equal(ApartmentState.MTA, a));
            Assert.NotEqual(Thread.CurrentThread.ManagedThreadId, threadIds.First());
        }

        [Fact]
        public void RunsWorkInPostingOrder()
        {
            using var audioThread = new AudioThread();
            var order = new List<int>();

            var tasks = Enumerable.Range(0, 100).Select(i => audioThread.Post(() => order.Add(i))).ToArray();

            Assert.True(Task.WaitAll(tasks, Timeout));
            Assert.Equal(Enumerable.Range(0, 100), order);
        }

        [Fact]
        public void PostWithResult_ReturnsValue()
        {
            using var audioThread = new AudioThread();
            Assert.Equal(42, audioThread.Post(() => 42).Result);
        }

        [Fact]
        public void IsCurrentThread_OnlyInsideWork()
        {
            using var audioThread = new AudioThread();
            Assert.False(audioThread.IsCurrentThread);
            Assert.True(audioThread.Post(() => audioThread.IsCurrentThread).Result);
        }

        [Fact]
        public void FailingWork_FaultsItsTaskAndTheThreadKeepsRunning()
        {
            using var audioThread = new AudioThread();

            var failing = audioThread.Post(() => throw new InvalidOperationException("boom"));
            var next = audioThread.Post(() => 1);

            var ex = Assert.Throws<AggregateException>(() => failing.Wait(Timeout));
            Assert.IsType<InvalidOperationException>(ex.InnerException);
            Assert.Equal(1, next.Result);
        }

        [Fact]
        public void Dispose_RunsQueuedWorkFirst()
        {
            var audioThread = new AudioThread();
            var gate = new ManualResetEventSlim();
            audioThread.Post(() => gate.Wait(Timeout));
            var queued = audioThread.Post(() => 7);

            gate.Set();
            audioThread.Dispose();

            Assert.True(queued.IsCompleted);
            Assert.Equal(7, queued.Result);
        }

        [Fact]
        public void PostAfterDispose_ReturnsFaultedTask()
        {
            var audioThread = new AudioThread();
            audioThread.Dispose();

            var task = audioThread.Post(() => { });

            Assert.True(task.IsFaulted);
            Assert.IsType<ObjectDisposedException>(task.Exception.InnerException);
        }

        [Fact]
        public void DisposeTwice_DoesNotThrow()
        {
            var audioThread = new AudioThread();
            audioThread.Dispose();
            audioThread.Dispose();
        }

        [Fact]
        public void DisposeFromInsideWork_DoesNotDeadlock()
        {
            var audioThread = new AudioThread();
            var task = audioThread.Post(() => audioThread.Dispose());

            Assert.True(task.Wait(Timeout));
        }

        [Fact]
        public void ContinuationsDoNotRunOnTheAudioThread()
        {
            using var audioThread = new AudioThread();
            int workThread = 0;
            int continuationThread = 0;

            audioThread.Post(() => { workThread = Thread.CurrentThread.ManagedThreadId; })
                .ContinueWith(_ => { continuationThread = Thread.CurrentThread.ManagedThreadId; }, TaskContinuationOptions.ExecuteSynchronously)
                .Wait(Timeout);

            Assert.NotEqual(workThread, continuationThread);
        }
    }
}
