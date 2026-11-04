using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DeskMaster;

internal sealed class FolderWindow : Form
{
    private static readonly FolderAppearance DefaultAppearance = new();
    internal const int CaptionHeight = 22;
    private const int TransitionMilliseconds = 220;
    private const int MaximumHeaderClickWaitMilliseconds = 100;
    private const int ShortcutIconSize = 32;
    private const string DesktopViewKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\Shell\Bags\1\Desktop";
    private readonly Func<DesktopFolder, bool> save;
    private readonly Func<DesktopFolder, Point?> findSlot;
    private readonly Func<AnimationSettings> getAnimationSettings;
    private readonly Func<string, string> resolveShortcutPath;
    private readonly int cardUnit;
    private readonly Button header;
    private readonly Button preview;
    private readonly Label caption;
    private readonly FlowLayoutPanel contents;
    private readonly BackgroundWindow backdrop;
    private readonly ContextMenuStrip menu;
    private readonly ToolTip tips = new();
    private readonly System.Windows.Forms.Timer headerClickTimer = new();
    private readonly Dictionary<string, Bitmap> icons = new(StringComparer.OrdinalIgnoreCase);
    private DesktopFolder folder;
    private bool expanded;
    private bool dragging;
    private bool headerDoubleClick;
    private bool renameWhenExpanded;
    private TextBox? nameEditor;
    private Point pendingHeaderClick;
    private long pendingHeaderClickAt;
    private Point mouseStart;
    private Point dragStart;
    private Rectangle transitionFrom;
    private Rectangle transitionTo;
    private long transitionStarted;
    private double transitionDuration;
    private object? activeTransition;
    private double transitionOpacity;
    private double expansion;
    private double transitionFromExpansion;
    private Rectangle expandedViewport;
    private TransitionShortcut[] transitionShortcuts = [];
    private string[]? displayedShortcuts;
    private FolderAppearance? displayedAppearance;

    private readonly record struct TransitionShortcut(string Path, string Name, Rectangle Thumbnail, Rectangle Tile);

    private bool IsAnimating => activeTransition is not null;
    private bool EditingTitle => nameEditor?.Parent == this;
    private FolderAppearance Appearance => folder.Appearance ?? DefaultAppearance;
    private Color Surface => AppearanceDialog.ToColor(Appearance.Background);
    private Color ExpandedTextColor => AppearanceDialog.ToColor(Appearance.ExpandedTextColor);
    private double TargetOpacity => Appearance.OpacityPercent is int percent
        ? Math.Clamp(percent, 20, 100) / 100.0 : expanded ? 0.86 : 0.62;

    public FolderWindow(DesktopFolder folder, Func<DesktopFolder, bool> save, Action delete,
        Func<DesktopFolder, Point?> findSlot, int cardUnit,
        Func<AnimationSettings> getAnimationSettings, Action<AnimationSettings> saveAnimationSettings,
        Func<string, string> resolveShortcutPath)
    {
        this.folder = folder;
        this.save = save;
        this.findSlot = findSlot;
        this.cardUnit = cardUnit;
        this.getAnimationSettings = getAnimationSettings;
        this.resolveShortcutPath = resolveShortcutPath;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        ForeColor = Color.Black;
        Font = new Font("Microsoft YaHei UI", 9);
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;

        menu = new ContextMenuStrip();
        menu.Items.Add("重命名", null, (_, _) => BeginRename());
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
            headerClickTimer.Stop();
            foreach (ToolStripMenuItem choice in sizeMenu.DropDownItems)
                choice.Checked = (int)choice.Tag! == this.folder.IconColumns;
        };
        menu.Items.Add(sizeMenu);
        menu.Items.Add("外观…", null, (_, _) => EditAppearance());
        menu.Items.Add(CreateAnimationMenu(saveAnimationSettings));
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
        AcceptItems(backdrop);
        header.MouseDown += HeaderMouseDown;
        header.Click += HeaderClick;
        headerClickTimer.Tick += (_, _) =>
        {
            headerClickTimer.Stop();
            if (header.Visible && !dragging)
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
            AcceptItems(control);

        ApplyAppearance();
        UpdateTitle();
        ApplyLocation(folder.Location);
    }

