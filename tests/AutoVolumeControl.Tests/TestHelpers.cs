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
    }

    sealed class FakeSession : IAudioSession
    {
        public FakeSession(string name, bool isSystemSound = false)
        {
            Name = name;
            IsSystemSound = isSystemSound;
        }

        public string Name { get; }
        public bool IsSystemSound { get; }
        public bool ThrowOnApply { get; set; }
        public float? Volume { get; private set; }
        public bool? Muted { get; private set; }
        public int ApplyCount { get; private set; }
        public int DisposeCount { get; private set; }

        public void Apply(float volume, bool muted)
        {
            if (ThrowOnApply)
                throw new InvalidOperationException("session gone");

            Volume = volume;
            Muted = muted;
            ApplyCount++;
        }

        public void Dispose() => DisposeCount++;
    }

    sealed class FakeAudioBackend : IAudioBackend
    {
        private readonly object lockObj = new object();
        private List<FakeSession> sessions = new List<FakeSession>();
        private float volume = 1f;
        private bool muted;

        public event EventHandler MasterVolumeChanged;
        public event EventHandler SessionCreated;
        public event EventHandler DefaultDeviceChanged;

        public bool IsAttached { get; private set; }
        public int AttachCount { get; private set; }
        public int GetMasterVolumeCount { get; private set; }
        public Exception AttachException { get; set; }
        public bool Disposed { get; private set; }
        public HashSet<int> CallingThreadIds { get; } = new HashSet<int>();
        public int? DisposeThreadId { get; private set; }

        /// <summary>When set, GetMasterVolume blocks until the gate is opened.</summary>
        public ManualResetEventSlim GetMasterVolumeGate { get; set; }
        public ManualResetEventSlim GetMasterVolumeEntered { get; } = new ManualResetEventSlim();

        public bool HasSubscribers => MasterVolumeChanged != null || SessionCreated != null || DefaultDeviceChanged != null;

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
        public void RaiseSessionCreated() => SessionCreated?.Invoke(this, EventArgs.Empty);
        public void RaiseDefaultDeviceChanged() => DefaultDeviceChanged?.Invoke(this, EventArgs.Empty);

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

        public IReadOnlyList<IAudioSession> GetSessions()
        {
            Record();
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
            DisposeThreadId = Thread.CurrentThread.ManagedThreadId;
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
