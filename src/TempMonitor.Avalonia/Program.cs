using Avalonia;

namespace TempMonitor.App;

internal static class Program
{
    public const string MinimizedArg = "--minimized";

    [STAThread]
    public static int Main(string[] args)
    {
        // One copy only: a second launch asks the first one to show its window, then exits.
        if (!Platform.Current.TryAcquireSingleInstance(waitForPrevious: args.Contains(Platform.RelaunchArg)))
        {
            Platform.Current.SignalFirstInstance();
            return 0;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the XAML previewer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
