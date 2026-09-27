using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AutoVolumeControl
{
    /// <summary>
    /// The small icons of the apps in the menu, one bitmap per app name, in <see cref="SystemInformation.SmallIconSize"/>
    /// (e.g. 24x24 at 150 %) so they are as sharp as the tray icon. Apps without a known executable, or whose icon
    /// cannot be read, get the generic Windows application icon. UI thread only.
    /// </summary>
    sealed class AppIconCache : IDisposable
    {
        private readonly Func<string, Size, Bitmap> extract;
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
        private Bitmap fallback;
        private bool disposed;

        public AppIconCache() : this(null, null)
        {
        }

        /// <param name="extract">Reads the icon of an executable; null for the Windows shell. May return null or throw.</param>
        /// <param name="size">The icon size; null for <see cref="SystemInformation.SmallIconSize"/>.</param>
        internal AppIconCache(Func<string, Size, Bitmap> extract, Size? size)
        {
            this.extract = extract ?? ExtractIcon;
            IconSize = size ?? SystemInformation.SmallIconSize;
        }

        public Size IconSize { get; }

        /// <summary>
        /// The icon of the app, or the generic icon. The bitmap belongs to the cache: callers must not dispose it,
        /// and it stays valid until <see cref="Retain"/> drops the app or the cache is disposed.
        /// </summary>
        public Image GetIcon(AppInfo app)
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(AppIconCache));

            var name = app?.Name ?? string.Empty;
            var path = app?.ExecutablePath;
            if (entries.TryGetValue(name, out var entry) && (entry.Path == path || path == null))
                return entry.Bitmap ?? Fallback();

            var bitmap = TryExtract(path);
            if (entry != null)
                entry.Bitmap?.Dispose();
            entries[name] = new Entry(path, bitmap);
            return bitmap ?? Fallback();
        }

        /// <summary>Releases the icons of apps that are no longer shown.</summary>
        public void Retain(IEnumerable<string> appNames)
        {
            var keep = new HashSet<string>(appNames.Where(n => n != null));
            foreach (var name in entries.Keys.Where(n => !keep.Contains(n)).ToList())
            {
                entries[name].Bitmap?.Dispose();
                entries.Remove(name);
            }
        }

        internal int CachedCount => entries.Count;

        private Bitmap TryExtract(string path)
        {
            if (path == null)
                return null;

            try
            {
                return extract(path, IconSize);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Reading the icon of '{path}' failed: {ex.Message}");
                return null;
            }
        }

        private Bitmap Fallback()
        {
            if (fallback == null)
            {
                using var icon = new Icon(SystemIcons.Application, IconSize);
                fallback = ToBitmap(icon, IconSize);
            }
            return fallback;
        }

        /// <summary>The shell's small icon of the file, as shown in Explorer, else the icon Windows associates with it.</summary>
        private static Bitmap ExtractIcon(string path, Size size)
        {
            var info = new ShFileInfo();
            var result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiIcon | ShgfiSmallIcon);
            if (result != IntPtr.Zero && info.IconHandle != IntPtr.Zero)
            {
                try
                {
                    using var icon = Icon.FromHandle(info.IconHandle);
                    return ToBitmap(icon, size);
                }
                finally
                {
                    DestroyIcon(info.IconHandle);
                }
            }

            using var associated = Icon.ExtractAssociatedIcon(path);
            return associated == null ? null : ToBitmap(associated, size);
        }

        /// <summary>
        /// A new bitmap of exactly the given size. <see cref="Icon.ToBitmap"/> keeps the alpha channel (drawing the icon
        /// with GDI onto an ARGB bitmap does not reliably); an image of another size is scaled from that bitmap.
        /// </summary>
        private static Bitmap ToBitmap(Icon icon, Size size)
        {
            var bitmap = icon.ToBitmap();
            if (bitmap.Size == size)
                return bitmap;

            try
            {
                var scaled = new Bitmap(size.Width, size.Height);
                try
                {
                    using var graphics = Graphics.FromImage(scaled);
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.DrawImage(bitmap, new Rectangle(Point.Empty, size));
                    return scaled;
                }
                catch
                {
                    scaled.Dispose();
                    throw;
                }
            }
            finally
            {
                bitmap.Dispose();
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            foreach (var entry in entries.Values)
                entry.Bitmap?.Dispose();
            entries.Clear();
            fallback?.Dispose();
            fallback = null;
        }

        private sealed class Entry
        {
            public Entry(string path, Bitmap bitmap)
            {
                Path = path;
                Bitmap = bitmap;
            }

            public string Path { get; }

            /// <summary>null when the icon could not be read; the generic icon is used then.</summary>
            public Bitmap Bitmap { get; }
        }

        private const uint ShgfiIcon = 0x100;
        private const uint ShgfiSmallIcon = 0x1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ShFileInfo
        {
            public IntPtr IconHandle;
            public int IconIndex;
            public uint Attributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string DisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string TypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string path, uint fileAttributes, ref ShFileInfo info, uint infoSize, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr icon);
    }
}
