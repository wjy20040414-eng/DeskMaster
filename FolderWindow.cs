using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DeskMaster;

internal sealed class FolderWindow : Form
{
    private static readonly FolderAppearance DefaultAppearance = new();
    internal const int CaptionHeight = 22;
    private const string DesktopViewKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\Shell\Bags\1\Desktop";
    private readonly Func<DesktopFolder, bool> save;
    private readonly Func<DesktopFolder, Point?> findSlot;
    private readonly int cardUnit;
    private readonly Button header;
    private readonly Button preview;
    private readonly Label caption;
    private readonly FlowLayoutPanel contents;
    private readonly BackgroundWindow backdrop;
    private readonly ContextMenuStrip menu;
    private readonly ToolTip tips = new();
    private readonly System.Windows.Forms.Timer transition = new() { Interval = 15 };
    private readonly Dictionary<string, Bitmap> icons = new(StringComparer.OrdinalIgnoreCase);
    private DesktopFolder folder;
    private bool expanded;
    private bool dragging;
    private Point mouseStart;
    private Point dragStart;
    private Rectangle transitionFrom;
    private Rectangle transitionTo;
    private long transitionStarted;
    private double transitionOpacity;
    private FolderAppearance Appearance => folder.Appearance ?? DefaultAppearance;
    private Color Surface => AppearanceDialog.ToColor(Appearance.Background);
    private Color ExpandedTextColor => AppearanceDialog.ToColor(Appearance.ExpandedTextColor);
    private double TargetOpacity => Appearance.OpacityPercent is int percent
        ? Math.Clamp(percent, 20, 100) / 100.0 : expanded ? 0.86 : 0.62;

    public FolderWindow(DesktopFolder folder, Func<DesktopFolder, bool> save, Action delete,
        Func<DesktopFolder, Point?> findSlot, int cardUnit)
    {
        this.folder = folder;
        this.save = save;
        this.findSlot = findSlot;
        this.cardUnit = cardUnit;
        transition.Tick += (_, _) => AnimateTransition();
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        ForeColor = Color.Black;
        Font = new Font("Microsoft YaHei UI", 9);
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;

        menu = new ContextMenuStrip();
        menu.Items.Add("重命名…", null, (_, _) => Rename());
        var sizeMenu = new ToolStripMenuItem("文件夹尺寸");
        for (int columns = 1; columns <= 3; columns++)
        {
            int selectedColumns = columns;
            var choice = new ToolStripMenuItem($"{columns * columns} 倍图标大小（{columns}×{columns}）")
            {
                Tag = columns
            };
            choice.Click += (_, _) =>
            {
                Place(this.folder with { IconColumns = selectedColumns });
            };
            sizeMenu.DropDownItems.Add(choice);
        }
        menu.Opening += (_, _) =>
        {
            foreach (ToolStripMenuItem choice in sizeMenu.DropDownItems)
                choice.Checked = (int)choice.Tag! == this.folder.IconColumns;
        };
        menu.Items.Add(sizeMenu);
        menu.Items.Add("外观…", null, (_, _) => EditAppearance());
        menu.Items.Add("删除文件夹…", null, (_, _) =>
        {
            if (MessageBox.Show($"删除「{this.folder.Name}」？", "DeskMaster",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                delete();
        });
        ContextMenuStrip = menu;

        header = MakeButton();
        header.TextAlign = ContentAlignment.MiddleCenter;
        header.FlatAppearance.MouseOverBackColor = Surface;
        header.FlatAppearance.MouseDownBackColor = Surface;
        header.Cursor = Cursors.Default;
        preview = MakeButton();
        preview.FlatAppearance.MouseOverBackColor = Surface;
        preview.FlatAppearance.MouseDownBackColor = Surface;
        preview.AccessibleName = "展开文件夹";
        preview.Paint += PaintPreview;
        caption = new Label { Cursor = Cursors.Hand };
        caption.Paint += PaintCaption;
        caption.Click += (_, _) => Toggle();
        contents = new FlowLayoutPanel { AutoScroll = true, WrapContents = true };
        backdrop = new BackgroundWindow(this) { ContextMenuStrip = menu };
        AcceptFiles(backdrop);
        header.Click += (_, _) =>
        {
            if (!dragging)
                Toggle();
        };
        preview.Click += (_, _) =>
        {
            if (!dragging)
                Toggle();
        };
        AttachDragHandle(header);
        AttachDragHandle(preview);
        Controls.AddRange([header, preview, contents, caption]);
        foreach (Control control in new Control[] { this, header, preview, contents, caption })
            AcceptFiles(control);

        ApplyAppearance();
        UpdateTitle();
        ApplyLocation(folder.Location);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var settings = base.CreateParams;
            settings.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            return settings;
        }
    }

    protected override bool ShowWithoutActivation => true;

    public void ShowOnDesktop()
    {
        ApplyLocation(folder.Location);
        backdrop.Show();
        if (!Visible)
            Show(backdrop);
        // HWND_BOTTOM and SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE.
        SetWindowPos(Handle, new IntPtr(1), 0, 0, 0, 0, 0x0013);
        SetWindowPos(backdrop.Handle, Handle, 0, 0, 0, 0, 0x0013);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0021) // WM_MOUSEACTIVATE
        {
            message.Result = new IntPtr(3); // MA_NOACTIVATE
            return;
        }
        base.WndProc(ref message);
    }

