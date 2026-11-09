using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace PaperTodo.Plugin.BulkImport;

/// <summary>
/// Locates the paper window the user just clicked and places the popup over it. The plugin has no
/// host API for window geometry, so this uses the cursor-derived window and normal own-window
/// discovery only.
/// </summary>
internal static class BulkImportWindowPlacement
{
    private const uint GaRoot = 2;
    private const uint MonitorDefaultToNearest = 2;

    public static Window? FindWindowUnderCursor()
    {
        try
        {
            if (!GetCursorPos(out var point))
            {
                return null;
            }
            var root = GetAncestor(WindowFromPoint(point), GaRoot);
            if (root == IntPtr.Zero)
            {
                return null;
            }

            var application = Application.Current;
            if (application == null)
            {
                return null;
            }

            foreach (Window window in application.Windows)
            {
                if (window == null || !window.IsVisible)
                {
                    continue;
                }
                var handle = new WindowInteropHelper(window).Handle;
                if (handle != IntPtr.Zero && handle == root)
                {
                    return window;
                }
            }
        }
        catch
        {
            // Placement is cosmetic; never let it block the import surface.
        }

        return null;
    }

    public static void Place(Window dialog, Window? owner)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        var (workLeft, workTop, workRight, workBottom) = WorkArea(owner);

        var width = double.IsNaN(dialog.Width) ? dialog.ActualWidth : dialog.Width;
        var height = double.IsNaN(dialog.Height) ? dialog.ActualHeight : dialog.Height;
        if (double.IsNaN(width) || width <= 0)
        {
            width = 640;
        }
        if (double.IsNaN(height) || height <= 0)
        {
            height = 560;
        }

        double left;
        double top;
        if (owner != null && owner.IsVisible)
        {
            left = owner.Left + ((owner.ActualWidth - width) / 2);
            top = owner.Top + ((owner.ActualHeight - height) / 2);
        }
        else
        {
            var (cursorLeft, cursorTop) = CursorInDips();
            left = cursorLeft - (width / 2);
            top = cursorTop - 40;
        }

        left = Math.Clamp(left, workLeft + 8, Math.Max(workLeft + 8, workRight - width - 8));
        top = Math.Clamp(top, workTop + 8, Math.Max(workTop + 8, workBottom - height - 8));

        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Left = left;
        dialog.Top = top;
    }

    private static (double Left, double Top) CursorInDips()
    {
        if (!GetCursorPos(out var point))
        {
            return (SystemParameters.WorkArea.Left + 200, SystemParameters.WorkArea.Top + 160);
        }

        var source = PresentationSource.FromVisual(Application.Current?.MainWindow ?? (Visual?)null);
        var transform = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var dip = transform.Transform(new Point(point.X, point.Y));
        return (dip.X, dip.Y);
    }

    private static (double Left, double Top, double Right, double Bottom) WorkArea(Window? owner)
    {
        var fallback = SystemParameters.WorkArea;
        try
        {
            var anchor = owner != null && owner.IsVisible
                ? new WindowInteropHelper(owner).Handle
                : IntPtr.Zero;
            var monitor = MonitorFromWindow(
                anchor,
                anchor == IntPtr.Zero ? MonitorDefaultToNearest : 0);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            {
                return (fallback.Left, fallback.Top, fallback.Right, fallback.Bottom);
            }

            var source = owner != null ? PresentationSource.FromVisual(owner) : null;
            var transform = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var topLeft = transform.Transform(
                new Point(info.Work.Left, info.Work.Top));
            var bottomRight = transform.Transform(
                new Point(info.Work.Right, info.Work.Bottom));
            return (topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
        }
        catch
        {
            return (fallback.Left, fallback.Top, fallback.Right, fallback.Bottom);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect32 Monitor;
        public Rect32 Work;
        public uint Flags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out Point32 point);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point32 point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
