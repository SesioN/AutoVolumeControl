using System;
using System.Threading;

namespace AutoVolumeControl
{
    /// <summary>Prevents two instances from syncing against each other.</summary>
    sealed class SingleInstance : IDisposable
    {
        public const string DefaultName = @"Local\AutoVolumeControl.SingleInstance";

        private readonly Mutex mutex;
        private bool owned;

        private SingleInstance(Mutex mutex, bool owned)
        {
            this.mutex = mutex;
            this.owned = owned;
        }

        public bool IsFirstInstance => owned;

        public static SingleInstance Acquire(string name = DefaultName)
        {
            var mutex = new Mutex(false, name);
            bool owned;
            try
            {
                owned = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // The previous instance crashed; the mutex is ours now.
                owned = true;
            }
            return new SingleInstance(mutex, owned);
        }

        public void Dispose()
        {
            if (owned)
            {
                mutex.ReleaseMutex();
                owned = false;
            }
            mutex.Dispose();
        }
    }
}