    private ToolStripMenuItem CreateAnimationMenu(Action<AnimationSettings> saveSettings)
    {
        var animationMenu = new ToolStripMenuItem("动画设置（全局）");
        AddChoices("帧率", AnimationSettings.FrameRates,
            rate => rate == 0 ? "自动（随屏幕刷新）" : $"{rate} FPS",
            settings => settings.FrameRate, (settings, rate) => settings with { FrameRate = rate });
        AddChoices("播放速度", AnimationSettings.Speeds,
            speed => speed == 100 ? "1×（默认）" : $"{speed / 100m:0.##}×",
            settings => settings.SpeedPercent, (settings, speed) => settings with { SpeedPercent = speed });
        return animationMenu;

        void AddChoices(string label, IReadOnlyList<int> values, Func<int, string> format,
            Func<AnimationSettings, int> getValue, Func<AnimationSettings, int, AnimationSettings> setValue)
        {
            var group = new ToolStripMenuItem(label);
            foreach (int value in values)
            {
                var item = new ToolStripMenuItem(format(value)) { Tag = value };
                item.Click += (_, _) =>
                {
                    var current = getAnimationSettings();
                    if (getValue(current) != value)
                        saveSettings(setValue(current, value));
                };
                group.DropDownItems.Add(item);
            }
            menu.Opening += (_, _) =>
            {
                int current = getValue(getAnimationSettings());
                foreach (ToolStripMenuItem item in group.DropDownItems)
                    item.Checked = (int)item.Tag! == current;
            };
            animationMenu.DropDownItems.Add(group);
        }
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
        RestoreDesktopOrder();
    }

    private void RestoreDesktopOrder()
    {
        // HWND_BOTTOM and SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE.
        SetWindowPos(Handle, new IntPtr(1), 0, 0, 0, 0, 0x0013);
        SetWindowPos(backdrop.Handle, Handle, 0, 0, 0, 0, 0x0013);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0021 && nameEditor is null) // WM_MOUSEACTIVATE
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
        headerClickTimer.Stop();
        bool wasAnimating = IsAnimating;
        var animationSettings = getAnimationSettings();
        transitionFrom = new Rectangle(Location, new Size(Width, Width));
        transitionOpacity = backdrop.Opacity;
        transitionFromExpansion = expansion;
        expanded = !expanded;
        if (!expanded)
        {
            renameWhenExpanded = false;
            if (nameEditor is not null)
                FinishNameEdit(nameEditor);
        }
        UpdateTitle();
        Rectangle target = GetFolderBounds(folder.Location, expanded);
        transitionTo = new Rectangle(target.Location, new Size(target.Width, target.Width));
        if (!wasAnimating)
        {
            transitionDuration = TransitionMilliseconds * 100.0 / animationSettings.SpeedPercent;
            if (expanded)
            {
                contents.Bounds = GetContentsBounds(target.Size);
                // Create the handle while hidden so the measured viewport includes native scrollbars.
                _ = contents.Handle;
                RefreshContents();
                contents.AutoScrollPosition = Point.Empty;
            }
            CaptureTransitionShortcuts();
        }

