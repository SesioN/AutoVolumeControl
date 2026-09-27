using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Threading;
using CSCore.CoreAudioAPI;
using Xunit;

namespace AutoVolumeControl.Tests
{
    /// <summary>
    /// Runs against the real Windows audio stack. Only this test process's own session is changed;
    /// the master volume and other apps are never touched. Skipped when no playback device exists.
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

        [SkippableFact]
        public void Attach_ReadsMasterVolumeAndSessions()
        {
            SkipWithoutPlaybackDevice();
            using var audioThread = new AudioThread();
            var backend = new CoreAudioBackend();

            var (volume, sessionCount) = audioThread.Post(() =>
            {
                backend.Attach();
                var master = backend.GetMasterVolume();
                var sessions = backend.GetSessions();
                foreach (var s in sessions) s.Dispose();
                return (master.Volume, sessions.Count);
            }).Result;

            Assert.InRange(volume, 0f, 1f);
            Assert.True(sessionCount >= 0);
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
        public void Dispose_Twice_DoesNotThrow()
        {
            SkipWithoutPlaybackDevice();
            using var audioThread = new AudioThread();
            var backend = new CoreAudioBackend();

            audioThread.Post(() =>
            {
                backend.Attach();
                backend.Dispose();
                backend.Dispose();
                Assert.False(backend.IsAttached);
            }).Wait(Timeout);
        }

        [SkippableFact]
        public void OwnSession_IsListedByProcessName_AndCanBeSynced()
        {
            SkipWithoutPlaybackDevice();
            using var audioThread = new AudioThread();
            var backend = new CoreAudioBackend();
            int sessionsCreated = 0;
            backend.SessionCreated += (s, e) => Interlocked.Increment(ref sessionsCreated);
            audioThread.Post(backend.Attach).Wait(Timeout);

            using var silence = PlaySilence();
            try
            {
                // Wait for the notification before enumerating: Attach() alone must enable it,
                // because Windows only sends it after GetCount was called on an enumerator.
                var created = Stopwatch.StartNew();
                while (Volatile.Read(ref sessionsCreated) == 0 && created.Elapsed < TimeSpan.FromSeconds(3))
                    Thread.Sleep(50);
                Assert.True(sessionsCreated > 0, "SessionCreated was not raised for the new session.");

                IAudioSession own = null;
                var watch = Stopwatch.StartNew();
                while (own == null && watch.Elapsed < Timeout)
                {
                    own = audioThread.Post(() =>
                    {
                        var sessions = backend.GetSessions();
                        var match = sessions.FirstOrDefault(s => s.Name == OwnProcessName);
                        foreach (var s in sessions.Where(s => s != match)) s.Dispose();
                        return match;
                    }).Result;
                    if (own == null) Thread.Sleep(100);
                }
                Skip.If(own == null, "Could not create an audio session for the test process.");

                audioThread.Post(() => own.Apply(0.37f, false)).Wait(Timeout);
                Assert.Equal(0.37f, ReadOwnSessionVolume(audioThread), 2);

                audioThread.Post(() => own.Apply(1f, false)).Wait(Timeout);
                audioThread.Post(own.Dispose).Wait(Timeout);
            }
            finally
            {
                audioThread.Post(backend.Dispose).Wait(Timeout);
            }
        }

        private static float ReadOwnSessionVolume(AudioThread audioThread)
        {
            return audioThread.Post(() =>
            {
                int pid = Process.GetCurrentProcess().Id;
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                using var manager = AudioSessionManager2.FromMMDevice(device);
                using var sessions = manager.GetSessionEnumerator();
                foreach (var session in sessions)
                {
                    using (session)
                    using (var control = session.QueryInterface<AudioSessionControl2>())
                    {
                        if (control.ProcessID != pid)
                            continue;
                        using var volume = session.QueryInterface<SimpleAudioVolume>();
                        return volume.MasterVolume;
                    }
                }
                throw new InvalidOperationException("Own session not found.");
            }).Result;
        }

        /// <summary>Plays an inaudible looping WAV so that Windows creates a session for this process.</summary>
        private static SoundPlayer PlaySilence()
        {
            const int sampleRate = 8000;
            const int samples = sampleRate; // one second
            var stream = new MemoryStream();
            var writer = new BinaryWriter(stream);
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
            stream.Position = 0;

            var player = new SoundPlayer(stream);
            player.PlayLooping();
            return player;
        }
    }
}
