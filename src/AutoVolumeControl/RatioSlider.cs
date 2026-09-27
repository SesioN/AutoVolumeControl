namespace AutoVolumeControl
{
    /// <summary>The 0–100 % slider of an app row; the mouse wheel moves it by <see cref="WheelStepPercent"/>.</summary>
    sealed class RatioSlider : MenuSlider
    {
        /// <summary>Percent per wheel notch.</summary>
        public const int WheelStepPercent = 5;

        public RatioSlider()
        {
            Minimum = 0;
            Maximum = 100;
            ShowValue = true;
            ValueSuffix = "%";
            WheelStep = WheelStepPercent;
        }
    }
}