        transitionStarted = Stopwatch.GetTimestamp();
        object animation = activeTransition ??= new object();
        caption.Hide();
        header.Hide();
        contents.Hide();
        preview.Show();
        Bounds = transitionFrom;
        preview.Invalidate();
        if (!wasAnimating)
            _ = RenderTransitionAsync(animation, animationSettings.FrameRate);
    }

    private void HeaderMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;

        Size size = SystemInformation.DoubleClickSize;
        var bounds = new Rectangle(pendingHeaderClick.X - size.Width / 2,
            pendingHeaderClick.Y - size.Height / 2, size.Width, size.Height);
        headerDoubleClick = headerClickTimer.Enabled &&
            Stopwatch.GetElapsedTime(pendingHeaderClickAt).TotalMilliseconds <= headerClickTimer.Interval &&
            bounds.Contains(header.PointToScreen(e.Location));
        // Pause the first click as soon as the second press starts, including when it turns into a drag.
        headerClickTimer.Stop();
    }

    private void HeaderClick(object? sender, EventArgs e)
    {
        if (dragging || !header.Visible)
            return;
        if (e is not MouseEventArgs mouse)
        {
            Toggle();
            return;
        }

        if (headerDoubleClick)
        {
            headerDoubleClick = false;
            // Move keyboard focus after the button finishes mouse-up and releases capture.
            BeginInvoke(() =>
            {
                if (header.Visible)
                    BeginRename();
            });
            return;
        }

        pendingHeaderClick = header.PointToScreen(mouse.Location);
        pendingHeaderClickAt = Stopwatch.GetTimestamp();
        headerClickTimer.Interval = Math.Min(SystemInformation.DoubleClickTime, MaximumHeaderClickWaitMilliseconds);
        headerClickTimer.Start();
    }

    private sealed class ForegroundButton : Button
    {
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0202 && Capture && FindForm() is FolderWindow folder)
            {
                Point screen = Cursor.Position;
                Point point = PointToClient(screen);
                // WinForms normally tests the window under the pointer before raising Click.
                // A transparent pixel inside this button can hit the card's background instead.
                if (ClientRectangle.Contains(point) && WindowFromPoint(screen) == folder.backdrop.Handle)
                    OnClick(new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0));
            }
            base.WndProc(ref message);
        }
    }

    private async Task RenderTransitionAsync(object animation, int frameRate)
    {
        long clockStarted = transitionStarted;
        double frameInterval = frameRate == 0 ? 0 : 1000.0 / frameRate;
        double nextFrame = 0;
        while (activeTransition == animation)
        {
            // Wait off the UI thread. Queue only one frame so input and painting can keep up.
            await Task.Run(WaitForNextFrameAsync);
            if (activeTransition != animation)
                return;

            if (frameInterval > 0)
            {
                long now = Stopwatch.GetTimestamp();
                double elapsed = Stopwatch.GetElapsedTime(clockStarted, now).TotalMilliseconds;
                bool complete = Stopwatch.GetElapsedTime(transitionStarted, now).TotalMilliseconds >= transitionDuration;
                // Allow for compositor jitter, and finish on time even between scheduled frames.
                if (elapsed + 1 < nextFrame && !complete)
                    continue;
                nextFrame = elapsed >= nextFrame + frameInterval ? elapsed + frameInterval : nextFrame + frameInterval;
            }

            AnimateTransition();
            preview.Update();
        }
    }

    private static async Task WaitForNextFrameAsync()
    {
        long started = Stopwatch.GetTimestamp();
        if (DwmFlush() != 0 || Stopwatch.GetElapsedTime(started).TotalMilliseconds < 1)
            await Task.Delay(8).ConfigureAwait(false);
    }

    private void AnimateTransition()
    {
        double progress = Stopwatch.GetElapsedTime(transitionStarted).TotalMilliseconds / transitionDuration;
        if (progress >= 1)
        {
            ApplyLocation(folder.Location);
            return;
        }

        double eased = progress * progress * (3 - 2 * progress);
        expansion = Lerp(transitionFromExpansion, expanded ? 1 : 0, eased);
        Bounds = Lerp(transitionFrom, transitionTo, eased);
        backdrop.Opacity = transitionOpacity + (TargetOpacity - transitionOpacity) * eased;
        preview.Invalidate();
    }

    private static double Lerp(double from, double to, double progress) => from + (to - from) * progress;

    private static int Lerp(int from, int to, double progress) => (int)Math.Round(Lerp((double)from, to, progress));

    private static Rectangle Lerp(Rectangle from, Rectangle to, double progress) => new(
        Lerp(from.X, to.X, progress), Lerp(from.Y, to.Y, progress),
        Lerp(from.Width, to.Width, progress), Lerp(from.Height, to.Height, progress));

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

    private Rectangle GetFolderBounds(Point requested, bool isExpanded)
    {
        var area = Screen.FromPoint(requested).WorkingArea;
        int captionHeight = isExpanded ? 0 : CaptionHeight;
        int side = isExpanded ? 240 : CollapsedSide(cardUnit, folder.IconColumns);
        side = Math.Min(side, Math.Min(area.Width, area.Height - captionHeight));
        int height = side + captionHeight;
        int left = Math.Clamp(requested.X, area.Left, area.Right - side);
        int top = Math.Clamp(requested.Y, area.Top, area.Bottom - height);
        return new Rectangle(left, top, side, height);
    }

    private void ApplyLocation(Point requested)
    {
        activeTransition = null;
        expansion = expanded ? 1 : 0;
        transitionShortcuts = [];
        backdrop.Opacity = TargetOpacity;
        Bounds = GetFolderBounds(requested, expanded);
        header.Visible = expanded && !EditingTitle;
        contents.Visible = expanded;
        preview.Visible = !expanded;
        caption.Visible = !expanded;
        preview.Invalidate();
        if (expanded && renameWhenExpanded)
            BeginRename();
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
        if (!expanded && !IsAnimating)
            region.Union(new Rectangle(0, side, side, CaptionHeight));
        Region = region;

        header.Bounds = new Rectangle(12, 4, Math.Max(0, Width - 24), 30);
        if (nameEditor is not null && EditingTitle)
            PositionTitleEditor(nameEditor);
        preview.Bounds = new Rectangle(0, 0, side, side);
        caption.Bounds = new Rectangle(0, side, side, CaptionHeight);
        if (expanded && !IsAnimating)
            contents.Bounds = GetContentsBounds(ClientSize);
        Invalidate();
    }

    private static Rectangle GetContentsBounds(Size size) =>
        new(8, 36, Math.Max(0, size.Width - 16), Math.Max(0, size.Height - 44));

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
        if (expanded && !IsAnimating)
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
            if (e.Button != MouseButtons.Left || !handle.Capture || IsAnimating)
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

    private void AttachShortcutDrag(Button tile, string path)
    {
        Rectangle dragArea = Rectangle.Empty;
        bool draggingItem = false;

        tile.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left)
                return;

            headerClickTimer.Stop();
            Point point = Cursor.Position;
            Size threshold = SystemInformation.DragSize;
            dragArea = new Rectangle(point.X - threshold.Width / 2, point.Y - threshold.Height / 2,
                threshold.Width, threshold.Height);
        };
        tile.MouseMove += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || !tile.Capture || draggingItem || dragArea.Contains(Cursor.Position))
                return;

            draggingItem = true;
            tile.Cursor = Cursor.Current = Cursors.SizeAll;
        };
        tile.Click += (_, _) =>
        {
            if (!draggingItem)
                Launch(resolveShortcutPath(path));
        };
        tile.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && draggingItem && expanded && !IsAnimating &&
                !Bounds.Contains(Cursor.Position))
                BeginInvoke(() => RemoveShortcut(path));
        };
        tile.MouseCaptureChanged += (_, _) =>
        {
            if (tile.Capture || !draggingItem)
                return;

            draggingItem = false;
            tile.Cursor = Cursors.Hand;
            Cursor.Current = tile.ClientRectangle.Contains(tile.PointToClient(Cursor.Position))
                ? Cursors.Hand : Cursors.Default;
        };
    }

    private void RemoveShortcut(string path)
    {
        string[] remaining = folder.Shortcuts.Where(item => item != path).ToArray();
        if (remaining.Length == folder.Shortcuts.Length)
            return;

        var names = folder.ShortcutNames is null ? null : new Dictionary<string, string>(folder.ShortcutNames);
        names?.Remove(path);
        if (!Save(folder with { Shortcuts = remaining, ShortcutNames = names is { Count: > 0 } ? names : null }))
            return;

        if (icons.Remove(path, out var icon))
            icon.Dispose();
        RefreshContents();
    }

    private void AcceptItems(Control target)
    {
        target.AllowDrop = true;
        target.DragEnter += (_, e) => e.Effect =
            e.AllowedEffect.HasFlag(DragDropEffects.Copy) && DroppedItems(e.Data).Any()
                ? DragDropEffects.Copy : DragDropEffects.None;
        target.DragDrop += (_, e) =>
        {
            if (!e.AllowedEffect.HasFlag(DragDropEffects.Copy))
                return;
            string[] dropped = DroppedItems(e.Data).ToArray();
            e.Effect = dropped.Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
            var shortcuts = folder.Shortcuts.Concat(dropped)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (shortcuts.Length > folder.Shortcuts.Length && Save(folder with { Shortcuts = shortcuts }))
                BeginInvoke(RefreshContents);
        };
    }

    private static IEnumerable<string> DroppedItems(IDataObject? data) =>
        (data?.GetData(DataFormats.FileDrop, autoConvert: false) as string[] ?? [])
            .Where(path => File.Exists(path) || Directory.Exists(path));

    private void RefreshContents()
    {
        preview.Invalidate();
        if (!expanded || (displayedShortcuts == folder.Shortcuts && displayedAppearance == Appearance))
            return;

        if (nameEditor is not null && !EditingTitle)
            FinishNameEdit(nameEditor);
        displayedShortcuts = folder.Shortcuts;
        displayedAppearance = Appearance;
        contents.SuspendLayout();
        try
        {
            while (contents.Controls.Count > 0)
                contents.Controls[0].Dispose();

            if (folder.Shortcuts.Length == 0)
            {
                var hint = new Label { AutoSize = true, Text = "拖入文件、文件夹或快捷方式", ForeColor = ExpandedTextColor, Margin = new Padding(8) };
                AcceptItems(hint);
                contents.Controls.Add(hint);
                return;
            }

            foreach (string path in folder.Shortcuts)
            {
                var tile = MakeButton();
                tile.Size = new Size(67, 65);
                tile.Margin = new Padding(2);
                tile.Tag = path;
                UpdateShortcutLabel(tile, path);
                tile.Paint += (_, e) =>
                {
                    e.Graphics.DrawImage(GetIcon(path), GetShortcutIconBounds(tile.ClientRectangle));
                    DrawShortcutName(e.Graphics, tile.AccessibleName!, tile.ClientRectangle, tile.ForeColor);
                };
                AttachShortcutDrag(tile, path);
                var actions = new ContextMenuStrip();
                actions.Items.Add("重命名", null, (_, _) => BeginInvoke(() => BeginShortcutRename(tile, path)));
                tile.ContextMenuStrip = actions;
                tile.Disposed += (_, _) => actions.Dispose();
                AcceptItems(tile);
                contents.Controls.Add(tile);
            }
        }
        finally
        {
            contents.ResumeLayout();
            if (IsAnimating)
                CaptureTransitionShortcuts();
        }
    }

    private string GetShortcutName(string path) =>
        folder.ShortcutNames?.TryGetValue(path, out var name) == true && !string.IsNullOrWhiteSpace(name)
            ? name : GetShortcutFileName(path);

    private static string GetShortcutFileName(string path)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (name.Length == 0)
            return path;

        string extension = Path.GetExtension(name);
        bool isShortcut = extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".url", StringComparison.OrdinalIgnoreCase);
        return isShortcut && !Directory.Exists(path) ? Path.GetFileNameWithoutExtension(name) : name;
    }

    private void UpdateShortcutLabel(Button tile, string path)
    {
        string name = GetShortcutName(path);
        tile.AccessibleName = name;
        tips.SetToolTip(tile, name);
        tile.Invalidate();
    }

    private static Rectangle GetShortcutIconBounds(Rectangle tile) => new(
        tile.Left + (tile.Width - ShortcutIconSize) / 2, tile.Top + 5, ShortcutIconSize, ShortcutIconSize);

    private void DrawShortcutName(Graphics graphics, string name, Rectangle tile, Color color)
    {
        TextFormatFlags alignment = Appearance.ExpandedAlignment switch
        {
            HorizontalAlignment.Left => TextFormatFlags.Left,
            HorizontalAlignment.Right => TextFormatFlags.Right,
            _ => TextFormatFlags.HorizontalCenter
        };
        TextRenderer.DrawText(graphics, name, Font, GetShortcutLabelBounds(tile), color, alignment | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
            TextFormatFlags.PreserveGraphicsClipping);
    }

    private static Rectangle GetShortcutLabelBounds(Rectangle tile) =>
        new(tile.Left + 2, tile.Top + 39, tile.Width - 4, 20);

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
            double opacity = Math.Max(0, 1 - expansion * 2);
            if (opacity > 0)
                TextRenderer.DrawText(e.Graphics, "＋", Font, preview.ClientRectangle, FadeText(Color.Black, opacity),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        else if (IsAnimating)
            PaintTransitionShortcuts(e.Graphics);
        else
        {
            Rectangle[] thumbnails = GetThumbnailBounds(preview.Width);
            for (int i = 0; i < thumbnails.Length; i++)
                e.Graphics.DrawImage(GetIcon(folder.Shortcuts[i]), thumbnails[i]);
        }
        DrawOutline(e.Graphics, preview.ClientRectangle);
    }

    private Rectangle[] GetThumbnailBounds(int side)
    {
        int count = Math.Min(folder.Shortcuts.Length, 9);
        int columns = count > 4 ? 3 : count > 1 ? 2 : 1;
        int cell = Math.Max(1, Math.Min(DesktopIconSize + 4, (side - 12) / columns));
        int left = (side - Math.Min(count, columns) * cell) / 2;
        int top = (side - ((count + columns - 1) / columns) * cell) / 2;
        int inset = cell > 8 ? 2 : 0;
        int iconSide = cell - inset * 2;
        var bounds = new Rectangle[count];
        for (int i = 0; i < count; i++)
            bounds[i] = new Rectangle(left + i % columns * cell + inset, top + i / columns * cell + inset,
                iconSide, iconSide);
        return bounds;
    }

    private void CaptureTransitionShortcuts()
    {
        Rectangle[] thumbnails = GetThumbnailBounds(GetFolderBounds(folder.Location, false).Width);
        expandedViewport = contents.ClientRectangle;
        expandedViewport.Offset(contents.Location);
        var shortcuts = new List<TransitionShortcut>();
        for (int i = 0; i < folder.Shortcuts.Length; i++)
        {
            var thumbnail = i < thumbnails.Length ? thumbnails[i] : Rectangle.Empty;
            var tile = Rectangle.Empty;
            string name = "";
            if (i < contents.Controls.Count && contents.Controls[i] is Button button)
            {
                var bounds = button.Bounds;
                bounds.Offset(contents.Location);
                if (expandedViewport.IntersectsWith(bounds))
                {
                    tile = bounds;
                    name = button.AccessibleName!;
                }
            }
            if (!thumbnail.IsEmpty || !tile.IsEmpty)
                shortcuts.Add(new TransitionShortcut(folder.Shortcuts[i], name, thumbnail, tile));
        }
        transitionShortcuts = shortcuts.ToArray();
    }

    private void PaintTransitionShortcuts(Graphics graphics)
    {
        double reveal = Math.Clamp((expansion - 0.30) / 0.65, 0, 1);
        double labelOpacity = Math.Clamp((expansion - 0.60) / 0.38, 0, 1);
        Color labelColor = FadeText(ExpandedTextColor, labelOpacity);
        var state = graphics.Save();
        graphics.IntersectClip(Lerp(preview.ClientRectangle, expandedViewport, expansion));
        using var fade = new ImageAttributes();
        foreach (var shortcut in transitionShortcuts)
        {
            bool hasTile = !shortcut.Tile.IsEmpty;
            Rectangle target = hasTile ? GetShortcutIconBounds(shortcut.Tile) : shortcut.Thumbnail;
            Rectangle icon = shortcut.Thumbnail.IsEmpty ? target : Lerp(shortcut.Thumbnail, target, expansion);
            double opacity = shortcut.Thumbnail.IsEmpty ? reveal : hasTile ? 1 : 1 - reveal;
            if (opacity >= 1)
                graphics.DrawImage(GetIcon(shortcut.Path), icon);
            else if (opacity > 0)
            {
                Bitmap image = GetIcon(shortcut.Path);
                fade.SetColorMatrix(IconFade(Surface, (float)opacity));
                graphics.DrawImage(image, icon, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, fade);
            }

            if (hasTile && labelOpacity > 0)
            {
                Rectangle tile = shortcut.Tile;
                tile.Offset(icon.Left - target.Left, icon.Top - target.Top);
                DrawShortcutName(graphics, shortcut.Name, tile, labelColor);
            }
        }
        graphics.Restore(state);
    }

    private Color FadeText(Color color, double opacity) => Color.FromArgb(
        Lerp(Surface.R, color.R, opacity), Lerp(Surface.G, color.G, opacity), Lerp(Surface.B, color.B, opacity));

    private static ColorMatrix IconFade(Color surface, float opacity)
    {
        // Blend toward the card surface without blending into the foreground window's transparency key.
        float backgroundWeight = (1 - opacity) / 255;
        return new ColorMatrix
        {
            Matrix00 = opacity, Matrix11 = opacity, Matrix22 = opacity,
            Matrix40 = surface.R * backgroundWeight,
            Matrix41 = surface.G * backgroundWeight,
            Matrix42 = surface.B * backgroundWeight
        };
    }

    private Bitmap GetIcon(string path)
    {
        if (icons.TryGetValue(path, out var cached))
            return cached;
        const uint systemIconIndex = 0x4000; // SHGFI_SYSICONINDEX
        IntPtr imageList = SHGetFileInfoW(resolveShortcutPath(path), 0, out var info,
            (uint)Marshal.SizeOf<ShellFileInfo>(), systemIconIndex);
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

    private void BeginRename()
    {
        if (!expanded || IsAnimating)
        {
            renameWhenExpanded = true;
            if (!expanded)
                Toggle();
            return;
        }

        renameWhenExpanded = false;
        BeginNameEdit(this, folder.Name, 48, "文件夹名称", name =>
        {
            if (name != folder.Name && Save(folder with { Name = name }))
                UpdateTitle();
        });
    }

    private void BeginShortcutRename(Button tile, string path)
    {
        if (tile.IsDisposed || !tile.Visible || IsAnimating)
            return;

        BeginNameEdit(tile, GetShortcutName(path), 255, "快捷方式名称", name => SaveShortcutName(path, name));
    }

    private void SaveShortcutName(string path, string name)
    {
        if (name == GetShortcutName(path) || !folder.Shortcuts.Contains(path))
            return;

        Dictionary<string, string> names = folder.ShortcutNames is null ? new() : new(folder.ShortcutNames);
        if (name == GetShortcutFileName(path))
            names.Remove(path);
        else
            names[path] = name;

        if (Save(folder with { ShortcutNames = names.Count > 0 ? names : null }))
        {
            foreach (Button tile in contents.Controls.OfType<Button>().Where(tile => (string?)tile.Tag == path))
                UpdateShortcutLabel(tile, path);
        }
    }

    private void BeginNameEdit(Control host, string name, int maxLength, string accessibleName, Action<string> apply)
    {
        headerClickTimer.Stop();
        if (nameEditor is not null)
        {
            if (nameEditor.Parent == host)
            {
                nameEditor.Focus();
                return;
            }
            FinishNameEdit(nameEditor);
        }

        var editor = new TextBox { Text = name, MaxLength = maxLength, AccessibleName = accessibleName };
        nameEditor = editor;
        editor.KeyDown += (_, e) =>
        {
            if (e.KeyCode is not (Keys.Enter or Keys.Escape))
                return;
            e.SuppressKeyPress = true;
            FinishNameEdit(editor, e.KeyCode == Keys.Enter ? apply : null);
        };
        editor.LostFocus += (_, _) => FinishNameEdit(editor, apply);
        host.Controls.Add(editor);
        if (host == this)
        {
            header.Hide();
            PositionTitleEditor(editor);
        }
        else
        {
            editor.Bounds = GetShortcutLabelBounds(host.ClientRectangle);
        }
        editor.BringToFront();
        Activate();
        editor.Focus();
        editor.SelectAll();
    }

    private void PositionTitleEditor(TextBox editor)
    {
        int height = editor.PreferredHeight;
        editor.Bounds = new Rectangle(header.Left, header.Top + (header.Height - height) / 2, header.Width, height);
    }

    private void FinishNameEdit(TextBox editor, Action<string>? apply = null)
    {
        if (nameEditor != editor)
            return;

        bool editingTitle = EditingTitle;
        nameEditor = null;
        string name = editor.Text.Trim();
        editor.Dispose();
        if (name.Length > 0)
            apply?.Invoke(name);
        if (editingTitle)
            header.Visible = expanded && !IsAnimating;
        RestoreDesktopOrder();
    }

    protected override void Dispose(bool disposing)
    {
        activeTransition = null;
        nameEditor = null;
        base.Dispose(disposing);
        if (disposing)
        {
            headerClickTimer.Dispose();
            tips.Dispose();
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

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmFlush();

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
