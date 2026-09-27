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
    /// files of bounded size exist, even if the app runs for weeks.
    /// </summary>
    sealed class LogFile : TraceListener
    {
        public const long MaxBytes = 1024 * 1024;

        private readonly object lockObj = new object();
        private readonly string path;
        private readonly string oldPath;
        private readonly long maxBytes;
        // Trace listeners are shared by all threads; a partial line belongs to the thread that wrote it.
        private readonly ThreadLocal<StringBuilder> pending = new ThreadLocal<StringBuilder>(() => new StringBuilder());
        private StreamWriter writer;

        private LogFile(string path, string oldPath, long maxBytes)
        {
            this.path = path;
            this.oldPath = oldPath;
            this.maxBytes = maxBytes;
        }

        public static string DefaultDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoVolumeControl");

        /// <summary>Adds the listener; returns null (and logs nothing) if the file cannot be opened.</summary>
        public static LogFile Start(string directory = null, long maxBytes = MaxBytes)
        {
            try
            {
                directory ??= DefaultDirectory;
                Directory.CreateDirectory(directory);
                var log = new LogFile(Path.Combine(directory, "AutoVolumeControl.log"), Path.Combine(directory, "AutoVolumeControl.old.log"), maxBytes);
                log.Open();
                Trace.Listeners.Add(log);
                return log;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        private void Open()
        {
            if (File.Exists(path) && new FileInfo(path).Length > maxBytes)
                Rotate();
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            writer = new StreamWriter(stream) { AutoFlush = true };
        }

        private void Rotate()
        {
            File.Delete(oldPath);
            File.Move(path, oldPath);
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
                if (writer == null)
                    return;

                var line = pending.Value;
                try
                {
                    line.Append(message);
                    writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}");
                    line.Clear();

                    if (writer.BaseStream.Length > maxBytes)
                    {
                        writer.Dispose();
                        writer = null;
                        Open();
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ObjectDisposedException)
                {
                    // Logging must never break the app.
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
                    writer?.Dispose();
                    writer = null;
                }
                pending.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
