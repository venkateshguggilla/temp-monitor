using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace TempMonitor.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Closing the window exits the app; minimizing hides it to the tray.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var window = new MainWindow();
            window.Closed += (_, _) => desktop.Shutdown();
            desktop.Exit += (_, _) => window.Shutdown();

            // Started at sign-in: live in the tray until the user opens the window.
            // (The lifetime shows MainWindow itself, so leave it unset to start hidden.)
            if (desktop.Args?.Contains(Program.MinimizedArg) != true)
                desktop.MainWindow = window;

            Platform.Current.ListenForActivation(() => Dispatcher.UIThread.Post(window.ShowFromTray));
        }
        base.OnFrameworkInitializationCompleted();
    }
}
