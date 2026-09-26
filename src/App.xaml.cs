using System.Windows;
using System.Windows.Threading;
using ACRSaveManager.UI;

namespace ACRSaveManager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Last-chance handler: without it, any uncaught UI-thread exception kills
        // the process with no message at all. Show the error and keep the app
        // alive when the dispatcher survives. (The app starts no background task,
        // so there is no task exception to catch.)
        DispatcherUnhandledException += OnDispatcherException;

        new MainWindow().Show();
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Dialogs.Error("Unexpected error:\n\n" + e.Exception.Message, "ACR Save Manager");

        // Keeping the process alive only helps while there is a window to carry
        // on with. A failure before the main window was shown leaves nothing on
        // screen, and the process would run on invisibly until killed.
        if (!Windows.OfType<Window>().Any(w => w.IsVisible))
            Shutdown(1);
    }
}
