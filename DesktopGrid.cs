using System.Runtime.InteropServices;

namespace DeskMaster;

internal sealed class DesktopGrid(Point spacing, Point origin, Point[] icons)
{
    public int CardUnit(int iconSize) => Math.Max(iconSize, Math.Min(spacing.X, spacing.Y) - 6);

    public static DesktopGrid Read()
    {
        try
        {
            dynamic windows = Activator.CreateInstance(Type.GetTypeFromCLSID(
                new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"))!)!;
            try
            {
                object location = 0;
                object root = Type.Missing;
                object? desktop = windows.FindWindowSW(ref location, ref root, 8, out int _, 1);
                if (desktop is null)
                    return Fallback();

                try
                {
                    Guid service = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
                    Guid iid = typeof(IShellBrowser).GUID;
                    ((IServiceProviderNative)desktop).QueryService(ref service, ref iid, out var browser);
                    try
                    {
                        browser.QueryActiveShellView(out var view);
                        try
                        {
                            return ReadView(view);
                        }
                        finally { Marshal.ReleaseComObject(view); }
                    }
                    finally { Marshal.ReleaseComObject(browser); }
                }
                finally { Marshal.ReleaseComObject(desktop); }
            }
            finally { Marshal.ReleaseComObject(windows); }
        }
        catch (Exception error) when (error is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return Fallback();
        }
    }

    private static DesktopGrid ReadView(IShellView view)
    {
        var folderView = (IFolderView)view;
        folderView.GetSpacing(out Point spacing);
        if (spacing.X <= 0 || spacing.Y <= 0)
            return Fallback();

        view.GetWindow(out IntPtr window);
        var origin = Point.Empty;
        ClientToScreen(window, ref origin);
        folderView.ItemCount(2, out int count); // SVGIO_ALLVIEW
        var icons = new List<Point>();
        for (int index = 0; index < count; index++)
        {
            if (folderView.Item(index, out IntPtr item) < 0)
                continue;
            try
            {
                if (folderView.GetItemPosition(item, out Point position) >= 0)
                    icons.Add(position + new Size(origin));
            }
            finally { Marshal.FreeCoTaskMem(item); }
        }
        return new DesktopGrid(spacing, icons.Count > 0 ? icons[0] : origin, icons.ToArray());
    }

    private static DesktopGrid Fallback()
    {
        Size spacing = SystemInformation.IconSpacingSize;
        return new DesktopGrid(new Point(Math.Max(1, spacing.Width), Math.Max(1, spacing.Height)), Point.Empty, []);
    }

    public Point? FindSlot(Point requested, Size footprint, int iconSize, int columns, IEnumerable<Rectangle> folders)
    {
        Rectangle area = Screen.FromPoint(requested).WorkingArea;
        var occupied = folders.Concat(icons.Select(icon => new Rectangle(icon, new Size(iconSize, iconSize)))).ToArray();
        int iconSpan = iconSize + (columns - 1) * spacing.X;
        int centeredX = origin.X + (iconSpan - footprint.Width) / 2;
        int firstX = area.Left + Offset(centeredX, area.Left, spacing.X);
        int firstY = area.Top + Offset(origin.Y, area.Top, spacing.Y);
        Point? best = null;
        long bestDistance = long.MaxValue;
        for (int x = firstX; x <= area.Right - footprint.Width; x += spacing.X)
        {
            for (int y = firstY; y <= area.Bottom - footprint.Height; y += spacing.Y)
            {
                long dx = x - (long)requested.X;
                long dy = y - (long)requested.Y;
                long distance = dx * dx + dy * dy;
                var candidate = new Rectangle(new Point(x, y), footprint);
                if (distance >= bestDistance || occupied.Any(candidate.IntersectsWith))
                    continue;

                best = candidate.Location;
                bestDistance = distance;
            }
        }
        return best;
    }

    private static int Offset(int anchor, int start, int step) => ((anchor - start) % step + step) % step;

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProviderNative
    {
        void QueryService(ref Guid service, ref Guid iid, out IShellBrowser browser);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void GetWindow();
        void ContextSensitiveHelp();
        void InsertMenusSB();
        void SetMenuSB();
        void RemoveMenusSB();
        void SetStatusTextSB();
        void EnableModelessSB();
        void TranslateAcceleratorSB();
        void BrowseObject();
        void GetViewStateStream();
        void GetControlWindow();
        void SendControlMsg();
        void QueryActiveShellView(out IShellView view);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void GetWindow(out IntPtr window);
    }

    [ComImport, Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView
    {
        void GetCurrentViewMode();
        void SetCurrentViewMode();
        void GetFolder();
        [PreserveSig] int Item(int index, out IntPtr item);
        void ItemCount(uint flags, out int count);
        void Items();
        void GetSelectionMarkedItem();
        void GetFocusedItem();
        [PreserveSig] int GetItemPosition(IntPtr item, out Point position);
        void GetSpacing(out Point spacing);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr window, ref Point point);
}
