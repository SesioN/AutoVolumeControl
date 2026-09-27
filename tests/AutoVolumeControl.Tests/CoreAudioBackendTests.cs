using System;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Threading;
using CSCore.CoreAudioAPI;
using Microsoft.CSharp;
using Xunit;

namespace AutoVolumeControl.Tests
{
    /// <summary>
    /// Runs against the real Windows audio stack. Only sessions of this test process and of a helper process
    /// it starts are changed; the master volume and other apps are never touched.
    /// Skipped when no playback device exists.
    /// </summary>
    [Collection("CoreAudio")]
    public class CoreAudioBackendTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
        private static readonly string OwnProcessName = Process.GetCurrentProcess().ProcessName;

        private static void SkipWithoutPlaybackDevice()
        {
            bool available;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                available = device != null;
            }
            catch (Exception)
            {
                available = false;
            }
            Skip.IfNot(available, "No default playback device.");
        }

        private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
        {
            var watch = Stopwatch.StartNew();
            while (!condition())
            {
                if (watch.Elapsed > timeout)
                    return false;
                Thread.Sleep(50);
            }
            return true;
        }

        private static string[] SessionNames(AudioThread audioThread, CoreAudioBackend backend)
        {
            return audioThread.Post(() =>
            {
                var sessions = backend.GetSessions();
                var names = sessions.Select(s => s.Name).ToArray();
                foreach (var s in sessions) s.Dispose();
                return names;
            }).Result;
        }

        private static IAudioSession FindSession(AudioThread audioThread, CoreAudioBackend backend, string name)
        {
            IAudioSession found = null;
            WaitFor(() =>
            {
                found = audioThread.Post(() =>
                {
                    var sessions = backend.GetSessions();
                    var match = sessions.FirstOrDefault(s => s.Name == name);
                    foreach (var s in sessions.Where(s => s != match)) s.Dispose();
                    return match;
                }).Result;
                return found != null;
            }, Timeout);
            return found;
        }

        [SkippableFact]
        public void Attach_ReadsMasterVolumeAndSessions()
        {
            SkipWithoutPlaybackDevice();
            using var audioThread = new AudioThread();
            var backend = new CoreAudioBackend();

            var volume = audioThread.Post(() =>
            {
                backend.Attach();
                return backend.GetMasterVolume().Volume;
            }).Result;

            Assert.True(backend.IsAttached);
            Assert.InRange(volume, 0f, 1f);
            Assert.NotNull(SessionNames(audioThread, backend));
            audioThread.Post(backend.Dispose).Wait(Timeout);
        }

        [SkippableFact]
        public void Attach_Twice_Rebinds()
        {
            SkipWithoutPlaybackDevice();
            using var audioThread = new AudioThread();
            var backend = new CoreAudioBackend();

            audioThread.Post(() =>
            {
                backend.Attach();
                backend.Attach();
                Assert.True(backend.IsAttached);
                backend.GetMasterVolume();
            }).Wait(Timeout);

            audioThread.Post(backend.Dispose).Wait(Timeout);
        }

        [Fact]
        public void BeforeAttach_CallsFail()
        {
            var backend = new CoreAudioBackend();

            Assert.False(backend.IsAttached);
            Assert.Throws<InvalidOperationException>(() => backend.GetMasterVolume());
            Assert.Throws<InvalidOperationException>(() => backend.GetSessions());
            backend.Dispose();
        }

        [SkippableFact]
        public void Dispose_Twice_DoesNotThrow_AndDetaches()
        {
            SkipWithoutPlaybackDevice();
            using var audioThread = new AudioThread();
            var backend = new CoreAudioBackend();

            audioThread.Post(() =>
            {
                backend.Attach();
                backend.GetSessions().ToList().ForEach(s => s.Dispose());
                backend.Dispose();
                backend.Dispose();
                Assert.False(backend.IsAttached);
                Assert.Equal(0, backend.WatchedSessionCount);
            }).Wait(Timeout);
        }

        [SkippableFact]
        public void OwnSession_IsListed_NotifiesOnCreation_AndCanBeSynced()
        {
            SkipWithoutPlaybackDevice();
            using var audioThread = new AudioThread();
            var backend = new CoreAudioBackend();
            int changes = 0;
            backend.SessionsChanged += (s, e) => Interlocked.Increment(ref changes);
            audioThread.Post(backend.Attach).Wait(Timeout);

            using var silence = PlaySilence();
            try
            {
                // Attach() alone must enable session notifications (GetCount was called).
                Assert.True(WaitFor(() => Volatile.Read(ref changes) > 0, TimeSpan.FromSeconds(3)), "No notification for the new session.");

                var own = FindSession(audioThread, backend, OwnProcessName);
                Skip.If(own == null, "Could not create an audio session for the test process.");

                audioThread.Post(() => { own.Volume = 0.37f; }).Wait(Timeout);
                Assert.Equal(0.37f, ReadOwnSessionVolume(audioThread), 2);
                Assert.Equal(0.37f, audioThread.Post(() => own.Volume).Result, 2);

                audioThread.Post(() => { own.Volume = 1f; own.Dispose(); }).Wait(Timeout);
            }
            finally
            {
                audioThread.Post(backend.Dispose).Wait(Timeout);
            }
        }

        [SkippableFact]
        public void OwnWrites_AreTaggedWithOurEventContext_ChangesByTheAppAreNot()
        {
            // Our writes carry CoreAudioBackend.EventContext, which the session watch ignores (no feedback loop);
            // writes by anyone else carry a different context and are reported.
            // Checked on the raw notifications, so other programs changing volumes cannot disturb the test.
            SkipWithoutPlaybackDevice();
            using var audioThread = new AudioThread();
            var backend = new CoreAudioBackend();
            audioThread.Post(backend.Attach).Wait(Timeout);

            using var silence = PlaySilence();
            AudioSessionControl rawSession = null;
            AudioSessionControl2 rawControl = null;
            MMDeviceEnumerator rawEnumerator = null;
            MMDevice rawDevice = null;
            AudioSessionManager2 rawManager = null;
            try
            {
                var own = FindSession(audioThread, backend, OwnProcessName);
                Skip.If(own == null, "Could not create an audio session for the test process.");

                var notifications = new System.Collections.Concurrent.ConcurrentQueue<(float Volume, Guid Context)>();
                audioThread.Post(() =>
                {
                    rawEnumerator = new MMDeviceEnumerator();
                    rawDevice = rawEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    rawManager = AudioSessionManager2.FromMMDevice(rawDevice);
                    rawControl = OwnSessionControl(rawManager, out rawSession);
                    rawSession.SimpleVolumeChanged += (s, e) => notifications.Enqueue((e.NewVolume, e.EventContext));
                }).Wait(Timeout);

                bool Seen(float volume, out Guid context)
                {
                    var match = notifications.FirstOrDefault(n => Math.Abs(n.Volume - volume) < 0.001f);
                    context = match.Context;
                    return match != default;
                }

                audioThread.Post(() => { own.Volume = 0.25f; }).Wait(Timeout);
                Assert.True(WaitFor(() => Seen(0.25f, out _), TimeSpan.FromSeconds(3)), "No notification for our own write.");
                Seen(0.25f, out var ownContext);
                Assert.Equal(CoreAudioBackend.EventContext, ownContext);

                SetOwnSessionVolumeAsApp(audioThread, 0.75f);
                Assert.True(WaitFor(() => Seen(0.75f, out _), TimeSpan.FromSeconds(3)), "No notification for the app's write.");
                Seen(0.75f, out var appContext);
                Assert.NotEqual(CoreAudioBackend.EventContext, appContext);

                audioThread.Post(() => { own.Volume = 1f; own.Dispose(); }).Wait(Timeout);
            }
            finally
            {
                audioThread.Post(() =>
                {
                    rawControl?.Dispose();
                    rawSession?.Dispose();
                    rawManager?.Dispose();
                    rawDevice?.Dispose();
                    rawEnumerator?.Dispose();
                    backend.Dispose();
                }).Wait(Timeout);
            }
        }

        [SkippableFact]
        public void ChangeByTheApp_IsReported()
        {
            SkipWithoutPlaybackDevice();
            using var audioThread = new AudioThread();
            var backend = new CoreAudioBackend();
            audioThread.Post(backend.Attach).Wait(Timeout);

            using var silence = PlaySilence();
            try
            {
                var own = FindSession(audioThread, backend, OwnProcessName);
                Skip.If(own == null, "Could not create an audio session for the test process.");
                int changes = 0;
                backend.SessionsChanged += (s, e) => Interlocked.Increment(ref changes);

                SetOwnSessionVolumeAsApp(audioThread, 0.6f);

                Assert.True(WaitFor(() => Volatile.Read(ref changes) > 0, TimeSpan.FromSeconds(3)), "No notification for an external volume change.");
                audioThread.Post(() => { own.Volume = 1f; own.Dispose(); }).Wait(Timeout);
            }
            finally
            {
                audioThread.Post(backend.Dispose).Wait(Timeout);
            }
        }

        [SkippableFact]
        public void ClosingAnApp_NotifiesAndRemovesItsSession()
        {
            SkipWithoutPlaybackDevice();
            using var player = SilentPlayer.Start();
            using var audioThread = new AudioThread();
            var backend = new CoreAudioBackend();
            audioThread.Post(backend.Attach).Wait(Timeout);
            try
            {
                Skip.IfNot(WaitFor(() => SessionNames(audioThread, backend).Contains(player.Name), Timeout),
                    "The helper process did not create an audio session.");
                int changes = 0;
                backend.SessionsChanged += (s, e) => Interlocked.Increment(ref changes);

                player.Kill();

                Assert.True(WaitFor(() => Volatile.Read(ref changes) > 0, Timeout), "No notification when the app closed.");
                Assert.DoesNotContain(player.Name, SessionNames(audioThread, backend));
            }
            finally
            {
                audioThread.Post(backend.Dispose).Wait(Timeout);
            }
        }

        [SkippableFact]
        public void RepeatedReads_DoNotLeakHandlesOrWatches()
        {
            SkipWithoutPlaybackDevice();
            using var audioThread = new AudioThread();
            var backend = new CoreAudioBackend();
            using var silence = PlaySilence();
            audioThread.Post(backend.Attach).Wait(Timeout);
            try
            {
                FindSession(audioThread, backend, OwnProcessName)?.Dispose();

                void Read(int times) => audioThread.Post(() =>
                {
                    for (int i = 0; i < times; i++)
                        foreach (var s in backend.GetSessions()) s.Dispose();
                }).Wait(TimeSpan.FromSeconds(60));

                Read(20);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                int handles = Process.GetCurrentProcess().HandleCount;
                int watches = audioThread.Post(() => backend.WatchedSessionCount).Result;

                Read(300);
                GC.Collect();
                GC.WaitForPendingFinalizers();

                Assert.InRange(Process.GetCurrentProcess().HandleCount - handles, -50, 50);
                Assert.Equal(watches, audioThread.Post(() => backend.WatchedSessionCount).Result);
            }
            finally
            {
                audioThread.Post(backend.Dispose).Wait(Timeout);
            }
        }

        [SkippableFact]
        public void RepeatedAttach_DoesNotLeakHandles()
        {
            SkipWithoutPlaybackDevice();
            using var audioThread = new AudioThread();
            var backend = new CoreAudioBackend();

            void Attach(int times) => audioThread.Post(() =>
            {
                for (int i = 0; i < times; i++)
                {
                    backend.Attach();
                    foreach (var s in backend.GetSessions()) s.Dispose();
                }
            }).Wait(TimeSpan.FromSeconds(60));

            Attach(10);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            int handles = Process.GetCurrentProcess().HandleCount;

            Attach(100);
            GC.Collect();
            GC.WaitForPendingFinalizers();

            Assert.InRange(Process.GetCurrentProcess().HandleCount - handles, -50, 50);
            audioThread.Post(backend.Dispose).Wait(Timeout);
        }

        private static AudioSessionControl2 OwnSessionControl(AudioSessionManager2 manager, out AudioSessionControl owner)
        {
            int pid = Process.GetCurrentProcess().Id;
            using var sessions = manager.GetSessionEnumerator();
            foreach (var session in sessions)
            {
                var control = session.QueryInterface<AudioSessionControl2>();
                if (control.ProcessID == pid)
                {
                    owner = session;
                    return control;
                }
                control.Dispose();
                session.Dispose();
            }
            throw new InvalidOperationException("Own session not found.");
        }

        private static float ReadOwnSessionVolume(AudioThread audioThread)
        {
            return audioThread.Post(() =>
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                using var manager = AudioSessionManager2.FromMMDevice(device);
                using var control = OwnSessionControl(manager, out var session);
                using (session)
                using (var volume = session.QueryInterface<SimpleAudioVolume>())
                    return volume.MasterVolume;
            }).Result;
        }

        private static void SetOwnSessionVolumeAsApp(AudioThread audioThread, float value)
        {
            audioThread.Post(() =>
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                using var manager = AudioSessionManager2.FromMMDevice(device);
                using var control = OwnSessionControl(manager, out var session);
                using (session)
                using (var volume = session.QueryInterface<SimpleAudioVolume>())
                    volume.MasterVolume = value;
            }).Wait(Timeout);
        }

        private static byte[] SilentWav()
        {
            const int sampleRate = 8000;
            const int samples = sampleRate; // one second
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write("RIFF".ToCharArray());
            writer.Write(36 + samples * 2);
            writer.Write("WAVEfmt ".ToCharArray());
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(sampleRate);
            writer.Write(sampleRate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data".ToCharArray());
            writer.Write(samples * 2);
            writer.Write(new byte[samples * 2]);
            writer.Flush();
            return stream.ToArray();
        }

        /// <summary>Plays an inaudible looping WAV so that Windows creates a session for this process.</summary>
        private static SoundPlayer PlaySilence()
        {
            var player = new SoundPlayer(new MemoryStream(SilentWav()));
            player.PlayLooping();
            return player;
        }

        /// <summary>A separate, uniquely named process that plays silence, compiled on the fly.</summary>
        private sealed class SilentPlayer : IDisposable
        {
            private readonly Process process;
            private readonly string directory;

            private SilentPlayer(Process process, string directory, string name)
            {
                this.process = process;
                this.directory = directory;
                Name = name;
            }

            public string Name { get; }

            public static SilentPlayer Start()
            {
                var name = "AvcTestPlayer" + Guid.NewGuid().ToString("N").Substring(0, 8);
                var directory = Path.Combine(Path.GetTempPath(), name);
                Directory.CreateDirectory(directory);
                var wav = Path.Combine(directory, "silence.wav");
                File.WriteAllBytes(wav, SilentWav());
                var exe = Path.Combine(directory, name + ".exe");

                const string source = @"
                    class P { static void Main(string[] a) {
                        new System.Media.SoundPlayer(a[0]).PlayLooping();
                        System.Threading.Thread.Sleep(60000);
                    } }";
                using (var compiler = new CSharpCodeProvider())
                {
                    var options = new CompilerParameters(new[] { "System.dll" }, exe) { GenerateExecutable = true };
                    options.CompilerOptions = "/target:winexe";
                    var result = compiler.CompileAssemblyFromSource(options, source);
                    if (result.Errors.HasErrors)
                        throw new InvalidOperationException(string.Join("\n", result.Errors.Cast<CompilerError>()));
                }

                var process = Process.Start(new ProcessStartInfo(exe, $"\"{wav}\"") { UseShellExecute = false, CreateNoWindow = true });
                return new SilentPlayer(process, directory, name);
            }

            public void Kill()
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
            }

            public void Dispose()
            {
                Kill();
                process.Dispose();
                try
                {
                    Directory.Delete(directory, true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
