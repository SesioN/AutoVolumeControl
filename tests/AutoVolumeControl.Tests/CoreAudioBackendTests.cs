using System;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Threading;
using System.Runtime.InteropServices;
using AutoVolumeControl.Interop;
using Microsoft.CSharp;
using Xunit;

namespace AutoVolumeControl.Tests
{
    /// <summary>
    /// Runs against the real Windows audio stack. Only sessions of this test process and of a helper process
    /// it starts are changed; the master volume and other apps are never touched.
    /// Skipped when no playback device exists.
    /// </summary>
    [Collection(IsolatedCollection.Name)]
    public class CoreAudioBackendTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
        private static readonly string OwnProcessName = Process.GetCurrentProcess().ProcessName;

        private static void SkipWithoutPlaybackDevice()
        {
            bool available;
            try
            {
                var enumerator = CoreAudio.CreateDeviceEnumerator();
                try
                {
                    enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var device);
                    available = device != null;
                    CoreAudio.Release(device);
                }
                finally
                {
                    CoreAudio.Release(enumerator);
                }
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
        public void SystemSoundsSession_IsWatched_ButNotListedAsApp()
        {
            // Its disconnect notification reveals an audio service restart even when no app plays audio.
            SkipWithoutPlaybackDevice();
            using var audioThread = new AudioThread();
            var backend = new CoreAudioBackend();
            try
            {
                var sessions = audioThread.Post(() =>
                {
                    backend.Attach();
                    return backend.GetSessions().Select(s => (s.Name, s.IsSystemSound)).ToList();
                }).Result;
                Skip.IfNot(sessions.Any(s => s.IsSystemSound), "No system sounds session on this device.");

                int watched = audioThread.Post(() => backend.WatchedSessionCount).Result;
                Assert.Equal(sessions.Count, watched);
            }
            finally
            {
                audioThread.Post(backend.Dispose).Wait(Timeout);
            }
        }

        [SkippableFact]
        public void InvalidateSessions_ReReadsTheList()
        {
            SkipWithoutPlaybackDevice();
            using var audioThread = new AudioThread();
            var backend = new CoreAudioBackend();
            try
            {
                audioThread.Post(() =>
                {
                    backend.Attach();
                    var first = backend.GetSessions().Select(s => s.Id).OrderBy(i => i).ToList();
                    backend.InvalidateSessions();
                    var second = backend.GetSessions().Select(s => s.Id).OrderBy(i => i).ToList();
                    // Same sessions, same watches: re-reading keeps existing watches instead of recreating them.
                    Assert.Equal(first, second);
                    Assert.Equal(first.Count, backend.WatchedSessionCount);
                }).Wait(Timeout);
            }
            finally
            {
                audioThread.Post(backend.Dispose).Wait(Timeout);
            }
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
                Assert.Equal(0.37f, audioThread.Post(() => { using var raw = RawOwnSession.Open(); return raw.Volume; }).Result, 2);
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
            RawOwnSession raw = null;
            try
            {
                var own = FindSession(audioThread, backend, OwnProcessName);
                Skip.If(own == null, "Could not create an audio session for the test process.");

                var events = new RecordingSessionEvents();
                audioThread.Post(() =>
                {
                    raw = RawOwnSession.Open();
                    raw.Control.RegisterAudioSessionNotification(events);
                }).Wait(Timeout);

                bool Seen(float volume, out Guid context)
                {
                    var match = events.VolumeChanges.FirstOrDefault(n => Math.Abs(n.Volume - volume) < 0.001f);
                    context = match.Context;
                    return match != default;
                }

                audioThread.Post(() => { own.Volume = 0.25f; }).Wait(Timeout);
                Assert.True(WaitFor(() => Seen(0.25f, out _), TimeSpan.FromSeconds(3)), "No notification for our own write.");
                Seen(0.25f, out var ownContext);
                Assert.Equal(CoreAudioBackend.EventContext, ownContext);

                audioThread.Post(() => { raw.Volume = 0.75f; }).Wait(Timeout);
                Assert.True(WaitFor(() => Seen(0.75f, out _), TimeSpan.FromSeconds(3)), "No notification for the change by the app.");
                Seen(0.75f, out var appContext);
                Assert.NotEqual(CoreAudioBackend.EventContext, appContext);

                audioThread.Post(() =>
                {
                    raw.Control.UnregisterAudioSessionNotification(events);
                    own.Volume = 1f;
                }).Wait(Timeout);
            }
            finally
            {
                audioThread.Post(() =>
                {
                    raw?.Dispose();
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

                audioThread.Post(() => { using var raw = RawOwnSession.Open(); raw.Volume = 0.6f; }).Wait(Timeout);

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
                // The first notification may be the stream closing, just before the process is gone; the
                // process exit notification follows. GetSessions only re-reads the list after such a
                // notification, so the app disappearing here proves the events alone keep the list current.
                Assert.True(WaitFor(() => !SessionNames(audioThread, backend).Contains(player.Name), Timeout),
                    "The closed app is still listed.");
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

        /// <summary>
        /// This process's own session, opened directly through the Core Audio API (not through the backend), to
        /// act as "the app" changing its own volume and to observe raw notifications.
        /// </summary>
        private sealed class RawOwnSession : IDisposable
        {
            private IMMDeviceEnumerator enumerator;
            private IMMDevice device;
            private IAudioSessionManager2 manager;

            public IAudioSessionControl2 Control { get; private set; }

            public static RawOwnSession Open()
            {
                var raw = new RawOwnSession();
                try
                {
                    raw.enumerator = CoreAudio.CreateDeviceEnumerator();
                    raw.enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out raw.device);
                    raw.manager = CoreAudio.Activate<IAudioSessionManager2>(raw.device, CoreAudio.AudioSessionManager2Id);
                    raw.manager.GetSessionEnumerator(out var sessions);
                    try
                    {
                        int pid = Process.GetCurrentProcess().Id;
                        sessions.GetCount(out int count);
                        for (int i = 0; i < count && raw.Control == null; i++)
                        {
                            sessions.GetSession(i, out var session);
                            var control = (IAudioSessionControl2)session;
                            control.GetProcessId(out int sessionPid);
                            if (sessionPid == pid)
                                raw.Control = control;
                            else
                                CoreAudio.Release(session);
                        }
                    }
                    finally
                    {
                        CoreAudio.Release(sessions);
                    }
                    if (raw.Control == null)
                        throw new InvalidOperationException("Own session not found.");
                    return raw;
                }
                catch
                {
                    raw.Dispose();
                    throw;
                }
            }

            /// <summary>Read and written without an event context, like any other app would.</summary>
            public float Volume
            {
                get
                {
                    ((ISimpleAudioVolume)Control).GetMasterVolume(out float value);
                    return value;
                }
                set
                {
                    var context = Guid.Empty;
                    ((ISimpleAudioVolume)Control).SetMasterVolume(value, ref context);
                }
            }

            public void Dispose()
            {
                CoreAudio.Release(Control);
                CoreAudio.Release(manager);
                CoreAudio.Release(device);
                CoreAudio.Release(enumerator);
            }
        }

        [ComVisible(true)]
        [ClassInterface(ClassInterfaceType.None)]
        private sealed class RecordingSessionEvents : IAudioSessionEvents
        {
            public System.Collections.Concurrent.ConcurrentQueue<(float Volume, Guid Context)> VolumeChanges { get; } =
                new System.Collections.Concurrent.ConcurrentQueue<(float Volume, Guid Context)>();

            public int OnSimpleVolumeChanged(float newVolume, bool newMute, IntPtr eventContext)
            {
                VolumeChanges.Enqueue((newVolume, CoreAudio.ReadGuid(eventContext)));
                return 0;
            }

            public int OnDisplayNameChanged(string newDisplayName, IntPtr eventContext) => 0;
            public int OnIconPathChanged(string newIconPath, IntPtr eventContext) => 0;
            public int OnChannelVolumeChanged(int channelCount, IntPtr newChannelVolumes, int changedChannel, IntPtr eventContext) => 0;
            public int OnGroupingParamChanged(IntPtr newGroupingParam, IntPtr eventContext) => 0;
            public int OnStateChanged(AudioSessionState newState) => 0;
            public int OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason) => 0;
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
