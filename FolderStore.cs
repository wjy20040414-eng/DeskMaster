using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskMaster;

internal sealed record DesktopFolder(Guid Id, string Name, Point Location, int IconColumns, string[] Shortcuts,
    FolderAppearance? Appearance = null, IReadOnlyDictionary<string, string>? ShortcutNames = null);

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
        FolderAppearance? Appearance = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, string>? ShortcutNames = null);

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
            new Point(entry.X, entry.Y), entry.IconColumns, entry.Shortcuts, entry.Appearance, entry.ShortcutNames)).ToArray();
    }

    public void Save(IEnumerable<DesktopFolder> folders)
    {
        var entries = folders.Select(folder => new Entry(folder.Id, folder.Name,
            folder.Location.X, folder.Location.Y, folder.Shortcuts, folder.IconColumns, folder.Appearance,
            ShortcutNames: folder.ShortcutNames));
        WriteJson(path, entries);
    }

    public void SaveChanges(DesktopFolder[] previous, DesktopFolder[] updated)
    {
        var createdCopies = new List<string>();
        try
        {
            foreach (var (folderId, source) in ShortcutsOnlyIn(updated, previous))
            {
                string? copy = ShortcutCopyPath(folderId, source);
                if (copy is null || Directory.Exists(source))
                    continue;

                CopyShortcut(source, copy);
                createdCopies.Add(copy);
            }
            Save(updated);
        }
        catch
        {
            foreach (string copy in createdCopies)
                DeleteCopyIfPossible(copy);
            throw;
        }

        foreach (var (folderId, source) in ShortcutsOnlyIn(previous, updated))
        {
            if (ShortcutCopyPath(folderId, source) is string copy)
                DeleteCopyIfPossible(copy);
        }
    }

    public void PreserveExistingShortcuts(IEnumerable<DesktopFolder> folders)
    {
        foreach (var folder in folders)
        foreach (string source in folder.Shortcuts)
        {
            string? copy = ShortcutCopyPath(folder.Id, source);
            if (copy is null || File.Exists(copy) || !File.Exists(source))
                continue;

            try
            {
                CopyShortcut(source, copy);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Older entries can still use their source when a copy cannot be saved.
            }
        }
    }

    public string ResolveShortcutPath(Guid folderId, string source) =>
        ShortcutCopyPath(folderId, source) is string copy && File.Exists(copy) ? copy : source;

    private string? ShortcutCopyPath(Guid folderId, string source)
    {
        string extension = Path.GetExtension(source);
        if (!extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
            return null;

        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
        return Path.Combine(Path.GetDirectoryName(path)!, "shortcuts", folderId.ToString("N"), key + extension);
    }

    private static IEnumerable<(Guid FolderId, string Source)> ShortcutsOnlyIn(
        IEnumerable<DesktopFolder> folders, IEnumerable<DesktopFolder> otherFolders)
    {
        var others = otherFolders.ToDictionary(folder => folder.Id);
        foreach (var folder in folders)
        {
            string[] other = others.GetValueOrDefault(folder.Id)?.Shortcuts ?? [];
            if (ReferenceEquals(folder.Shortcuts, other))
                continue;

            foreach (string source in folder.Shortcuts.Except(other, StringComparer.Ordinal))
                yield return (folder.Id, source);
        }
    }

    private static void CopyShortcut(string source, string copy)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        string temporaryPath = copy + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temporaryPath);
            FileAttributes attributes = File.GetAttributes(temporaryPath);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(temporaryPath, attributes & ~FileAttributes.ReadOnly);
            File.Move(temporaryPath, copy, overwrite: true);
        }
        finally
        {
            DeleteCopyIfPossible(temporaryPath);
        }
    }

    private static void DeleteCopyIfPossible(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Cleanup must not roll back an already saved folder.
        }
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
