using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AudioFlow.App;

internal static class AppIcons
{
    private static readonly ConcurrentDictionary<string, ImageSource> cache = new(StringComparer.OrdinalIgnoreCase);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[] small, uint count);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    public static ImageSource? Get(string path)
    {
        if (cache.TryGetValue(path, out var known)) return known;
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        var small = new IntPtr[1];
        try
        {
            if (ExtractIconEx(path, 0, null, small, 1) == 0 || small[0] == IntPtr.Zero) return null;
            var image = Imaging.CreateBitmapSourceFromHIcon(small[0], Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
            image.Freeze(); cache.TryAdd(path, image); return image;
        }
        catch { return null; }
        finally { if (small[0] != IntPtr.Zero) DestroyIcon(small[0]); }
    }
}
