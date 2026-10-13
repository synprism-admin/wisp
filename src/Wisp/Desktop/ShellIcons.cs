using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Wisp.Desktop;

/// <summary>High-resolution icons for files, shortcuts and executables, from the Windows shell.</summary>
public static class ShellIcons
{
    static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, out DIBSECTION ds);

    [StructLayout(LayoutKind.Sequential)]
    struct DIBSECTION
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
        public int biSize, biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        public int dsBitfields0, dsBitfields1, dsBitfields2;
        public IntPtr dshSection;
        public int dsOffset;
    }

    const int SIIGBF_ICONONLY = 0x4, SIIGBF_BIGGERSIZEOK = 0x1;

    /// <summary>Returns a frozen icon image (call from any thread), or null if the shell has none.</summary>
    public static ImageSource? Get(string path, int size = 64) =>
        Cache.GetOrAdd($"{size}|{path}", _ => Load(path, size));

    static ImageSource? Load(string path, int size)
    {
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory);
            if (factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out var hbm) != 0)
                return null;
            try
            {
                // Read the DIB's pixels directly: the usual HBITMAP converters drop the alpha channel.
                if (GetObject(hbm, Marshal.SizeOf<DIBSECTION>(), out var ds) == 0 || ds.bmBits == IntPtr.Zero || ds.bmBitsPixel != 32)
                    return Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                int w = ds.bmWidth, h = ds.bmHeight, stride = ds.bmWidthBytes;
                var pixels = new byte[stride * h];
                Marshal.Copy(ds.bmBits, pixels, 0, pixels.Length);
                if (ds.biHeight > 0) // bottom-up: flip rows
                {
                    var row = new byte[stride];
                    for (int y = 0; y < h / 2; y++)
                    {
                        Buffer.BlockCopy(pixels, y * stride, row, 0, stride);
                        Buffer.BlockCopy(pixels, (h - 1 - y) * stride, pixels, y * stride, stride);
                        Buffer.BlockCopy(row, 0, pixels, (h - 1 - y) * stride, stride);
                    }
                }
                var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
                bmp.Freeze();
                return bmp;
            }
            finally { DeleteObject(hbm); }
        }
        catch { return null; }
    }
}
