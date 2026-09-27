using System;
using System.Globalization;
using System.Linq;

namespace AutoVolumeControl
{
    /// <summary>
    /// The size of the tray menu chosen by the user, on top of the display scaling: one of <see cref="Steps"/>,
    /// stored as HKCU\SOFTWARE\&lt;product name&gt;\Menu\Scale.
    /// </summary>
    sealed class MenuScale
    {
        /// <summary>Name of the sub-store, so the value cannot clash with the per-app flags.</summary>
        public const string StoreName = "Menu";
        public const string ValueName = "Scale";
        public const int DefaultPercent = 100;

        /// <summary>The sizes the menu offers, in percent, from small to large.</summary>
        public static readonly int[] Steps = { 85, 100, 115, 130 };

        private readonly ISettingsStore store;

        public MenuScale(ISettingsStore store)
        {
            this.store = store.GetSubStore(StoreName);
        }

        /// <summary>Raised on the calling thread after <see cref="Percent"/> changed.</summary>
        public event EventHandler Changed;

        /// <summary>The stored size; a hand-edited or corrupted value is tolerated like the per-app settings.</summary>
        public int Percent
        {
            get
            {
                var value = store.Get(ValueName);
                if (value == null || !int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int percent))
                    return DefaultPercent;
                return Nearest(percent);
            }
        }

        /// <summary><see cref="Percent"/> as a factor, e.g. 1.15.</summary>
        public float Factor => Percent / 100f;

        /// <summary>Stores the step nearest to the value. Writes and raises <see cref="Changed"/> only on a change.</summary>
        public void SetPercent(int percent)
        {
            percent = Nearest(percent);
            if (percent == Percent)
                return;

            store.Set(ValueName, percent.ToString(CultureInfo.InvariantCulture));
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>The step closest to the value; the smaller one on a tie.</summary>
        public static int Nearest(int percent) => Steps.OrderBy(s => Math.Abs(s - percent)).ThenBy(s => s).First();

        /// <summary>The position of the step closest to the value in <see cref="Steps"/>.</summary>
        public static int IndexOf(int percent) => Array.IndexOf(Steps, Nearest(percent));
    }
}
