using System.Runtime.InteropServices;
using Avalonia;

namespace MirrorGrab;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        using var mutex = new Mutex(true, "MirrorGrab_SingleInstance", out bool created);
        if (!created)
        {
            NotifyAlreadyRunning();
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void NotifyAlreadyRunning()
    {
        // Can't show an Avalonia dialog here — the app isn't initialized.
        // On Windows, use MessageBox; elsewhere, fall back to stderr.
        if (OperatingSystem.IsWindows())
        {
            MessageBoxW(IntPtr.Zero,
                "MirrorGrab is already running.",
                "MirrorGrab",
                0x40 /* MB_ICONINFORMATION */);
        }
        else
        {
            Console.Error.WriteLine("MirrorGrab is already running.");
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
