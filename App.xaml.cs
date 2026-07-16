using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppNotifications;

namespace DropDown;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    public Window MainWindow { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
        AppNotificationManager.Default.Register();

        MainWindow = new MainWindow();
        MainWindow.Closed += (_, _) => AppNotificationManager.Default.Unregister();
        MainWindow.Activate();
    }

    /// <summary>Handles the "Open Folder" button on a download-complete toast:
    /// fires even while the app is minimized/unfocused, so it can't route through
    /// MainWindow's UI thread state; just launches Explorer directly.</summary>
    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        if (args.Arguments.TryGetValue("action", out var action) && action == "openFolder"
            && args.Arguments.TryGetValue("folder", out var folder))
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
            }
            catch
            {
                // Best effort; the toast has already served its purpose either way
            }
        }
    }
}
