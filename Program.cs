namespace DeskMaster;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        FolderCommand command = args switch
        {
            ["--new-folder"] => FolderCommand.Create,
            ["--exit"] => FolderCommand.Exit,
            _ => FolderCommand.Show
        };
        if (args is ["--install-desktop-menu"] or ["--remove-desktop-menu"] or
                    ["--install-desktop-menu-system"] or ["--remove-desktop-menu-system"])
        {
            try
            {
                DesktopMenu.Configure(
                    install: args[0] is "--install-desktop-menu" or "--install-desktop-menu-system",
                    machineWide: args[0] is "--install-desktop-menu-system" or "--remove-desktop-menu-system");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                MessageBox.Show($"无法更新桌面右键菜单。\n\n{error.Message}", "DeskMaster",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                Environment.ExitCode = 1;
            }
            return;
        }

        using var instance = new Mutex(true, @"Local\DeskMaster", out bool firstInstance);
        if (!firstInstance)
        {
            if (!FolderCommandWindow.Post(command))
            {
                MessageBox.Show("无法连接到正在运行的 DeskMaster。请退出后重试。", "DeskMaster",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Environment.ExitCode = 1;
            }
            return;
        }

        if (command == FolderCommand.Exit)
            return;

        ApplicationConfiguration.Initialize();
        try
        {
            using var application = new DesktopApplication(FolderStore.ForCurrentUser());
            void RunCommand(FolderCommand request)
            {
                switch (request)
                {
                    case FolderCommand.Create: application.CreateFolder(); break;
                    case FolderCommand.Show: application.ShowFolders(); break;
                    case FolderCommand.Exit: application.ExitThread(); break;
                }
            }
            using var commands = new FolderCommandWindow(RunCommand);
            if (command != FolderCommand.Show)
                RunCommand(command);
            Application.Run(application);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            MessageBox.Show($"无法读取桌面文件夹配置。\n\n{error.Message}", "DeskMaster",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
