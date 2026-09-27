using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace AutoVolumeControl
{
    /// <summary>One audio session of a running app on the playback device.</summary>
    interface IAudioSession : IDisposable
    {
        string Name { get; }
        bool IsSystemSound { get; }
        float Volume { get; set; }
        bool Muted { get; set; }
    }

    static class VolumeSynchronizer
    {
        /// <summary>Differences below this are rounding noise of the audio API and not written.</summary>
        public const float VolumeTolerance = 0.001f;

        /// <summary>
        /// Brings volume and mute of every enabled app session to the given master values.
        /// Values that already match are not written again, so repeated syncs cost nothing and do not
        /// flood other apps (e.g. the Windows mixer) with change notifications.
        /// A failing session is skipped so the others are still synced.
        /// </summary>
        /// <returns>The number of sessions that were changed.</returns>
        public static int Sync(float masterVolume, bool muted, IEnumerable<IAudioSession> sessions, AppPreferences preferences)
        {
            int changed = 0;
            foreach (var session in sessions)
            {
                try
                {
                    if (session.IsSystemSound || string.IsNullOrEmpty(session.Name))
                        continue;

                    if (!preferences.IsEnabled(session.Name))
                        continue;

                    bool sessionChanged = false;
                    if (Math.Abs(session.Volume - masterVolume) > VolumeTolerance)
                    {
                        session.Volume = masterVolume;
                        sessionChanged = true;
                    }
                    if (session.Muted != muted)
                    {
                        session.Muted = muted;
                        sessionChanged = true;
                    }
                    if (sessionChanged)
                        changed++;
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"Failed to sync session '{session.Name}': {ex.Message}");
                }
            }
            return changed;
        }
    }
}
