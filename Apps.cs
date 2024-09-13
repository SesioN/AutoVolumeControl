using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using CSCore.CoreAudioAPI;

namespace AutoVolumeControl
{
    class Apps
    {
        private List<string> apps;
        private MMDevice defaultPlaybackDevice;

        public Apps()
        {
            this.apps = new List<string>();
            InitializeDefaultDevice();
        }

        public List<string> GetApps()
        {
            Refresh();
            return this.apps;
        }

        public void Refresh()
        {
            this.apps.Clear();

            if (this.defaultPlaybackDevice != null)
            {
                var sessionApps = new List<string>();

                var task = Task.Factory.StartNew(() =>
                {
                    try
                    {
                        using (var sessionManager = AudioSessionManager2.FromMMDevice(defaultPlaybackDevice))
                        {
                            var sessionEnumerator = sessionManager.GetSessionEnumerator();

                            foreach (var session in sessionEnumerator)
                            {
                                using (var audioSessionControl = session.QueryInterface<AudioSessionControl2>())
                                {
                                    // Skip system sound sessions
                                    if (audioSessionControl.IsSystemSoundSession)
                                    {
                                        continue;
                                    }

                                    string name = GetSessionDisplayName(audioSessionControl);

                                    if (!string.IsNullOrEmpty(name) && !sessionApps.Contains(name))
                                    {
                                        sessionApps.Add(name);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error in Refresh: {ex.Message}");
                    }
                });

                task.Wait();

                this.apps.AddRange(sessionApps);
            }
        }

        private void InitializeDefaultDevice()
        {
            using (var enumerator = new MMDeviceEnumerator())
            {
                defaultPlaybackDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
        }

        private string GetSessionDisplayName(AudioSessionControl2 audioSession)
        {
            string name = null;

            try
            {
                // Attempt to get the process associated with the audio session
                using (var process = audioSession.Process)
                {
                    if (process != null)
                    {
                        name = process.ProcessName;
                    }
                }
            }
            catch
            {
                // Process might have exited or be unavailable
            }

            // If process name is not available, use DisplayName
            if (string.IsNullOrEmpty(name))
            {
                name = audioSession.DisplayName;
            }

            // If still not available, use the SessionIdentifier or SessionInstanceIdentifier as a last resort
            if (string.IsNullOrEmpty(name))
            {
                name = audioSession.SessionIdentifier;
            }

            // Clean up the name if necessary
            if (!string.IsNullOrEmpty(name))
            {
                name = name.Trim();
            }

            return name;
        }
    }
}