    private Button MakeButton() => new ForegroundButton()
    {
        FlatStyle = FlatStyle.Flat,
        FlatAppearance = { BorderSize = 0, MouseOverBackColor = BackColor, MouseDownBackColor = BackColor },
        ForeColor = ExpandedTextColor,
        BackColor = BackColor,
        UseVisualStyleBackColor = false,
        Cursor = Cursors.Hand
    };

    private void Toggle()
    {
        transitionFrom = new Rectangle(Location, new Size(Width, Width));
        transitionOpacity = backdrop.Opacity;
        expanded = !expanded;
        UpdateTitle();
        Rectangle target = GetFolderBounds(folder.Location);
        transitionTo = new Rectangle(target.Location, new Size(target.Width, target.Width));
        transitionStarted = Stopwatch.GetTimestamp();
        transition.Start();
        caption.Hide();
        header.Hide();
        contents.Hide();
        preview.Show();
        Bounds = transitionFrom;
        preview.Invalidate();
    }

    private sealed class ForegroundButton : Button
    {
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0202 && Capture && FindForm() is FolderWindow folder)
            {
                Point point = Cursor.Position;
                // WinForms normally tests the window under the pointer before raising Click.
                // A transparent pixel inside this button can hit the card's background instead.
                if (ClientRectangle.Contains(PointToClient(point)) && WindowFromPoint(point) == folder.backdrop.Handle)
                    OnClick(EventArgs.Empty);
            }
            base.WndProc(ref message);
        }
    }

    private void AnimateTransition()
    {
        double progress = Stopwatch.GetElapsedTime(transitionStarted).TotalMilliseconds / 180;
        if (progress >= 1)
        {
            ApplyLocation(folder.Location);
            return;
        }

        double eased = progress * progress * (3 - 2 * progress);
        int Interpolate(int from, int to) => (int)Math.Round(from + (to - from) * eased);
        int side = Interpolate(transitionFrom.Width, transitionTo.Width);
        Bounds = new Rectangle(Interpolate(transitionFrom.X, transitionTo.X),
            Interpolate(transitionFrom.Y, transitionTo.Y), side, side);
        backdrop.Opacity = transitionOpacity + (TargetOpacity - transitionOpacity) * eased;
    }

    private void UpdateTitle()
    {
        Text = folder.Name;
        header.Text = folder.Name;
        caption.AccessibleName = folder.Name;
        caption.Invalidate();
        tips.SetToolTip(caption, folder.Name);
    }

    internal static int DesktopIconSize => Registry.GetValue(DesktopViewKey, "IconSize", null) is int configured
        && configured is >= 16 and <= 256 ? configured : 48;

    internal static int CollapsedSide(int cardUnit, int columns)
    {
        int captionOffset = Math.Max(0, cardUnit - (DesktopIconSize + 8));
        return cardUnit * columns - captionOffset;
    }

    private Rectangle GetFolderBounds(Point requested)
    {
        var area = Screen.FromPoint(requested).WorkingArea;
        int captionHeight = expanded ? 0 : CaptionHeight;
        int side = expanded ? 240 : CollapsedSide(cardUnit, folder.IconColumns);
        side = Math.Min(side, Math.Min(area.Width, area.Height - captionHeight));
        int height = side + captionHeight;
        int left = Math.Clamp(requested.X, area.Left, area.Right - side);
        int top = Math.Clamp(requested.Y, area.Top, area.Bottom - height);
        return new Rectangle(left, top, side, height);
    }

    private void ApplyLocation(Point requested)
    {
        bool wasAnimating = transition.Enabled;
        transition.Stop();
        backdrop.Opacity = TargetOpacity;
        Bounds = GetFolderBounds(requested);
        header.Visible = expanded;
        contents.Visible = expanded;
        preview.Visible = !expanded;
        caption.Visible = !expanded;
        if (wasAnimating && expanded)
            RefreshContents();
        preview.Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (header is null || preview is null || contents is null || caption is null || ClientSize.Width < 8)
            return;

        int side = ClientSize.Width;
        var region = CreateSymmetricRegion(new Size(side, side));
        if (backdrop is not null)
        {
            backdrop.Bounds = new Rectangle(Location, new Size(side, side));
            backdrop.Region = region.Clone();
        }
        if (!expanded && !transition.Enabled)
            region.Union(new Rectangle(0, side, side, CaptionHeight));
        Region = region;

        header.Bounds = new Rectangle(12, 4, Math.Max(0, Width - 24), 30);
        preview.Bounds = new Rectangle(0, 0, side, side);
        caption.Bounds = new Rectangle(0, side, side, CaptionHeight);
        contents.Bounds = new Rectangle(8, 36, Math.Max(0, Width - 16), Math.Max(0, Height - 44));
        Invalidate();
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (backdrop is not null)
            backdrop.Location = Location;
    }

    private void PaintCaption(object? sender, PaintEventArgs e)
    {
        const TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
            | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        var bounds = caption.ClientRectangle;
        var shadow = bounds;
        shadow.Offset(0, 1);
        TextRenderer.DrawText(e.Graphics, folder.Name, Font, shadow, Color.Black, flags);
        TextRenderer.DrawText(e.Graphics, folder.Name, Font, bounds, AppearanceDialog.ToColor(Appearance.CaptionColor), flags);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (expanded && !transition.Enabled)
            DrawOutline(e.Graphics, ClientRectangle);
    }

    private void DrawOutline(Graphics graphics, Rectangle bounds)
    {
        int thickness = Math.Clamp(Appearance.BorderThickness, 0, 6);
        if (thickness == 0 || bounds.Width <= thickness * 2 || bounds.Height <= thickness * 2)
            return;

        var previousMode = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        int innerDiameter = Math.Max(2, CornerDiameter(bounds.Width) - thickness * 2);
        using var inner = RoundedSquare(Rectangle.Inflate(bounds, -thickness, -thickness), innerDiameter);
        using var outline = new GraphicsPath(FillMode.Alternate);
        // The form region clips the outside. Fill to that edge so no background shows around the border.
        outline.AddRectangle(Rectangle.Inflate(bounds, 1, 1));
        outline.AddPath(inner, connect: false);
        using var brush = new SolidBrush(AppearanceDialog.ToColor(Appearance.BorderColor));
        graphics.FillPath(brush, outline);
        graphics.SmoothingMode = previousMode;
    }

    private static int CornerDiameter(int width) => Math.Clamp(width / 3, 4, 22);

    private static Region CreateSymmetricRegion(Size size)
    {
        int radius = Math.Min(CornerDiameter(size.Width), size.Height) / 2;
        var region = new Region(new Rectangle(0, radius, size.Width, size.Height - radius * 2));
        for (int row = 0; row < radius; row++)
        {
            double offset = radius - row - 0.5;
            int inset = (int)Math.Round(radius - Math.Sqrt(radius * radius - offset * offset));
            int width = size.Width - inset * 2;
            region.Union(new Rectangle(inset, row, width, 1));
            region.Union(new Rectangle(inset, size.Height - row - 1, width, 1));
        }
        return region;
    }

    private static GraphicsPath RoundedSquare(Rectangle bounds, int diameter)
    {
        var shape = new GraphicsPath();
        shape.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        shape.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        shape.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        shape.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        shape.CloseFigure();
        return shape;
    }

    private void AttachDragHandle(Control handle)
    {
        handle.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left)
                return;
            dragging = false;
            mouseStart = Cursor.Position;
            dragStart = Location;
        };
        handle.MouseMove += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || !handle.Capture || transition.Enabled)
                return;
            var delta = Cursor.Position - new Size(mouseStart);
            if (!dragging && Math.Abs(delta.X) + Math.Abs(delta.Y) < 5)
                return;
            dragging = true;
            ApplyLocation(dragStart + new Size(delta));
        };
        handle.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || !dragging)
                return;
            Place(folder with { Location = dragStart + new Size(Cursor.Position - new Size(mouseStart)) });
            dragging = false;
        };
    }

    private void Place(DesktopFolder requested)
    {
        if (findSlot(requested) is Point slot)
            Save(requested with { Location = slot });
        else
            MessageBox.Show("当前屏幕没有足够的桌面空间。", "DeskMaster");
        ApplyLocation(folder.Location);
    }

    private void AcceptFiles(Control target)
    {
        target.AllowDrop = true;
        target.DragEnter += (_, e) => e.Effect =
            e.AllowedEffect.HasFlag(DragDropEffects.Copy) && DroppedFiles(e.Data).Any()
                ? DragDropEffects.Copy : DragDropEffects.None;
        target.DragDrop += (_, e) =>
        {
            if (!e.AllowedEffect.HasFlag(DragDropEffects.Copy))
                return;
            string[] dropped = DroppedFiles(e.Data).ToArray();
            e.Effect = dropped.Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
            var shortcuts = folder.Shortcuts.Concat(dropped)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (shortcuts.Length > folder.Shortcuts.Length && Save(folder with { Shortcuts = shortcuts }))
                BeginInvoke(RefreshContents);
        };
    }

    private static IEnumerable<string> DroppedFiles(IDataObject? data) =>
        (data?.GetData(DataFormats.FileDrop, autoConvert: false) as string[] ?? [])
            .Where(File.Exists);

    private void RefreshContents()
    {
        preview.Invalidate();
        if (!expanded)
            return;

        contents.SuspendLayout();
        try
        {
            while (contents.Controls.Count > 0)
                contents.Controls[0].Dispose();

            if (folder.Shortcuts.Length == 0)
            {
                var hint = new Label { AutoSize = true, Text = "拖入文件或快捷方式", ForeColor = ExpandedTextColor, Margin = new Padding(8) };
                AcceptFiles(hint);
                contents.Controls.Add(hint);
                return;
            }

            foreach (string path in folder.Shortcuts)
            {
                var tile = MakeButton();
                tile.Size = new Size(67, 65);
                tile.Margin = new Padding(2);
                string extension = Path.GetExtension(path);
                string name = extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".url", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFileNameWithoutExtension(path) : Path.GetFileName(path);
                tile.AccessibleName = name;
                tile.Paint += (_, e) =>
                {
                    const int iconSide = 32;
                    int iconLeft = (tile.ClientSize.Width - iconSide) / 2;
                    e.Graphics.DrawImage(GetIcon(path), iconLeft, 5, iconSide, iconSide);
                    var label = new Rectangle(2, 39, tile.ClientSize.Width - 4, 20);
                    TextFormatFlags alignment = Appearance.ExpandedAlignment switch
                    {
                        HorizontalAlignment.Left => TextFormatFlags.Left,
                        HorizontalAlignment.Right => TextFormatFlags.Right,
                        _ => TextFormatFlags.HorizontalCenter
                    };
                    TextRenderer.DrawText(e.Graphics, name, tile.Font, label, tile.ForeColor,
                        alignment | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                };
                tile.Click += (_, _) => Launch(path);
                var actions = new ContextMenuStrip();
                actions.Items.Add("从文件夹移除", null, (_, _) =>
                {
                    if (Save(folder with { Shortcuts = folder.Shortcuts.Where(item => item != path).ToArray() }))
                        RefreshContents();
                });
                tile.ContextMenuStrip = actions;
                tile.Disposed += (_, _) => actions.Dispose();
                tips.SetToolTip(tile, name);
                AcceptFiles(tile);
                contents.Controls.Add(tile);
            }
        }
        finally
        {
            contents.ResumeLayout();
        }
    }

    private void EditAppearance()
    {
        using var dialog = new AppearanceDialog(Appearance);
        if (dialog.ShowDialog() == DialogResult.OK && Save(folder with { Appearance = dialog.Selection }))
        {
            ApplyAppearance();
            RefreshContents();
            Invalidate();
        }
    }

    private void ApplyAppearance()
    {
        backdrop.BackColor = Surface;
        header.ForeColor = ExpandedTextColor;
        header.TextAlign = Appearance.ExpandedAlignment switch
        {
            HorizontalAlignment.Left => ContentAlignment.MiddleLeft,
            HorizontalAlignment.Right => ContentAlignment.MiddleRight,
            _ => ContentAlignment.MiddleCenter
        };
        // Keep the caption's transparency key distinct from all user-selected colors.
        int key = 0x7B7F83;
        while (key == Appearance.Background || key == Appearance.CaptionColor ||
               key == Appearance.ExpandedTextColor || key == Appearance.BorderColor)
            key--;
        Color foregroundKey = AppearanceDialog.ToColor(key);
        TransparencyKey = BackColor = caption.BackColor = foregroundKey;
        header.BackColor = preview.BackColor = foregroundKey;
        header.FlatAppearance.MouseOverBackColor = preview.FlatAppearance.MouseOverBackColor = foregroundKey;
        header.FlatAppearance.MouseDownBackColor = preview.FlatAppearance.MouseDownBackColor = foregroundKey;
        backdrop.Opacity = TargetOpacity;
        caption.Invalidate();
    }

    private void PaintPreview(object? sender, PaintEventArgs e)
    {
        if (folder.Shortcuts.Length == 0)
        {
            TextRenderer.DrawText(e.Graphics, "＋", Font, preview.ClientRectangle, Color.Black,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            DrawOutline(e.Graphics, preview.ClientRectangle);
            return;
        }

        int count = Math.Min(folder.Shortcuts.Length, 9);
        int columns = count > 4 ? 3 : count > 1 ? 2 : 1;
        int cell = Math.Max(1, Math.Min(DesktopIconSize + 4, (preview.Width - 12) / columns));
        int left = (preview.Width - Math.Min(count, columns) * cell) / 2;
        int top = (preview.Height - ((count + columns - 1) / columns) * cell) / 2;
        for (int i = 0; i < count; i++)
        {
            int x = left + i % columns * cell;
            int y = top + i / columns * cell;
            int inset = cell > 8 ? 2 : 0;
            e.Graphics.DrawImage(GetIcon(folder.Shortcuts[i]), x + inset, y + inset, cell - inset * 2, cell - inset * 2);
        }
        DrawOutline(e.Graphics, preview.ClientRectangle);
    }

    private Bitmap GetIcon(string path)
    {
        if (icons.TryGetValue(path, out var cached))
            return cached;
        const uint systemIconIndex = 0x4000; // SHGFI_SYSICONINDEX
        IntPtr imageList = SHGetFileInfoW(path, 0, out var info, (uint)Marshal.SizeOf<ShellFileInfo>(), systemIconIndex);
        IntPtr handle = imageList == IntPtr.Zero
            ? IntPtr.Zero
            : ImageList_GetIcon(imageList, info.IconIndex, 1); // ILD_TRANSPARENT, with no overlay mask
        if (handle == IntPtr.Zero)
            cached = SystemIcons.Application.ToBitmap();
        else
        {
            try
            {
                using var icon = Icon.FromHandle(handle);
                cached = icon.ToBitmap();
            }
            finally
            {
                DestroyIcon(handle);
            }
        }
        icons.Add(path, cached);
        return cached;
    }

    private static void Launch(string path)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception error) when (error is Win32Exception or FileNotFoundException)
        {
            MessageBox.Show($"无法打开此项目。\n\n{error.Message}", "DeskMaster",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private bool Save(DesktopFolder updated)
    {
        if (!save(updated))
            return false;
        folder = updated;
        return true;
    }

    private void Rename()
    {
        using var dialog = new Form
        {
            Text = "重命名文件夹", StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            ShowInTaskbar = false, ClientSize = new Size(310, 87), Font = Font
        };
        var name = new TextBox { Text = folder.Name, MaxLength = 48, Bounds = new Rectangle(12, 12, 286, 27) };
        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Bounds = new Rectangle(141, 48, 75, 27) };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(223, 48, 75, 27) };
        dialog.Controls.AddRange([name, ok, cancel]);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        name.SelectAll();
        if (dialog.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(name.Text) &&
            Save(folder with { Name = name.Text.Trim() }))
            UpdateTitle();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            tips.Dispose();
            transition.Dispose();
            backdrop.Dispose();
            menu.Dispose();
            foreach (var icon in icons.Values)
                icon.Dispose();
        }
    }

    private sealed class BackgroundWindow : Form
    {
        private readonly FolderWindow foreground;

        public BackgroundWindow(FolderWindow foreground)
        {
            this.foreground = foreground;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var settings = base.CreateParams;
                settings.ExStyle |= 0x08000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
                return settings;
            }
        }

        protected override void WndProc(ref Message message)
        {
            // Color-keyed foreground pixels pass mouse input to this window.
            // Dispatch it to the control that occupies the same point.
            if ((message.Msg is >= 0x0200 and <= 0x0209 or 0x007B) && foreground.Visible)
            {
                Point screen = Cursor.Position;
                Control target = foreground;
                while (target.GetChildAtPoint(target.PointToClient(screen), GetChildAtPointSkip.Invisible) is Control child)
                    target = child;

                Point point = message.Msg == 0x007B ? screen : target.PointToClient(screen);
                IntPtr coordinates = new(unchecked((point.Y << 16) | (point.X & 0xFFFF)));
                message.Result = SendMessage(target.Handle, message.Msg, message.WParam, coordinates);
                return;
            }
            base.WndProc(ref message);
        }
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr IconHandle;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr SHGetFileInfoW(string path, uint attributes, out ShellFileInfo info, uint size, uint flags);

    [DllImport("comctl32.dll")]
    private static extern IntPtr ImageList_GetIcon(IntPtr imageList, int index, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
