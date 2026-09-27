using System;
using System.Windows.Forms;
using MaterialSkin.Controls;

namespace AutoVolumeControl
{
    /// <summary>
    /// The 0–100 % slider of an app row. A <see cref="MaterialSlider"/> whose mouse wheel moves the value in the
    /// expected direction: MaterialSlider itself lowers the value when the wheel is turned up.
    /// </summary>
    sealed class RatioSlider : MaterialSlider
    {
        /// <summary>Percent per wheel notch.</summary>
        public const int WheelStep = 5;

        public RatioSlider()
        {
            ShowText = false;
            Text = string.Empty;
            ShowValue = true;
            ValueSuffix = "%";
            RangeMin = 0;
            RangeMax = 100;
            onValueChanged += (sender, value) => RatioChanged?.Invoke(this, value);
        }

        /// <summary>
        /// The user moved the slider (for every step while dragging) or turned the mouse wheel over it.
        /// Not raised when <see cref="MaterialSlider.Value"/> is set in code.
        /// </summary>
        public event EventHandler<int> RatioChanged;

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            // The base implementation is skipped on purpose; it moves the value the wrong way.
            if (e is HandledMouseEventArgs handled)
                handled.Handled = true;
            if (!Enabled || e.Delta == 0)
                return;

            int value = Math.Max(RangeMin, Math.Min(RangeMax, Value + Math.Sign(e.Delta) * WheelStep));
            if (value == Value)
                return;

            Value = value;
            RatioChanged?.Invoke(this, value);
        }
    }
}
