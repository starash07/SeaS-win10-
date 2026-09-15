using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace SeaS.Installer;

public partial class MainWindow : Window
{
    private bool _isInitializing = true;
    private bool _isInstalling;
    private bool _autoStartElevatedInstall;
    private bool _quietInstall;
    private bool _quietInstallCompleted;
    private InstallResult? _installResult;

    public MainWindow()
        : this(InstallerEngine.ReadRegisteredInstallations())
    {
    }

    internal MainWindow(IReadOnlyList<RegisteredInstallation> registrations)
    {
        InitializeComponent();
        InstallerVersionText.Text = GetVersionText();
        InstallPathTextBox.Text = InstallerEngine.GetDefaultInstallDirectory(InstallScope.CurrentUser);
        ApplyCommandLineOptions();
        _isInitializing = false;
        try
        {
            ApplyRegisteredInstallation(registrations);
        }
        catch (Exception exception)
        {
            SetupDescriptionText.Text = exception.Message;
            StartInstallButton.IsEnabled = false;
        }
        Loaded += MainWindow_Loaded;
    }

    private static string GetVersionText()
    {
        var version = typeof(MainWindow).Assembly.GetName().Version;
        return version is null ? "V1.1.0" : $"V{version.Major}.{version.Minor}.{version.Build}";
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_autoStartElevatedInstall)
        {
            return;
        }

        _autoStartElevatedInstall = false;
        if (_quietInstall)
        {
            Hide();
            ShowInTaskbar = false;
        }

