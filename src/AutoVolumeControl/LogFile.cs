using System;
using System.Diagnostics;
using System.IO;

namespace AutoVolumeControl
{
    /// <summary>
    /// Writes <see cref="Trace"/> output with timestamps to %LOCALAPPDATA%\AutoVolumeControl\AutoVolumeControl.log.
    /// When the file exceeds <see cref="MaxBytes"/> at startup it is moved to AutoVolumeControl.old.log, so at most
    /// two files of bounded size exist.
    /// </summary>
    sealed class LogFile : TraceListener
    {
        public const long MaxBytes = 1024 * 1024;

        private readonly StreamWriter writer;
        private readonly object lockObj = new object();

        private LogFile(StreamWriter writer)
        {
            this.writer = writer;
        }

        public static string DefaultDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoVolumeControl");

        /// <summary>Adds the listener; returns null (and logs nothing) if the file cannot be opened.</summary>
        public static LogFile Start(string directory = null)
        {
            try
            {
                directory ??= DefaultDirectory;
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "AutoVolumeControl.log");
                Rotate(path, Path.Combine(directory, "AutoVolumeControl.old.log"));

                var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                var log = new LogFile(new StreamWriter(stream) { AutoFlush = true });
                Trace.Listeners.Add(log);
                return log;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        internal static void Rotate(string path, string oldPath)
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length <= MaxBytes)
                return;

            File.Delete(oldPath);
            File.Move(path, oldPath);
        }

        public override void Write(string message) => WriteLine(message);

        public override void WriteLine(string message)
        {
            lock (lockObj)
            {
                try
                {
                    writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}");
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
                {
                    // Logging must never break the app.
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
                    writer.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }
}
