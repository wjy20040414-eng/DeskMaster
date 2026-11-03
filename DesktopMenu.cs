using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DeskMaster;

internal static class DesktopMenu
{
    private const string KeyPrefix = @"Software\Classes\DesktopBackground\Shell\DeskMaster.";
    private static readonly (string Key, string Label, string Argument)[] Commands =
    [
        ("NewFolder", "新建桌面文件夹", "--new-folder"),
        ("ShowFolders", "显示桌面文件夹", "--show-folders"),
        ("Exit", "退出 DeskMaster", "--exit")
    ];

    public static void Configure(bool install)
    {
        if (install)
        {
            string executable = Path.Combine(AppContext.BaseDirectory, "DeskMaster.exe");
            if (!File.Exists(executable))
                throw new FileNotFoundException("找不到 DeskMaster.exe。", executable);

            foreach (var item in Commands)
            {
                using var entry = Registry.CurrentUser.CreateSubKey(KeyPrefix + item.Key);
                using var command = entry.CreateSubKey("command");
                command.SetValue("", $"\"{executable}\" {item.Argument}");
                entry.SetValue("", item.Label);
                entry.SetValue("Icon", $"\"{executable}\",0");
            }
        }
        else
            foreach (var item in Commands)
                Registry.CurrentUser.DeleteSubKeyTree(KeyPrefix + item.Key, throwOnMissingSubKey: false);

        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