        await BeginInstallAsync();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isInstalling)
        {
            Close();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_isInstalling)
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    private void CompleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (LaunchAfterInstallCheckBox.IsChecked == true && _installResult is not null)
        {
            TryLaunchInstalledApp(_installResult.ExecutablePath);
        }

        Close();
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var currentPath = InstallPathTextBox.Text;
        var initialDirectory = Directory.Exists(currentPath)
            ? currentPath
            : Path.GetDirectoryName(currentPath);
        var dialog = new OpenFolderDialog
        {
            Title = "选择 SeaS 安装位置",
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : null,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            InstallPathTextBox.Text = InstallerEngine.NormalizeInstallDirectory(dialog.FolderName);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "SeaS 安装", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void InstallScopeRadioButton_Checked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || InstallPathTextBox is null)
        {
            return;
        }

        InstallPathTextBox.Text = InstallerEngine.GetDefaultInstallDirectory(GetSelectedScope());
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        await BeginInstallAsync();
    }

    private async Task BeginInstallAsync(bool allowDowngrade = false)
    {
        if (_isInstalling)
        {
            return;
        }

        InstallOptions options;
        try
        {
            // Recheck immediately before installation in case registrations changed
            // while the setup window was open.
            options = ApplyRegisteredInstallation(InstallerEngine.ReadRegisteredInstallations(), allowDowngrade);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "SeaS 安装", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (options.Scope == InstallScope.AllUsers && !IsRunningAsAdministrator())
        {
            if (TryRelaunchElevated(options))
            {
                Close();
            }

            return;
        }

        var runningProcesses = InstallerEngine.FindRunningComponents(options.InstallDirectory);
        if (runningProcesses.Count > 0)
        {
            foreach (var process in runningProcesses)
            {
                process.Dispose();
            }

            var result = MessageBox.Show(
                this,
                "安装位置中的 SeaS 或内置 LittleFish 正在运行。\n\n继续安装会先关闭这些窗口，不会影响单独安装的 LittleFish。",
                "SeaS 安装",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Information);
            if (result != MessageBoxResult.OK)
            {
                return;
            }
        }

        _isInstalling = true;
        InstallProgressBar.Value = 0;
        ProgressStatusText.Text = "检查安装设置…";
        ShowPage(SetupPage, ProgressPage);
        var progress = new Progress<InstallProgress>(update =>
        {
            InstallProgressBar.Value = update.Value;
            ProgressStatusText.Text = update.Status;
        });

        try
        {
            _installResult = await InstallerEngine.InstallAsync(options, progress);
            ShowPage(ProgressPage, CompletePage);
            _quietInstallCompleted = _quietInstall;
        }
        catch (NewerVersionInstalledException exception)
        {
            ShowPage(ProgressPage, SetupPage);
            var result = MessageBox.Show(
                this,
                $"已经安装 SeaS {exception.InstalledVersion}，当前安装包为 {GetVersionText()}。\n\n是否仍要继续降级安装？",
                "SeaS 安装",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            _isInstalling = false;
            if (result == MessageBoxResult.Yes)
            {
                await BeginInstallAsync(allowDowngrade: true);
            }
        }
        catch (Exception exception)
        {
            ShowPage(ProgressPage, SetupPage);
            MessageBox.Show(
                this,
                $"SeaS 安装没有完成。\n\n{exception.Message}",
                "SeaS 安装",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _isInstalling = false;
            if (_quietInstallCompleted)
            {
                Close();
            }
        }
    }

    private InstallScope GetSelectedScope()
    {
        return AllUsersRadioButton.IsChecked == true ? InstallScope.AllUsers : InstallScope.CurrentUser;
    }

    private InstallOptions ApplyRegisteredInstallation(
        IReadOnlyList<RegisteredInstallation> registrations,
        bool allowDowngrade = false)
    {
        var requestedOptions = new InstallOptions(
            GetSelectedScope(),
            InstallPathTextBox.Text,
            CreateDesktopShortcutCheckBox.IsChecked == true,
            CreateStartMenuShortcutCheckBox.IsChecked == true,
            allowDowngrade);
        var options = InstallerEngine.ResolveRegisteredInstallation(requestedOptions, registrations);
        ApplyResolvedInstallationOptions(options);

        InstallScopeOptions.IsEnabled = !options.IsExistingInstallation;
        InstallPathTextBox.IsReadOnly = options.IsExistingInstallation;
        BrowseInstallPathButton.IsEnabled = !options.IsExistingInstallation;
        SetupTitleText.Text = options.IsExistingInstallation ? "准备更新 SeaS" : "准备安装 SeaS";
        SetupDescriptionText.Text = options.IsExistingInstallation
            ? "已找到 SeaS，将在原位置更新并保留书架和设置。"
            : "确认安装位置和快捷方式，然后开始安装。";
        StartInstallButton.Content = options.IsExistingInstallation ? "开始更新" : "开始安装";
        return options;
    }

    private void ApplyResolvedInstallationOptions(InstallOptions options)
    {
        _isInitializing = true;
        try
        {
            AllUsersRadioButton.IsChecked = options.Scope == InstallScope.AllUsers;
            CurrentUserRadioButton.IsChecked = options.Scope == InstallScope.CurrentUser;
            InstallPathTextBox.Text = options.InstallDirectory;
        }
        finally
        {
            _isInitializing = false;
        }
    }

    internal static void RunInstallationDetectionSelfTests()
    {
        var fresh = new MainWindow([]);
        try
        {
            if (!fresh.InstallScopeOptions.IsEnabled || fresh.InstallPathTextBox.IsReadOnly
                || !fresh.BrowseInstallPathButton.IsEnabled || !fresh.StartInstallButton.IsEnabled
                || fresh.InstallPathTextBox.Text != InstallerEngine.GetDefaultInstallDirectory(InstallScope.CurrentUser)
                || (string)fresh.StartInstallButton.Content != "开始安装")
            {
                throw new InvalidOperationException("全新安装界面自检失败。");
            }
        }
        finally
        {
            fresh.Close();
        }

        foreach (var scope in new[] { InstallScope.CurrentUser, InstallScope.AllUsers })
        {
            var upgrade = new MainWindow([new RegisteredInstallation(scope, @"D:\Reading Apps\SeaS Reader", "1.0.1")]);
            try
            {
                if (upgrade.GetSelectedScope() != scope
                    || upgrade.InstallPathTextBox.Text != @"D:\Reading Apps\SeaS Reader"
                    || upgrade.InstallScopeOptions.IsEnabled || !upgrade.InstallPathTextBox.IsReadOnly
                    || upgrade.BrowseInstallPathButton.IsEnabled || !upgrade.StartInstallButton.IsEnabled
                    || upgrade.SetupTitleText.Text != "准备更新 SeaS"
                    || (string)upgrade.StartInstallButton.Content != "开始更新")
                {
                    throw new InvalidOperationException("启动时识别已有安装的界面自检失败。");
                }
            }
            finally
            {
                upgrade.Close();
            }
        }

        var conflicting = new MainWindow([
            new RegisteredInstallation(InstallScope.CurrentUser, @"D:\SeaS", "1.0.1"),
            new RegisteredInstallation(InstallScope.AllUsers, @"C:\Program Files\SeaS", "1.0.1")
        ]);
        try
        {
            if (conflicting.StartInstallButton.IsEnabled
                || !conflicting.SetupDescriptionText.Text.Contains("多个不同位置"))
            {
                throw new InvalidOperationException("多位置安装提示自检失败。");
            }
        }
        finally
        {
            conflicting.Close();
        }
    }

    private void ApplyCommandLineOptions()
    {
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        var isElevatedInstall = args.Any(arg => string.Equals(arg, "--elevated-install", StringComparison.OrdinalIgnoreCase));
        _quietInstall = args.Any(arg => string.Equals(arg, "--quiet-install", StringComparison.OrdinalIgnoreCase));
        if (!isElevatedInstall && !_quietInstall)
        {
            return;
        }

        var requestedScope = isElevatedInstall ? "machine" : GetArgumentValue(args, "--scope");
        var useAllUsers = string.Equals(requestedScope, "machine", StringComparison.OrdinalIgnoreCase);
        AllUsersRadioButton.IsChecked = useAllUsers;
        CurrentUserRadioButton.IsChecked = !useAllUsers;
        var encodedPath = GetArgumentValue(args, "--path64");
        if (!string.IsNullOrWhiteSpace(encodedPath))
        {
            try
            {
                InstallPathTextBox.Text = Encoding.UTF8.GetString(Convert.FromBase64String(encodedPath));
            }
            catch (FormatException)
            {
                InstallPathTextBox.Text = InstallerEngine.GetDefaultInstallDirectory(
                    useAllUsers ? InstallScope.AllUsers : InstallScope.CurrentUser);
            }
        }

        CreateDesktopShortcutCheckBox.IsChecked = GetArgumentValue(args, "--desktop") != "0";
        CreateStartMenuShortcutCheckBox.IsChecked = GetArgumentValue(args, "--startmenu") != "0";
        _autoStartElevatedInstall = true;
    }

    private static string? GetArgumentValue(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private bool TryRelaunchElevated(InstallOptions options)
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            MessageBox.Show(this, "无法定位当前安装程序。", "SeaS 安装", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        var encodedPath = Convert.ToBase64String(Encoding.UTF8.GetBytes(options.InstallDirectory));
        var arguments = string.Join(' ',
            "--elevated-install",
            "--path64", encodedPath,
            "--desktop", options.CreateDesktopShortcut ? "1" : "0",
            "--startmenu", options.CreateStartMenuShortcut ? "1" : "0");
        try
        {
            Process.Start(new ProcessStartInfo(executablePath)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = arguments
            });
            return true;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return false;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"无法申请管理员权限。\n\n{exception.Message}", "SeaS 安装", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private static void TryLaunchInstalledApp(string executablePath)
    {
        try
        {
            if (IsRunningAsAdministrator())
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{executablePath}\"")
                {
                    UseShellExecute = true
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo(executablePath)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(executablePath)
                });
            }
        }
        catch
        {
        }
    }

    private static void ShowPage(UIElement currentPage, UIElement nextPage)
    {
        currentPage.Visibility = Visibility.Collapsed;
        currentPage.Opacity = 0;
        nextPage.Visibility = Visibility.Visible;

        var animation = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        nextPage.BeginAnimation(OpacityProperty, animation);
    }
}
