using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AutoVolumeControl
{
    /// <summary>Colors of the tray menu (Material light theme: indigo primary, pink accent).</summary>
    static class MenuTheme
    {
        public static readonly Color Background = Color.White;
        public static readonly Color Border = Color.FromArgb(204, 204, 204);
        public static readonly Color Divider = Color.FromArgb(226, 226, 226);
        public static readonly Color Text = Color.FromArgb(33, 33, 33);
        public static readonly Color TextDisabled = Color.FromArgb(158, 158, 158);
        public static readonly Color Primary = Color.FromArgb(0x3F, 0x51, 0xB5);
        public static readonly Color PrimaryHover = Color.FromArgb(0x5C, 0x6B, 0xC0);
        public static readonly Color PrimaryLight = Color.FromArgb(178, 185, 225);
        public static readonly Color Accent = Color.FromArgb(0xFF, 0x40, 0x81);
        public static readonly Color CheckboxOff = Color.FromArgb(117, 117, 117);
        public static readonly Color ControlDisabled = Color.FromArgb(189, 189, 189);
        public static readonly Color TrackDisabled = Color.FromArgb(224, 224, 224);

        public const string FontFamily = "Segoe UI";

        /// <summary>A font whose size is given in pixels at 96 DPI and 100 %, multiplied by the scale.</summary>
        public static Font CreateFont(float logicalPixels, float scale, FontStyle style = FontStyle.Regular)
        {
            return new Font(FontFamily, Math.Max(1f, logicalPixels * scale), style, GraphicsUnit.Pixel);
        }

        /// <summary>Converts a size at 96 DPI and 100 % to device pixels.</summary>
        public static int Scale(int logicalPixels, float scale) => (int)Math.Round(logicalPixels * scale);

        public static GraphicsPath RoundedRectangle(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            float diameter = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
            if (diameter <= 0)
            {
                path.AddRectangle(rect);
                return path;
            }
            path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        public const TextFormatFlags SingleLine = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
    }

    /// <summary>Draws the menu background, border and separators in the theme's colors.</summary>
    sealed class MenuRenderer : ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(e.ToolStrip.BackColor);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(MenuTheme.Border);
            var bounds = new Rectangle(Point.Empty, e.ToolStrip.Size);
            e.Graphics.DrawRectangle(pen, 0, 0, bounds.Width - 1, bounds.Height - 1);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var bounds = new Rectangle(Point.Empty, e.Item.Size);
            using (var background = new SolidBrush(e.ToolStrip.BackColor))
                e.Graphics.FillRectangle(background, bounds);
            using var pen = new Pen(MenuTheme.Divider);
            int y = bounds.Height / 2;
            e.Graphics.DrawLine(pen, 0, y, bounds.Width, y);
        }
    }
}
