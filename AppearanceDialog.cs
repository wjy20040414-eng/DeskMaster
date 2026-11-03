namespace DeskMaster;

internal sealed class AppearanceDialog : Form
{
    private readonly Button background;
    private readonly Button borderColor;
    private readonly Button captionColor;
    private readonly Button expandedText;
    private readonly NumericUpDown opacity;
    private readonly CheckBox defaultOpacity;
    private readonly NumericUpDown thickness;
    private readonly ComboBox alignment;

    public AppearanceDialog(FolderAppearance current)
    {
        Text = "文件夹外观";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = ShowInTaskbar = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Microsoft YaHei UI", 9);

        var layout = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2, Padding = new Padding(12)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));

        background = ColorButton(current.Background);
        borderColor = ColorButton(current.BorderColor);
        captionColor = ColorButton(current.CaptionColor);
        expandedText = ColorButton(current.ExpandedTextColor);
        opacity = Number(20, 100, current.OpacityPercent ?? 62);
        defaultOpacity = new CheckBox { Text = "使用默认值", AutoSize = true, Checked = current.OpacityPercent is null };
        opacity.Enabled = !defaultOpacity.Checked;
        defaultOpacity.CheckedChanged += (_, _) => opacity.Enabled = !defaultOpacity.Checked;
        var opacityOptions = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        opacityOptions.Controls.AddRange([opacity, defaultOpacity]);
        thickness = Number(0, 6, current.BorderThickness);
        alignment = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 116 };
        alignment.Items.AddRange(["左对齐", "居中", "右对齐"]);
        alignment.SelectedIndex = current.ExpandedAlignment switch
        {
            HorizontalAlignment.Left => 0,
            HorizontalAlignment.Right => 2,
            _ => 1
        };

        AddRow(layout, "背景颜色", background);
        AddRow(layout, "背景不透明度（20–100%）", opacityOptions);
        AddRow(layout, "边框颜色", borderColor);
        AddRow(layout, "边框粗细（0–6 像素）", thickness);
        AddRow(layout, "收起时文字颜色", captionColor);
        AddRow(layout, "展开时文字颜色", expandedText);
        AddRow(layout, "展开时文字对齐", alignment);

        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
        var actions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        actions.Controls.AddRange([cancel, ok]);
        int row = layout.RowCount++;
        layout.Controls.Add(actions, 0, row);
        layout.SetColumnSpan(actions, 2);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public FolderAppearance Selection => new(
        Rgb(background.BackColor),
        defaultOpacity.Checked ? null : (int)opacity.Value,
        Rgb(borderColor.BackColor),
        (int)thickness.Value,
        Rgb(captionColor.BackColor),
        Rgb(expandedText.BackColor),
        alignment.SelectedIndex switch
        {
            0 => HorizontalAlignment.Left,
            2 => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Center
        });

    internal static Color ToColor(int rgb) => Color.FromArgb(unchecked((int)0xFF000000) | (rgb & 0xFFFFFF));

    private static int Rgb(Color color) => color.ToArgb() & 0xFFFFFF;

    private Button ColorButton(int rgb)
    {
        var button = new Button { Width = 116, BackColor = ToColor(rgb), UseVisualStyleBackColor = false };
        void UpdateSample(Color color)
        {
            button.BackColor = color;
            button.ForeColor = color.GetBrightness() < 0.5 ? Color.White : Color.Black;
            button.Text = $"#{Rgb(color):X6}";
        }
        UpdateSample(button.BackColor);
        button.Click += (_, _) =>
        {
            using var picker = new ColorDialog { Color = button.BackColor, FullOpen = true };
            if (picker.ShowDialog(this) == DialogResult.OK)
                UpdateSample(picker.Color);
        };
        return button;
    }

    private static NumericUpDown Number(int minimum, int maximum, int value) =>
        new() { Minimum = minimum, Maximum = maximum, Value = Math.Clamp(value, minimum, maximum), Width = 60 };

    private static void AddRow(TableLayoutPanel layout, string text, Control input)
    {
        int row = layout.RowCount++;
        layout.Controls.Add(new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        layout.Controls.Add(input, 1, row);
    }
}
