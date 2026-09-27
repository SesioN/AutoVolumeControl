using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AutoVolumeControl
{
    /// <summary>
    /// A Material style slider (track, round thumb, halo on hover and while dragged) that scales with the menu.
    /// Optionally shows the value on the right, or tick marks with a label per value below the track.
    /// </summary>
    class MenuSlider : Control
    {
        // Sizes at 96 DPI and 100 %.
        private const int ThumbSize = 18;
        private const int HaloSize = 36;
        private const int ActiveTrack = 6;
        private const int InactiveTrack = 4;
        private const int TickSize = 4;
        private const int ValueGap = 6;

        private float scale = 1f;
        private int minimum;
        private int maximum = 100;
        private int value;
        private bool hovered;
        private bool dragging;
        private string valueSuffix = string.Empty;
        private bool showValue;
        private string[] tickLabels;

        public MenuSlider()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            Margin = Padding.Empty;
            Cursor = Cursors.Hand;
            Height = PreferredHeight;
        }

        /// <summary>
        /// The user moved the slider (for every step while dragging, see <see cref="Dragging"/>) or turned the mouse
        /// wheel over it. Not raised when <see cref="Value"/> is set in code.
        /// </summary>
        public event EventHandler<int> ValueChangedByUser;

        /// <summary>Display scaling times the user's menu size, e.g. 1.5 at 150 %.</summary>
        public float ScaleFactor
        {
            get => scale;
            set
            {
                scale = value;
                Height = PreferredHeight;
                Invalidate();
            }
        }

        public int Minimum
        {
            get => minimum;
            set
            {
                minimum = value;
                maximum = Math.Max(maximum, value);
                this.value = Clamp(this.value);
                Invalidate();
            }
        }

        public int Maximum
        {
            get => maximum;
            set
            {
                maximum = value;
                minimum = Math.Min(minimum, value);
                this.value = Clamp(this.value);
                Invalidate();
            }
        }

        /// <summary>Setting it does not raise <see cref="ValueChangedByUser"/>.</summary>
        public int Value
        {
            get => value;
            set
            {
                this.value = Clamp(value);
                Invalidate();
            }
        }

        /// <summary>Shows the value and this suffix (e.g. "%") on the right.</summary>
        public bool ShowValue
        {
            get => showValue;
            set
            {
                showValue = value;
                Invalidate();
            }
        }

        public string ValueSuffix
        {
            get => valueSuffix;
            set
            {
                valueSuffix = value ?? string.Empty;
                Invalidate();
            }
        }

        /// <summary>One label per value from <see cref="Minimum"/> to <see cref="Maximum"/>, shown below a tick mark each.</summary>
        public string[] TickLabels
        {
            get => tickLabels;
            set
            {
                tickLabels = value;
                Height = PreferredHeight;
                Invalidate();
            }
        }

        /// <summary>How far one mouse wheel notch moves the value.</summary>
        public int WheelStep { get; set; } = 1;

        /// <summary>True from pressing the left button on the slider until it is released or the capture is lost.</summary>
        public bool Dragging => dragging;

        private int S(int logicalPixels) => MenuTheme.Scale(logicalPixels, scale);

        private int SliderHeight => S(HaloSize);

        private int PreferredHeight => SliderHeight + (tickLabels == null ? 0 : TextRenderer.MeasureText("0", Font, Size.Empty, MenuTheme.SingleLine).Height);

        public override Size GetPreferredSize(Size proposedSize) => new Size(Width, PreferredHeight);

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            Height = PreferredHeight;
        }

        private int Clamp(int v) => Math.Max(minimum, Math.Min(maximum, v));

        /// <summary>The horizontal range the thumb center moves in.</summary>
        private (float Left, float Right) Track()
        {
            float left = S(HaloSize) / 2f;
            float right = Width - S(HaloSize) / 2f;
            if (showValue)
                right -= S(ValueGap) + TextRenderer.MeasureText(maximum + valueSuffix, Font, Size.Empty, MenuTheme.SingleLine).Width;
            if (tickLabels != null && tickLabels.Length > 0)
            {
                float half = Math.Max(
                    TextRenderer.MeasureText(tickLabels[0], Font, Size.Empty, MenuTheme.SingleLine).Width,
                    TextRenderer.MeasureText(tickLabels[tickLabels.Length - 1], Font, Size.Empty, MenuTheme.SingleLine).Width) / 2f;
                left = Math.Max(left, half);
                right = Math.Min(right, Width - half);
            }
            return (left, Math.Max(left + 1, right));
        }

        private float PositionOf(int v)
        {
            var (left, right) = Track();
            return maximum == minimum ? left : left + (v - minimum) * (right - left) / (maximum - minimum);
        }

        private int ValueAt(int x)
        {
            var (left, right) = Track();
            double share = Math.Max(0, Math.Min(1, (x - left) / (right - left)));
            return Clamp(minimum + (int)Math.Round(share * (maximum - minimum), MidpointRounding.AwayFromZero));
        }

        /// <summary>Changes the value like the user does: raises <see cref="ValueChangedByUser"/> if it changed.</summary>
        internal void ChangeValue(int newValue)
        {
            newValue = Clamp(newValue);
            if (newValue == value)
                return;

            Value = newValue;
            ValueChangedByUser?.Invoke(this, newValue);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            bool start = e.Button == MouseButtons.Left && Enabled;
            if (start)
            {
                dragging = true;
                Capture = true;
            }
            // MouseDown first, so handlers know about the drag before the first value arrives.
            base.OnMouseDown(e);
            if (start)
            {
                ChangeValue(ValueAt(e.X));
                Invalidate();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging)
                ChangeValue(ValueAt(e.X));
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && dragging)
            {
                dragging = false;
                Capture = false;
                Invalidate();
            }
            base.OnMouseUp(e);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            if (!Capture && dragging)
            {
                dragging = false;
                Invalidate();
            }
            base.OnMouseCaptureChanged(e);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            hovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hovered = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        /// <summary>Wheel up raises the value.</summary>
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (e is HandledMouseEventArgs handled)
                handled.Handled = true;
            if (Enabled && e.Delta != 0)
                ChangeValue(value + Math.Sign(e.Delta) * WheelStep);
            base.OnMouseWheel(e);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var (left, right) = Track();
            float centerY = SliderHeight / 2f;
            float thumbX = PositionOf(value);
            var color = Enabled ? MenuTheme.Primary : MenuTheme.TextDisabled;

            float inactive = Math.Max(2f, InactiveTrack * scale);
            using (var brush = new SolidBrush(Enabled ? MenuTheme.PrimaryLight : MenuTheme.TrackDisabled))
            using (var path = MenuTheme.RoundedRectangle(new RectangleF(left, centerY - inactive / 2, right - left, inactive), inactive / 2))
                g.FillPath(brush, path);

            float active = Math.Max(2f, ActiveTrack * scale);
            using (var brush = new SolidBrush(color))
            using (var path = MenuTheme.RoundedRectangle(new RectangleF(left, centerY - active / 2, Math.Max(active, thumbX - left), active), active / 2))
                g.FillPath(brush, path);

            if (tickLabels != null)
                PaintTicks(g, centerY);

            if (Enabled && (hovered || dragging))
            {
                float halo = S(HaloSize);
                using var haloBrush = new SolidBrush(Color.FromArgb(dragging ? 77 : 38, MenuTheme.Primary));
                g.FillEllipse(haloBrush, thumbX - halo / 2, centerY - halo / 2, halo, halo);
            }

            float thumb = S(ThumbSize);
            using (var brush = new SolidBrush(color))
                g.FillEllipse(brush, thumbX - thumb / 2, centerY - thumb / 2, thumb, thumb);

            if (showValue)
            {
                var valueRect = new Rectangle((int)Math.Ceiling(right + S(HaloSize) / 2f), 0, Width - (int)Math.Ceiling(right + S(HaloSize) / 2f), SliderHeight);
                TextRenderer.DrawText(g, value + valueSuffix, Font, valueRect, Enabled ? MenuTheme.Text : MenuTheme.TextDisabled, BackColor,
                    MenuTheme.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
            }
        }

        private void PaintTicks(Graphics g, float centerY)
        {
            float tick = Math.Max(2f, TickSize * scale);
            for (int v = minimum; v <= maximum; v++)
            {
                float x = PositionOf(v);
                var tickColor = !Enabled ? MenuTheme.ControlDisabled : v <= value ? Color.White : MenuTheme.Primary;
                using (var brush = new SolidBrush(tickColor))
                    g.FillEllipse(brush, x - tick / 2, centerY - tick / 2, tick, tick);

                int index = v - minimum;
                if (index >= tickLabels.Length)
                    continue;

                var label = tickLabels[index];
                var size = TextRenderer.MeasureText(label, Font, Size.Empty, MenuTheme.SingleLine);
                var labelRect = new Rectangle((int)Math.Round(x - size.Width / 2f), SliderHeight, size.Width, size.Height);
                TextRenderer.DrawText(g, label, Font, labelRect, v == value && Enabled ? MenuTheme.Text : MenuTheme.TextDisabled, BackColor, MenuTheme.SingleLine);
            }
        }
    }
}
