using System;
using System.Drawing;

namespace AutoVolumeControl
{
    /// <summary>The fonts of the menu items in one scale. Disposed after the items using them.</summary>
    sealed class MenuFonts : IDisposable
    {
        // Sizes in pixels at 96 DPI and 100 %.
        private const float HeaderSize = 18;
        private const float BodySize = 15;
        private const float SmallSize = 13;
        private const float ButtonSize = 14;
        private const float ScaleIconSmallSize = 12;
        private const float ScaleIconLargeSize = 20;

        public MenuFonts(float scale)
        {
            Header = MenuTheme.CreateFont(HeaderSize, scale, FontStyle.Bold);
            Body = MenuTheme.CreateFont(BodySize, scale);
            Small = MenuTheme.CreateFont(SmallSize, scale);
            Button = MenuTheme.CreateFont(ButtonSize, scale, FontStyle.Bold);
            ScaleIconSmall = MenuTheme.CreateFont(ScaleIconSmallSize, scale);
            ScaleIconLarge = MenuTheme.CreateFont(ScaleIconLargeSize, scale);
        }

        public Font Header { get; }
        public Font Body { get; }
        public Font Small { get; }
        public Font Button { get; }
        public Font ScaleIconSmall { get; }
        public Font ScaleIconLarge { get; }

        public void Dispose()
        {
            Header.Dispose();
            Body.Dispose();
            Small.Dispose();
            Button.Dispose();
            ScaleIconSmall.Dispose();
            ScaleIconLarge.Dispose();
        }
    }
}
