using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace AutoVolumeControl
{
    /// <summary>One audio session on the playback device.</summary>
    interface IAudioSession : IDisposable
    {
        string Name { get; }
        bool IsSystemSound { get; }
        void Apply(float volume, bool muted);
    }

    static class VolumeSynchronizer
    {
        /// <summary>
        /// Sets volume and mute of every enabled app session to the given master values.
        /// A failing session is skipped so the others are still synced.
        /// </summary>
        /// <returns>The number of sessions that were updated.</returns>
        public static int Sync(float masterVolume, bool muted, IEnumerable<IAudioSession> sessions, AppPreferences preferences)
        {
            int applied = 0;
            foreach (var session in sessions)
            {
                try
                {
                    if (session.IsSystemSound || string.IsNullOrEmpty(session.Name))
                        continue;

                    if (!preferences.IsEnabled(session.Name))
                        continue;

                    session.Apply(masterVolume, muted);
                    applied++;
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"Failed to sync session '{session.Name}': {ex.Message}");
                }
            }
            return applied;
        }
    }
}
