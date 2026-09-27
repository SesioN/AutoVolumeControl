using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace AutoVolumeControl
{
    /// <summary>
    /// A single long-lived MTA thread that owns all Core Audio COM objects.
    /// Work runs strictly in the order it was posted, so volume updates can no longer overtake each other,
    /// and COM is initialized exactly once for the lifetime of the objects.
    /// </summary>
    sealed class AudioThread : IDisposable
    {
        private readonly BlockingCollection<Action> queue = new BlockingCollection<Action>();
        private readonly Thread thread;
        private int disposed;

        public AudioThread(string name = "AutoVolumeControl audio")
        {
            thread = new Thread(Run)
            {
                Name = name,
                IsBackground = true
            };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        public bool IsCurrentThread => Thread.CurrentThread == thread;

        public Task Post(Action action)
        {
            return Post(() =>
            {
                action();
                return true;
            });
        }

        public Task<T> Post<T>(Func<T> func)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                queue.Add(() =>
                {
                    try
                    {
                        tcs.SetResult(func());
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(ex);
                    }
                });
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ObjectDisposedException)
            {
                // Adding after Dispose (completed or already disposed queue).
                tcs.SetException(new ObjectDisposedException(nameof(AudioThread)));
            }
            return tcs.Task;
        }

        private void Run()
        {
            foreach (var work in queue.GetConsumingEnumerable())
            {
                work();
            }
        }

        /// <summary>Runs the work that is already queued, then stops the thread.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;

            // The queue is not disposed: a Post racing with Dispose must see a completed queue, not a disposed one.
            queue.CompleteAdding();
            if (!IsCurrentThread)
                thread.Join(TimeSpan.FromSeconds(2));
        }
    }
}
