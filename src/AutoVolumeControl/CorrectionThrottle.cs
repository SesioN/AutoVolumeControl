using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace AutoVolumeControl
{
    /// <summary>
    /// Stops correcting a session that keeps resetting its own volume, so the two apps do not fight in an
    /// endless loop. Only repeated corrections towards the same target count: when the master volume changes,
    /// the target changes, the count starts over and any pause ends, so the master is always followed.
    /// State is kept per session, so many sessions of one app do not add up.
    /// Not thread safe; used on the audio thread only.
    /// </summary>
    sealed class CorrectionThrottle
    {
        public const int DefaultMaxRepeatedCorrections = 10;
        public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(10);
        public static readonly TimeSpan DefaultPause = TimeSpan.FromSeconds(60);

        private readonly Func<DateTime> clock;
        private readonly int maxRepeatedCorrections;
        private readonly TimeSpan window;
        private readonly TimeSpan pause;
        private readonly Dictionary<string, State> states = new Dictionary<string, State>();

        public CorrectionThrottle(Func<DateTime> clock = null, int? maxRepeatedCorrections = null, TimeSpan? window = null, TimeSpan? pause = null)
        {
            this.clock = clock ?? (() => DateTime.UtcNow);
            this.maxRepeatedCorrections = maxRepeatedCorrections ?? DefaultMaxRepeatedCorrections;
            this.window = window ?? DefaultWindow;
            this.pause = pause ?? DefaultPause;
        }

        /// <summary>Called before correcting a session; false while its corrections are paused.</summary>
        public bool Allow(string session, float targetVolume, bool targetMuted)
        {
            var now = clock();
            if (!states.TryGetValue(session, out var state))
            {
                state = new State();
                states.Add(session, state);
            }

            bool sameTarget = state.HasTarget
                && Math.Abs(state.Volume - targetVolume) <= VolumeSynchronizer.VolumeTolerance
                && state.Muted == targetMuted;
            if (!sameTarget)
            {
                // A new master value is always applied, even to a session that was paused.
                state.Corrections.Clear();
                state.PausedUntil = DateTime.MinValue;
                state.HasTarget = true;
                state.Volume = targetVolume;
                state.Muted = targetMuted;
            }

            if (now < state.PausedUntil)
                return false;

            while (state.Corrections.Count > 0 && now - state.Corrections.Peek() > window)
                state.Corrections.Dequeue();

            state.Corrections.Enqueue(now);
            if (state.Corrections.Count <= maxRepeatedCorrections)
                return true;

            Trace.WriteLine($"Session {session} keeps changing its own volume; not correcting it for {pause.TotalSeconds} s.");
            state.PausedUntil = now + pause;
            state.Corrections.Clear();
            return false;
        }

        /// <summary>Time until the earliest pause ends, or null if nothing is paused.</summary>
        public TimeSpan? TimeUntilNextResume()
        {
            var now = clock();
            var ends = states.Values.Where(s => s.PausedUntil > now).Select(s => s.PausedUntil).ToList();
            return ends.Count == 0 ? (TimeSpan?)null : ends.Min() - now;
        }

        /// <summary>Forgets sessions that no longer exist.</summary>
        public void Retain(IEnumerable<string> sessions)
        {
            var keep = new HashSet<string>(sessions);
            foreach (var gone in states.Keys.Where(k => !keep.Contains(k)).ToList())
                states.Remove(gone);
        }

        internal int TrackedSessionCount => states.Count;

        private sealed class State
        {
            public bool HasTarget;
            public float Volume;
            public bool Muted;
            public DateTime PausedUntil;
            public readonly Queue<DateTime> Corrections = new Queue<DateTime>();
        }
    }
}
