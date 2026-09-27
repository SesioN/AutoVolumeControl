using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AutoVolumeControl
{
    /// <summary>A Material style checkbox (accent colored box, check mark, optional text) that scales with the menu.</summary>
    sealed class MenuCheckBox : CheckBox
    {
        // Sizes at 96 DPI and 100 %.
        private const int BoxSize = 18;
        private const int BoxPadding = 9;
        private const int TextGap = 4;
        private const int HaloSize = 32;

        private float scale = 1f;
        private bool hovered;

        public MenuCheckBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AutoSize = true;
            Margin = Padding.Empty;
            Cursor = Cursors.Hand;
        }

        /// <summary>Display scaling times the user's menu size, e.g. 1.5 at 150 %.</summary>
        public float ScaleFactor
        {
            get => scale;
            set
            {
                scale = value;
                if (AutoSize)
                    Size = GetPreferredSize(Size.Empty);
                Invalidate();
            }
        }

        private int S(int logicalPixels) => MenuTheme.Scale(logicalPixels, scale);

        public override Size GetPreferredSize(Size proposedSize)
        {
            int width = S(BoxPadding) * 2 + S(BoxSize);
            int height = width;
            if (!string.IsNullOrEmpty(Text))
            {
                var text = TextRenderer.MeasureText(Text, Font, Size.Empty, MenuTheme.SingleLine);
                width += S(TextGap) + text.Width + S(BoxPadding);
                height = System.Math.Max(height, text.Height);
            }
            return new Size(width, height);
        }

        protected override void OnTextChanged(System.EventArgs e)
        {
            base.OnTextChanged(e);
            if (AutoSize)
                Size = GetPreferredSize(Size.Empty);
        }

        protected override void OnFontChanged(System.EventArgs e)
        {
            base.OnFontChanged(e);
            if (AutoSize)
                Size = GetPreferredSize(Size.Empty);
        }

        protected override void OnMouseEnter(System.EventArgs eventargs)
        {
            base.OnMouseEnter(eventargs);
            hovered = true;
            Invalidate();
        }

        protected override void OnMouseLeave(System.EventArgs eventargs)
        {
            base.OnMouseLeave(eventargs);
            hovered = false;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            var g = pevent.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int box = S(BoxSize);
            float x = S(BoxPadding);
            float y = (Height - box) / 2f;
            var center = new PointF(x + box / 2f, y + box / 2f);

            if (hovered && Enabled)
            {
                float halo = S(HaloSize);
                using var haloBrush = new SolidBrush(Color.FromArgb(28, Checked ? MenuTheme.Accent : Color.Black));
                g.FillEllipse(haloBrush, center.X - halo / 2, center.Y - halo / 2, halo, halo);
            }

            float stroke = System.Math.Max(1f, 2f * scale);
            var boxRect = new RectangleF(x + stroke / 2, y + stroke / 2, box - stroke, box - stroke);
            using (var path = MenuTheme.RoundedRectangle(boxRect, 2f * scale))
            {
                if (Checked)
                {
                    var color = Enabled ? MenuTheme.Accent : MenuTheme.ControlDisabled;
                    using var brush = new SolidBrush(color);
                    using var border = new Pen(color, stroke);
                    g.FillPath(brush, path);
                    g.DrawPath(border, path);

                    using var check = new Pen(Color.White, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                    g.DrawLines(check, new[]
                    {
                        new PointF(x + box * 0.22f, y + box * 0.52f),
                        new PointF(x + box * 0.42f, y + box * 0.72f),
                        new PointF(x + box * 0.78f, y + box * 0.32f),
                    });
                }
                else
                {
                    using var border = new Pen(Enabled ? MenuTheme.CheckboxOff : MenuTheme.ControlDisabled, stroke);
                    g.DrawPath(border, path);
                }
            }

            if (!string.IsNullOrEmpty(Text))
            {
                int textX = S(BoxPadding) + box + S(TextGap);
                var textRect = new Rectangle(textX, 0, Width - textX, Height);
                TextRenderer.DrawText(g, Text, Font, textRect, Enabled ? MenuTheme.Text : MenuTheme.TextDisabled, BackColor,
                    MenuTheme.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            }
        }
    }
}
