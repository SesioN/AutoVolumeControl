using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AutoVolumeControl.Tests
{
    /// <summary>A throw-away key below HKCU\SOFTWARE\AutoVolumeControl.Tests, deleted on dispose.</summary>
    sealed class TestRegistryKey : IDisposable
    {
        private const string Root = @"SOFTWARE\AutoVolumeControl.Tests";

        public TestRegistryKey()
        {
            Path = $@"{Root}\{Guid.NewGuid():N}";
            Registry.CurrentUser.CreateSubKey(Path, true).Dispose();
        }

        public string Path { get; }

        public object GetValue(string name)
        {
            using var key = Registry.CurrentUser.OpenSubKey(Path, false);
            return key?.GetValue(name);
        }

        public void SetValue(string name, object value, RegistryValueKind kind = RegistryValueKind.String)
        {
            using var key = Registry.CurrentUser.CreateSubKey(Path, true);
            key.SetValue(name, value, kind);
        }

        public void Dispose()
        {
            Registry.CurrentUser.DeleteSubKeyTree(Path, false);
            try
            {
                // Removes the shared root once the last test key is gone.
                Registry.CurrentUser.DeleteSubKey(Root, false);
            }
            catch (InvalidOperationException)
            {
                // Other tests still use it.
            }
        }
    }

    sealed class InMemorySettingsStore : ISettingsStore
    {
        public Dictionary<string, string> Values { get; } = new Dictionary<string, string>();

        public string Get(string name) => Values.TryGetValue(name, out var value) ? value : null;

        public void Set(string name, string value) => Values[name] = value;

        public bool Exists(string name) => Values.ContainsKey(name);

        public Dictionary<string, InMemorySettingsStore> SubStores { get; } = new Dictionary<string, InMemorySettingsStore>();

        public ISettingsStore GetSubStore(string name)
        {
            if (!SubStores.TryGetValue(name, out var subStore))
                SubStores[name] = subStore = new InMemorySettingsStore();
            return subStore;
        }

        /// <summary>The per-app ratios (percent) of <see cref="AppPreferences"/>.</summary>
        public Dictionary<string, string> Ratios => ((InMemorySettingsStore)GetSubStore(AppPreferences.RatiosStoreName)).Values;
    }

    sealed class FakeSession : IAudioSession
    {
        private float volume;
        private bool muted;

        public FakeSession(string name, bool isSystemSound = false, float volume = 1f, bool muted = false, string id = null, string executablePath = null, int processId = 0)
        {
            ProcessId = processId;
            Id = id ?? Guid.NewGuid().ToString("N");
            Name = name;
            ExecutablePath = executablePath;
            IsSystemSound = isSystemSound;
            this.volume = volume;
            this.muted = muted;
        }

        public string Id { get; }
        public string Name { get; }
        public string ExecutablePath { get; }
        public bool IsSystemSound { get; }
        public int ProcessId { get; }
        public bool ThrowOnWrite { get; set; }
        public int VolumeWrites { get; private set; }
        public int MuteWrites { get; private set; }
        public int Writes => VolumeWrites + MuteWrites;
        public int DisposeCount { get; private set; }

        public float Volume
        {
            get => volume;
            set
            {
                if (ThrowOnWrite)
                    throw new InvalidOperationException("session gone");
                volume = value;
                VolumeWrites++;
            }
        }

        public bool Muted
        {
            get => muted;
            set
            {
                if (ThrowOnWrite)
                    throw new InvalidOperationException("session gone");
                muted = value;
                MuteWrites++;
            }
        }

        /// <summary>Simulates the app changing its own volume.</summary>
        public void ChangeOwnVolume(float newVolume) => volume = newVolume;

        public void Dispose() => DisposeCount++;
    }

    sealed class FakeAudioBackend : IAudioBackend
    {
        private readonly object lockObj = new object();
        private List<FakeSession> sessions = new List<FakeSession>();
        private float volume = 1f;
        private bool muted;

        public event EventHandler MasterVolumeChanged;
        public event EventHandler SessionsChanged;
        public event EventHandler ReattachRequired;

        public bool IsAttached { get; private set; }
        public int AttachCount { get; private set; }
        public int GetMasterVolumeCount { get; private set; }
        public int GetSessionsCount { get; private set; }
        public int InvalidateCount { get; private set; }
        public Exception AttachException { get; set; }
        /// <summary>Thrown by the next GetSessions calls while set (e.g. audio service restarted).</summary>
        public Exception SessionsException { get; set; }
        public bool Disposed { get; private set; }
        public HashSet<int> CallingThreadIds { get; } = new HashSet<int>();

        /// <summary>When set, GetMasterVolume blocks until the gate is opened.</summary>
        public ManualResetEventSlim GetMasterVolumeGate { get; set; }
        public ManualResetEventSlim GetMasterVolumeEntered { get; } = new ManualResetEventSlim();

        public bool HasSubscribers => MasterVolumeChanged != null || SessionsChanged != null || ReattachRequired != null;

        public void SetMaster(float newVolume, bool newMuted)
        {
            lock (lockObj)
            {
                volume = newVolume;
                muted = newMuted;
            }
        }

        public void SetSessions(params FakeSession[] newSessions)
        {
            lock (lockObj)
            {
                sessions = newSessions.ToList();
            }
        }

        public void RaiseMasterVolumeChanged() => MasterVolumeChanged?.Invoke(this, EventArgs.Empty);
        public void RaiseSessionsChanged() => SessionsChanged?.Invoke(this, EventArgs.Empty);
        public void RaiseReattachRequired() => ReattachRequired?.Invoke(this, EventArgs.Empty);

        public void Attach()
        {
            Record();
            AttachCount++;
            if (AttachException != null)
            {
                IsAttached = false;
                throw AttachException;
            }
            IsAttached = true;
        }

        public (float Volume, bool Muted) GetMasterVolume()
        {
            Record();
            GetMasterVolumeCount++;
            GetMasterVolumeEntered.Set();
            GetMasterVolumeGate?.Wait(TimeSpan.FromSeconds(10));
            lock (lockObj)
            {
                return (volume, muted);
            }
        }

        public void InvalidateSessions()
        {
            Record();
            InvalidateCount++;
        }

        /// <summary>Runs inside every GetSessions call, e.g. to raise notifications like a real session would.</summary>
        public Action OnGetSessions { get; set; }

        public IReadOnlyList<IAudioSession> GetSessions()
        {
            Record();
            GetSessionsCount++;
            OnGetSessions?.Invoke();
            if (SessionsException != null)
                throw SessionsException;
            lock (lockObj)
            {
                return sessions.Cast<IAudioSession>().ToList();
            }
        }

        public void Dispose()
        {
            Record();
            Disposed = true;
            IsAttached = false;
        }

        private void Record()
        {
            lock (CallingThreadIds)
            {
                CallingThreadIds.Add(Thread.CurrentThread.ManagedThreadId);
            }
        }
    }

    static class Sta
    {
        /// <summary>Runs WinForms code on a dedicated STA thread and rethrows its exceptions.</summary>
        public static void Run(Action action)
        {
            ExceptionDispatchInfo failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    failure = ExceptionDispatchInfo.Capture(ex);
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!thread.Join(TimeSpan.FromSeconds(60)))
                throw new TimeoutException("STA test did not finish.");
            failure?.Throw();
        }

        /// <summary>Pumps the message queue until the condition holds.</summary>
        public static bool PumpUntil(Func<bool> condition, int timeoutMs = 5000)
        {
            var watch = Stopwatch.StartNew();
            while (!condition())
            {
                if (watch.ElapsedMilliseconds > timeoutMs)
                    return false;
                Application.DoEvents();
                Thread.Sleep(5);
            }
            return true;
        }
    }
}
