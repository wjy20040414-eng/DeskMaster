using System.Runtime.InteropServices;

namespace DeskMaster;

internal enum FolderCommand
{
    Create,
    Show,
    Exit
}

internal sealed class FolderCommandWindow : NativeWindow, IDisposable
{
    private const string WindowName = "DeskMaster.FolderCommands";
    private const int CommandMessage = 0x8001; // WM_APP + 1
    private static readonly IntPtr MessageOnly = new(-3); // HWND_MESSAGE
    private readonly Action<FolderCommand> runCommand;

    public FolderCommandWindow(Action<FolderCommand> runCommand)
    {
        this.runCommand = runCommand;
        CreateHandle(new CreateParams { Caption = WindowName, Parent = MessageOnly });
    }

    public static bool Post(FolderCommand command)
    {
        // A concurrent launch can acquire the instance mutex before its message window exists.
        for (int attempt = 0; attempt < 40; attempt++)
        {
            IntPtr window = FindWindowEx(MessageOnly, IntPtr.Zero, null, WindowName);
            if (window != IntPtr.Zero && PostMessage(window, CommandMessage, new IntPtr((int)command), IntPtr.Zero))
                return true;

            Thread.Sleep(50);
        }
        return false;
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == CommandMessage)
            runCommand((FolderCommand)message.WParam.ToInt32());
        else
            base.WndProc(ref message);
    }

    public void Dispose() => DestroyHandle();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string title);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
