using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace AutoVolumeControl
{
    /// <summary>
    /// Writes <see cref="Trace"/> output with timestamps to %LOCALAPPDATA%\AutoVolumeControl\AutoVolumeControl.log.
    /// Whenever the file exceeds <see cref="MaxBytes"/> it is moved to AutoVolumeControl.old.log, so at most two
    /// files of bounded size exist, even if the app runs for weeks. If the files are locked by another program
    /// (e.g. a log viewer), logging continues in the current file and rotating or reopening is retried later.
    /// </summary>
    sealed class LogFile : TraceListener
    {
        public const long MaxBytes = 1024 * 1024;
        public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);

        private readonly object lockObj = new object();
        private readonly string path;
        private readonly string oldPath;
        private readonly long maxBytes;
        private readonly Func<DateTime> clock;
        // Trace listeners are shared by all threads; a partial line belongs to the thread that wrote it.
        private readonly ThreadLocal<StringBuilder> pending = new ThreadLocal<StringBuilder>(() => new StringBuilder());
        private StreamWriter writer;
        private DateTime nextRetry = DateTime.MinValue;
        private bool disposed;

        private LogFile(string path, string oldPath, long maxBytes, Func<DateTime> clock)
        {
            this.path = path;
            this.oldPath = oldPath;
            this.maxBytes = maxBytes;
            this.clock = clock;
        }

        public static string DefaultDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoVolumeControl");

        /// <summary>Adds the listener. Returns null if the directory is not usable at all.</summary>
        public static LogFile Start(string directory = null, long maxBytes = MaxBytes, Func<DateTime> clock = null)
        {
            try
            {
                directory ??= DefaultDirectory;
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }

            var log = new LogFile(Path.Combine(directory, "AutoVolumeControl.log"), Path.Combine(directory, "AutoVolumeControl.old.log"), maxBytes, clock ?? (() => DateTime.UtcNow));
            lock (log.lockObj)
            {
                log.OpenIfPossible();
            }
            Trace.Listeners.Add(log);
            return log;
        }

        /// <summary>Opens the file, rotating it first if it is too large. Called under the lock.</summary>
        private void OpenIfPossible()
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > maxBytes)
                    TryRotate();
                var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                writer = new StreamWriter(stream) { AutoFlush = true };
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                writer = null;
                nextRetry = clock() + RetryInterval;
            }
        }

        private void TryRotate()
        {
            try
            {
                File.Delete(oldPath);
                File.Move(path, oldPath);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Locked by another program: keep appending and try again later.
                nextRetry = clock() + RetryInterval;
            }
        }

        /// <summary>Collects partial output until the line is completed by <see cref="WriteLine"/>.</summary>
        public override void Write(string message)
        {
            try
            {
                pending.Value.Append(message);
            }
            catch (ObjectDisposedException)
            {
                // A thread still logging while the app exits.
            }
        }

        public override void WriteLine(string message)
        {
            lock (lockObj)
            {
                if (disposed)
                    return;

                if (writer == null)
                {
                    if (clock() < nextRetry)
                        return;
                    OpenIfPossible();
                    if (writer == null)
                        return;
                }

                var line = pending.Value;
                try
                {
                    line.Append(message);
                    writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}");

                    if (writer.BaseStream.Length > maxBytes && clock() >= nextRetry)
                    {
                        writer.Dispose();
                        writer = null;
                        OpenIfPossible();
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ObjectDisposedException)
                {
                    // Logging must never break the app.
                }
                finally
                {
                    line.Clear();
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Trace.Listeners.Remove(this);
                lock (lockObj)
                {
                    disposed = true;
                    writer?.Dispose();
                    writer = null;
                }
                pending.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
