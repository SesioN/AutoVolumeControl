using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Xunit;

namespace AutoVolumeControl.Tests
{
    public class LogFileTests : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "AvcLogTests" + Guid.NewGuid().ToString("N"));

        private string LogPath => Path.Combine(directory, "AutoVolumeControl.log");
        private string OldPath => Path.Combine(directory, "AutoVolumeControl.old.log");

        public void Dispose()
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }

        [Fact]
        public void WritesTraceOutputWithTimestamp()
        {
            using (var log = LogFile.Start(directory))
            {
                Assert.NotNull(log);
                log.WriteLine("hello");
            }

            // Trace listeners are global: lines from tests running in parallel may be in the file too.
            var line = File.ReadAllLines(LogPath).Single(l => l.EndsWith(" hello"));
            Assert.True(DateTime.TryParse(line.Substring(0, 23), out _), line);
        }

        [Fact]
        public void IsRegisteredAsTraceListener_UntilDisposed()
        {
            var log = LogFile.Start(directory);
            Assert.Contains(log, Trace.Listeners.Cast<TraceListener>());

            log.Dispose();

            Assert.DoesNotContain(log, Trace.Listeners.Cast<TraceListener>());
        }

        [Fact]
        public void AppendsAcrossStarts()
        {
            using (var log = LogFile.Start(directory)) log.WriteLine("first");
            using (var log = LogFile.Start(directory)) log.WriteLine("second");

            var text = File.ReadAllText(LogPath);
            Assert.Contains("first", text);
            Assert.Contains("second", text);
        }

        [Fact]
        public void LargeFile_IsRotatedAtStart()
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(LogPath, new string('x', (int)LogFile.MaxBytes + 1));
            File.WriteAllText(OldPath, "older");

            using (var log = LogFile.Start(directory)) log.WriteLine("new");

            Assert.Equal(LogFile.MaxBytes + 1, new FileInfo(OldPath).Length);
            Assert.DoesNotContain("x", File.ReadAllText(LogPath));
        }

        [Fact]
        public void SmallFile_IsKept()
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(LogPath, "keep");

            using (var log = LogFile.Start(directory)) { }

            Assert.Contains("keep", File.ReadAllText(LogPath));
            Assert.False(File.Exists(OldPath));
        }

        [Fact]
        public void RotatesWhileRunning_WhenTheFileGetsTooLarge()
        {
            // The app runs for weeks; the size must stay bounded without a restart.
            using (var log = LogFile.Start(directory, maxBytes: 1000))
            {
                for (int i = 0; i < 100; i++)
                    log.WriteLine($"line {i} " + new string('x', 50));
            }

            Assert.True(new FileInfo(LogPath).Length <= 1000 + 200);
            Assert.True(new FileInfo(OldPath).Length <= 1000 + 200);
            Assert.Contains("line 99", File.ReadAllText(LogPath));
        }

        [Fact]
        public void WriteThenWriteLine_EndUpOnOneLine()
        {
            using (var log = LogFile.Start(directory))
            {
                log.Write("first part, ");
                log.WriteLine("second part");
            }

            Assert.Contains(File.ReadAllLines(LogPath), l => l.EndsWith(" first part, second part"));
        }

        [Fact]
        public void WriteAfterDispose_DoesNotThrow()
        {
            var log = LogFile.Start(directory);
            log.Dispose();
            log.WriteLine("ignored");
        }
    }
}
