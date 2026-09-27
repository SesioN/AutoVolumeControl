using System;
using System.Threading;
using Xunit;

namespace AutoVolumeControl.Tests
{
    public class SingleInstanceTests
    {
        private static string UniqueName() => $@"Local\AutoVolumeControl.Tests.{Guid.NewGuid():N}";

        private static bool AcquireOnOtherThread(string name)
        {
            // Mutex ownership is per thread, so a second instance is simulated on another thread.
            bool first = true;
            var thread = new Thread(() =>
            {
                using var other = SingleInstance.Acquire(name);
                first = other.IsFirstInstance;
            });
            thread.Start();
            thread.Join();
            return first;
        }

        [Fact]
        public void FirstInstance_Owns()
        {
            using var instance = SingleInstance.Acquire(UniqueName());
            Assert.True(instance.IsFirstInstance);
        }

        [Fact]
        public void SecondInstance_IsRejected()
        {
            var name = UniqueName();
            using var first = SingleInstance.Acquire(name);

            Assert.False(AcquireOnOtherThread(name));
        }

        [Fact]
        public void AfterDispose_NextInstanceOwns()
        {
            var name = UniqueName();
            SingleInstance.Acquire(name).Dispose();

            Assert.True(AcquireOnOtherThread(name));
        }

        [Fact]
        public void AbandonedByCrashedInstance_NextInstanceOwns()
        {
            var name = UniqueName();
            var crashed = new Thread(() => new Mutex(false, name).WaitOne());
            crashed.Start();
            crashed.Join();

            Assert.True(AcquireOnOtherThread(name));
        }

        [Fact]
        public void Dispose_Twice_DoesNotThrow()
        {
            var instance = SingleInstance.Acquire(UniqueName());
            instance.Dispose();
            instance.Dispose();
        }
    }
}
