using System.Text.Json;

namespace DeskMaster;

internal sealed record DesktopFolder(Guid Id, string Name, Point Location, int IconColumns, string[] Shortcuts,
    FolderAppearance? Appearance = null);

internal sealed record FolderAppearance(
    int Background = 0xE8E8E8,
    int? OpacityPercent = null,
    int BorderColor = 0,
    int BorderThickness = 2,
    int CaptionColor = 0xFFFFFF,
    int ExpandedTextColor = 0,
    HorizontalAlignment ExpandedAlignment = HorizontalAlignment.Center);

internal sealed record AnimationSettings(int FrameRate = 0, int SpeedPercent = 100)
{
    internal static IReadOnlyList<int> FrameRates { get; } = [0, 30, 60, 90, 120, 144];
    internal static IReadOnlyList<int> Speeds { get; } = [50, 75, 100, 125, 150, 200];

    internal AnimationSettings Normalize() => new(
        FrameRates.Contains(FrameRate) ? FrameRate : 0,
        Speeds.Contains(SpeedPercent) ? SpeedPercent : 100);
}

internal sealed class FolderStore(string path)
{
    private sealed record Entry(Guid Id, string Name, int X, int Y, string[] Shortcuts, int IconColumns = 1,
        FolderAppearance? Appearance = null);

    private string AnimationPath => Path.Combine(Path.GetDirectoryName(path)!, "animation.json");

    public static FolderStore ForCurrentUser() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskMaster", "folders.json"));

    public DesktopFolder[] Load()
    {
        if (!File.Exists(path))
            return [];

        var entries = JsonSerializer.Deserialize<Entry[]>(File.ReadAllBytes(path))
            ?? throw new JsonException("缺少文件夹列表。");
        if (entries.Any(entry => entry is null || string.IsNullOrWhiteSpace(entry.Name) ||
                entry.IconColumns is < 1 or > 3 || entry.Shortcuts is null ||
                entry.Shortcuts.Any(shortcut => string.IsNullOrWhiteSpace(shortcut))) ||
            entries.Select(entry => entry.Id).Distinct().Count() != entries.Length)
            throw new JsonException("文件夹数据格式无效。");

        return entries.Select(entry => new DesktopFolder(entry.Id, entry.Name,
            new Point(entry.X, entry.Y), entry.IconColumns, entry.Shortcuts, entry.Appearance)).ToArray();
    }

    public void Save(IEnumerable<DesktopFolder> folders)
    {
        var entries = folders.Select(folder => new Entry(folder.Id, folder.Name,
            folder.Location.X, folder.Location.Y, folder.Shortcuts, folder.IconColumns, folder.Appearance));
        WriteJson(path, entries);
    }

    public AnimationSettings LoadAnimationSettings()
    {
        try
        {
            return JsonSerializer.Deserialize<AnimationSettings>(File.ReadAllBytes(AnimationPath))?.Normalize() ?? new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void SaveAnimationSettings(AnimationSettings settings) => WriteJson(AnimationPath, settings);

    private static void WriteJson<T>(string filePath, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        string temporaryPath = filePath + ".tmp";
        File.WriteAllBytes(temporaryPath, JsonSerializer.SerializeToUtf8Bytes(value));
        File.Move(temporaryPath, filePath, overwrite: true);
    }
}
