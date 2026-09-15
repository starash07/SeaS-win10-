using System.Windows;

namespace SeaS.Installer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Any(argument => string.Equals(
                argument,
                "--package-smoke-test",
                StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                InstallerEngine.RunPackageSelfTests();
                SeaS.Installer.MainWindow.RunInstallationDetectionSelfTests();
                Shutdown(0);
            }
            catch
            {
                Shutdown(1);
            }
            return;
        }

        base.OnStartup(e);
    }
}
