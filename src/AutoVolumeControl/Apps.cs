using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CSCore.CoreAudioAPI;

namespace AutoVolumeControl
{
    class Apps : IDisposable
    {
        private readonly List<string> apps;
        private MMDevice defaultPlaybackDevice;

        private readonly object lockObj = new object();

        public event EventHandler AppsUpdated;

        private readonly AppSettings appSettings;

        public Apps()
        {
            apps = new List<string>();
            appSettings = new AppSettings();
            InitializeDefaultDevice();
        }

        public List<string> GetApps()
        {
            lock (lockObj)
            {
                return new List<string>(apps);
            }
        }

        public void AddRange(IEnumerable<string> newApps)
        {
            lock (lockObj)
            {
                apps.AddRange(newApps);
            }
        }

        public void Refresh()
        {
            List<string> sessionApps = new List<string>();

            var task = Task.Run(() =>
            {
                try
                {
                    InitializeCom();
                    var sessions = GetActiveSessions();
                    sessionApps.AddRange(sessions);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error in Refresh Task: {ex.Message}\n{ex.StackTrace}");
                }
                finally
                {
                    ComHelper.CoUninitialize();
                }
            });

            try
            {
                task.Wait();
            }
            catch (AggregateException aggEx)
            {
                foreach (var ex in aggEx.InnerExceptions)
                {
                    Console.WriteLine($"Exception in task: {ex.Message}\n{ex.StackTrace}");
                }
            }

            bool isUpdated = false;

            lock (lockObj)
            {
                foreach (var app in sessionApps)
                {
                    if (!appSettings.Exists(app))
                    {
                        appSettings.Set(app, "True");                      
                    }
                    if (!apps.Contains(app))
                    {
                        apps.Add(app);
                        isUpdated = true;
                    }
                }

                int originalCount = apps.Count;
                apps.RemoveAll(a => !sessionApps.Contains(a));
                if (apps.Count != originalCount)
                {
                    isUpdated = true;
                }
            }

            if (isUpdated)
            {
                AppsUpdated?.Invoke(this, EventArgs.Empty);
            }
        }

        private void InitializeDefaultDevice()
        {
            Task.Run(() =>
            {
                try
                {
                    InitializeCom();
                    using var enumerator = new MMDeviceEnumerator();
                    defaultPlaybackDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error in InitializeDefaultDevice: {ex.Message}");
                }
                finally
                {
                    ComHelper.CoUninitialize();
                }
            }).Wait();
        }

        private void InitializeCom()
        {
            int hr = ComHelper.CoInitializeEx(IntPtr.Zero, ComHelper.COINIT.COINIT_MULTITHREADED);
            if (hr == (int)HResult.RPC_E_CHANGED_MODE)
            {
                Console.WriteLine("COM already initialized with a different threading model");
            }
            else if (hr < 0)
            {
                throw new COMException("Failed to initialize COM library", hr);
            }
        }

        private IEnumerable<string> GetActiveSessions()
        {
            var sessionNames = new List<string>();

            using var sessionManager = AudioSessionManager2.FromMMDevice(defaultPlaybackDevice);
            var sessionEnumerator = sessionManager.GetSessionEnumerator();

            foreach (var session in sessionEnumerator)
            {
                using var audioSessionControl = session.QueryInterface<AudioSessionControl2>();
                if (audioSessionControl.IsSystemSoundSession)
                    continue;

                string name = GetSessionDisplayName(audioSessionControl);
                if (!string.IsNullOrEmpty(name) && !sessionNames.Contains(name))
                {
                    sessionNames.Add(name);
                    Console.WriteLine($"Found audio session: {name}");
                }
            }

            Console.WriteLine($"Total sessions found: {sessionNames.Count}");
            return sessionNames;
        }

        private string GetSessionDisplayName(AudioSessionControl2 audioSession)
        {
            string name = null;

            name = GetProcessName(audioSession.Process);
            if (!string.IsNullOrEmpty(name))
                return name.Trim();

            name = audioSession.DisplayName;
            if (!string.IsNullOrEmpty(name))
                return name.Trim();

            name = audioSession.SessionIdentifier;
            return name?.Trim();
        }

        private string GetProcessName(Process process)
        {
            try
            {
                return process?.ProcessName;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting process name: {ex.Message}");
                return null;
            }
        }

        public void Dispose()
        {
            defaultPlaybackDevice?.Dispose();
        }

        public enum HResult : int
        {
            S_OK = 0,
            S_FALSE = 1,
            RPC_E_CHANGED_MODE = unchecked((int)0x80010106)
        }
    }
}
