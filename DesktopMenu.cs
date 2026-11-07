using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DeskMaster;

internal static class DesktopMenu
{
    private const string DirectoryKeyPrefix = @"Software\Classes\Directory\Background\Shell\DeskMaster.";
    private const string DesktopKeyPrefix = @"Software\Classes\DesktopBackground\Shell\DeskMaster.";
    private const string RemovedShortcutCommand = "ColoredShortcut";
    private static readonly (string Key, string Label, string Argument)[] Commands =
    [
        ("NewFolder", "新建桌面文件夹", "--new-folder"),
        ("ShowFolders", "显示桌面文件夹", "--show-folders"),
        ("Exit", "退出 DeskMaster", "--exit")
    ];

    public static void Configure(bool install, bool machineWide = false)
    {
        RegistryKey hive = machineWide ? Registry.LocalMachine : Registry.CurrentUser;
        string prefix = machineWide ? DesktopKeyPrefix : DirectoryKeyPrefix;
        if (install)
        {
            string executable = Path.Combine(AppContext.BaseDirectory, "DeskMaster.exe");
            if (!File.Exists(executable))
                throw new FileNotFoundException("找不到 DeskMaster.exe。", executable);

            foreach (var item in Commands)
            {
                using var entry = hive.CreateSubKey(prefix + item.Key);
                using var command = entry.CreateSubKey("command");
                command.SetValue("", $"\"{executable}\" {item.Argument}");
                entry.SetValue("", item.Label);
                entry.SetValue("Icon", $"\"{executable}\",0");
            }
        }
        else
            foreach (var item in Commands)
                hive.DeleteSubKeyTree(prefix + item.Key, throwOnMissingSubKey: false);

        hive.DeleteSubKeyTree(prefix + RemovedShortcutCommand, throwOnMissingSubKey: false);
        if (!machineWide)
        {
            foreach (var item in Commands)
                hive.DeleteSubKeyTree(DesktopKeyPrefix + item.Key, throwOnMissingSubKey: false);
            hive.DeleteSubKeyTree(DesktopKeyPrefix + RemovedShortcutCommand, throwOnMissingSubKey: false);
        }

        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
