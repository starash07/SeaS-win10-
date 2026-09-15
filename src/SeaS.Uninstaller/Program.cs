using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SeaS.Uninstaller;

internal static class Program
{
    private const string AppName = "SeaS";
    private const string ManifestFileName = ".seas-manifest.txt";
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SeaS";
    private const uint MbOk = 0x00000000;
    private const uint MbYesNo = 0x00000004;
    private const uint MbIconInformation = 0x00000040;
    private const uint MbIconWarning = 0x00000030;
    private const uint MbDefaultButton2 = 0x00000100;
    private const int IdYes = 6;
    private const uint MoveFileDelayUntilReboot = 0x00000004;

    private enum InstallDirectoryState
    {
        ManagedInstall,
        StaleRegistration
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(argument => string.Equals(argument, "--package-smoke-test", StringComparison.OrdinalIgnoreCase)))
        {
            return RunPackageSelfTests();
        }

        try
        {
            var installDirectory = GetArgumentValue(args, "--uninstall");
            if (string.IsNullOrWhiteSpace(installDirectory))
            {
                ShowMessage("未找到 SeaS 安装位置，无法开始卸载。", MbIconWarning);
                return 1;
            }

            installDirectory = Path.GetFullPath(installDirectory);
            var isMachineScope = string.Equals(GetArgumentValue(args, "--scope"), "machine", StringComparison.OrdinalIgnoreCase);
            var isTemporaryCopy = args.Any(argument => string.Equals(argument, "--temp", StringComparison.OrdinalIgnoreCase));
            var quiet = args.Any(argument => string.Equals(argument, "--quiet", StringComparison.OrdinalIgnoreCase));
            var requestedDeleteData = string.Equals(GetArgumentValue(args, "--delete-data"), "1", StringComparison.Ordinal);
            if (!isTemporaryCopy)
            {
                return ConfirmAndRelaunchFromTemp(installDirectory, isMachineScope, quiet, requestedDeleteData);
            }

            var parentIdText = GetArgumentValue(args, "--parent");
            if (int.TryParse(parentIdText, out var parentId))
            {
                WaitForParent(parentId);
            }

            var deleteData = string.Equals(GetArgumentValue(args, "--delete-data"), "1", StringComparison.Ordinal);
            Uninstall(installDirectory, isMachineScope, deleteData);
            if (!quiet)
            {
                ShowMessage("SeaS 已卸载完成。", MbIconInformation);
            }
            ScheduleTemporaryCopyCleanup();
            return 0;
        }
        catch (Exception exception)
        {
            ShowMessage($"SeaS 卸载没有完成。\n\n{exception.Message}", MbIconWarning);
            return 1;
        }
    }

    private static int ConfirmAndRelaunchFromTemp(
        string installDirectory,
        bool isMachineScope,
        bool quiet,
        bool requestedDeleteData)
    {
        var installState = ValidateInstallDirectory(installDirectory);
        if (!quiet)
        {
            var confirmation = MessageBoxW(
                IntPtr.Zero,
                installState == InstallDirectoryState.ManagedInstall
                    ? "确定要卸载 SeaS 吗？\n\n默认会保留书架、阅读进度和本地设置。"
                    : "SeaS 程序文件已经不存在。是否清理残留的卸载记录和快捷方式？",
                "卸载 SeaS",
                MbYesNo | MbIconWarning | MbDefaultButton2);
            if (confirmation != IdYes)
            {
                return 0;
            }
        }

        var deleteData = requestedDeleteData;
        if (!quiet)
        {
            var deleteDataChoice = MessageBoxW(
                IntPtr.Zero,
                "是否同时删除当前用户的书架、阅读进度和本地设置？\n\n不会删除用户导入的原始书籍文件。\n选择“否”将保留这些数据。",
                "卸载 SeaS",
                MbYesNo | MbIconWarning | MbDefaultButton2);
            deleteData = deleteDataChoice == IdYes;
        }

        var currentExecutable = Environment.ProcessPath
                                ?? throw new InvalidOperationException("无法定位 SeaS 卸载程序。");
        var tempDirectory = Path.Combine(Path.GetTempPath(), "SeaS.Uninstall", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var tempExecutable = Path.Combine(tempDirectory, "SeaS_Uninstall.exe");
        File.Copy(currentExecutable, tempExecutable, overwrite: true);

        var startInfo = new ProcessStartInfo(tempExecutable)
        {
            UseShellExecute = true,
            Verb = isMachineScope ? "runas" : string.Empty,
            Arguments = string.Join(' ',
                "--temp",
                "--uninstall", QuoteArgument(installDirectory),
                "--scope", isMachineScope ? "machine" : "user",
                "--delete-data", deleteData ? "1" : "0",
                quiet ? "--quiet" : string.Empty,
                "--parent", Environment.ProcessId.ToString())
        };
        Process.Start(startInfo);
        return 0;
    }

    private static void Uninstall(string installDirectory, bool isMachineScope, bool deleteData)
    {
        var installState = ValidateInstallDirectory(installDirectory);
        if (installState == InstallDirectoryState.ManagedInstall)
        {
            CloseInstalledProcesses(installDirectory);
        }
        DeleteShortcuts(isMachineScope);
        DeleteUninstallRegistryEntry(isMachineScope);
        if (installState == InstallDirectoryState.ManagedInstall)
        {
            DeleteManagedFiles(installDirectory);
        }
        else
        {
            TryDeleteFile(Path.Combine(installDirectory, "SeaS_Uninstall.exe"));
            TryDeleteDirectory(installDirectory, recursive: false);
        }

        if (deleteData)
        {
            DeleteCurrentUserData();
        }
    }

    private static InstallDirectoryState ValidateInstallDirectory(string installDirectory)
    {
        var root = Path.GetPathRoot(installDirectory);
        if (string.IsNullOrWhiteSpace(root)
            || string.Equals(
                installDirectory.TrimEnd(Path.DirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("卸载目录无效。");
        }

        if (!string.Equals(Path.GetFileName(installDirectory.TrimEnd(Path.DirectorySeparatorChar)), AppName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("目标文件夹不是 SeaS 安装目录，已停止卸载。");
        }

        if (!File.Exists(Path.Combine(installDirectory, ManifestFileName)))
        {
            if (File.Exists(Path.Combine(installDirectory, "SeaS.exe"))
                || File.Exists(Path.Combine(installDirectory, "SeaS.dll")))
            {
                throw new InvalidOperationException("SeaS 程序文件仍然存在，但安装清单缺失。已停止卸载以避免误删文件。");
            }

            return InstallDirectoryState.StaleRegistration;
        }

        return InstallDirectoryState.ManagedInstall;
    }

    private static int RunPackageSelfTests()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "SeaS.Uninstaller.SelfTest", Guid.NewGuid().ToString("N"));
        var installDirectory = Path.Combine(testRoot, AppName);
        try
        {
            Directory.CreateDirectory(installDirectory);
            File.WriteAllText(Path.Combine(installDirectory, "SeaS_Uninstall.exe"), string.Empty);
            if (ValidateInstallDirectory(installDirectory) != InstallDirectoryState.StaleRegistration)
            {
                return 1;
            }

            File.WriteAllText(Path.Combine(installDirectory, ManifestFileName), ManifestFileName);
            if (ValidateInstallDirectory(installDirectory) != InstallDirectoryState.ManagedInstall)
            {
                return 1;
            }

            File.Delete(Path.Combine(installDirectory, ManifestFileName));
            File.WriteAllText(Path.Combine(installDirectory, "SeaS.exe"), string.Empty);
            try
            {
                ValidateInstallDirectory(installDirectory);
                return 1;
            }
            catch (InvalidOperationException)
            {
            }

            return 0;
        }
        finally
        {
            try
            {
                if (Directory.Exists(testRoot))
                {
                    Directory.Delete(testRoot, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private static void CloseInstalledProcesses(string installDirectory)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(Path.Combine(installDirectory, "SeaS.exe")),
            Path.GetFullPath(Path.Combine(installDirectory, "Plugins", "LittleFish", "LittleFish.exe"))
        };

        foreach (var processName in new[] { "SeaS", "LittleFish" })
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    string? executablePath;
                    try
                    {
                        executablePath = process.MainModule?.FileName;
                    }
                    catch
                    {
                        continue;
                    }

                    if (executablePath is null || !targets.Contains(Path.GetFullPath(executablePath)))
                    {
                        continue;
                    }

                    try
                    {
                        process.CloseMainWindow();
                        if (!process.WaitForExit(1500))
                        {
                            process.Kill(entireProcessTree: true);
                            process.WaitForExit(2500);
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }
    }

    private static void DeleteShortcuts(bool isMachineScope)
    {
        var desktopDirectory = Environment.GetFolderPath(isMachineScope
            ? Environment.SpecialFolder.CommonDesktopDirectory
            : Environment.SpecialFolder.DesktopDirectory);
        var programsDirectory = Environment.GetFolderPath(isMachineScope
            ? Environment.SpecialFolder.CommonPrograms
            : Environment.SpecialFolder.Programs);
        TryDeleteFile(Path.Combine(desktopDirectory, $"{AppName}.lnk"));
        TryDeleteDirectory(Path.Combine(programsDirectory, AppName), recursive: true);
    }

    private static void DeleteUninstallRegistryEntry(bool isMachineScope)
    {
        var hive = isMachineScope ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        baseKey.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false);
    }

    private static void DeleteManagedFiles(string installDirectory)
    {
        var manifestPath = Path.Combine(installDirectory, ManifestFileName);
        var root = Path.GetFullPath(installDirectory) + Path.DirectorySeparatorChar;
        var managedFiles = File.ReadAllLines(manifestPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim().Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(path => path.Length)
            .ToArray();

        foreach (var relativePath in managedFiles)
        {
            var fullPath = Path.GetFullPath(Path.Combine(installDirectory, relativePath));
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(fullPath, manifestPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("安装清单中包含无效路径。");
            }

            TryDeleteFile(fullPath);
        }

        TryDeleteFile(manifestPath);
        foreach (var directory in Directory.EnumerateDirectories(installDirectory, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            TryDeleteDirectory(directory, recursive: false);
        }

        TryDeleteDirectory(installDirectory, recursive: false);
    }

    private static void DeleteCurrentUserData()
    {
        var localAppData = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        var dataDirectory = Path.GetFullPath(Path.Combine(localAppData, AppName));
        if (!dataDirectory.StartsWith(localAppData + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("用户数据目录无效。");
        }

        TryDeleteDirectory(dataDirectory, recursive: true);
    }

    private static void WaitForParent(int parentId)
    {
        try
        {
            using var parent = Process.GetProcessById(parentId);
            parent.WaitForExit(5000);
        }
        catch
        {
        }
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

    private static string QuoteArgument(string value)
    {
        return $"\"{value.Replace("\"", "\\\"")}\"";
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path, bool recursive)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive);
            }
        }
        catch
        {
        }
    }

    private static void ShowMessage(string message, uint icon)
    {
        MessageBoxW(IntPtr.Zero, message, AppName, MbOk | icon);
    }

    private static void ScheduleTemporaryCopyCleanup()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }

        MoveFileExW(executablePath, null, MoveFileDelayUntilReboot);
        var directory = Path.GetDirectoryName(executablePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            MoveFileExW(directory, null, MoveFileDelayUntilReboot);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr windowHandle, string text, string caption, uint type);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(string existingFileName, string? newFileName, uint flags);
}
