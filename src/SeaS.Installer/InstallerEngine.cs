using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;

namespace SeaS.Installer;

internal static class InstallerEngine
{
    private const string AppName = "SeaS";
    private const string AppExeName = "SeaS.exe";
    private const string UninstallerExeName = "SeaS_Uninstall.exe";
    private const string ManifestFileName = ".seas-manifest.txt";
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SeaS";

    public static string GetDefaultInstallDirectory(InstallScope scope)
    {
        var parent = scope == InstallScope.AllUsers
            ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        return Path.Combine(parent, AppName);
    }

    public static InstallOptions ResolveRegisteredInstallation(InstallOptions requestedOptions)
    {
        return ResolveRegisteredInstallation(requestedOptions, ReadRegisteredInstallations());
    }

    internal static InstallOptions ResolveRegisteredInstallation(
        InstallOptions requestedOptions,
        IReadOnlyList<RegisteredInstallation> registrations)
    {
        var validRegistrations = registrations
            .Where(registration => !string.IsNullOrWhiteSpace(registration.InstallDirectory))
            .Select(registration => registration with
            {
                InstallDirectory = NormalizeRegisteredInstallDirectory(registration.InstallDirectory)
            })
            .ToArray();
        if (validRegistrations.Length == 0)
        {
            return requestedOptions with
            {
                InstallDirectory = NormalizeInstallDirectory(requestedOptions.InstallDirectory),
                IsExistingInstallation = false
            };
        }

        var installPaths = validRegistrations
            .Select(registration => registration.InstallDirectory)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (installPaths.Length > 1)
        {
            throw new MultipleInstallationsException();
        }

        var existingScope = validRegistrations.Any(registration => registration.Scope == InstallScope.AllUsers)
            ? InstallScope.AllUsers
            : InstallScope.CurrentUser;
        return requestedOptions with
        {
            Scope = existingScope,
            InstallDirectory = installPaths[0],
            IsExistingInstallation = true
        };
    }

    public static IReadOnlyList<RegisteredInstallation> ReadRegisteredInstallations()
    {
        var registrations = new List<RegisteredInstallation>();
        AddRegisteredInstallation(registrations, InstallScope.CurrentUser);
        AddRegisteredInstallation(registrations, InstallScope.AllUsers);
        return registrations;
    }

    public static string NormalizeInstallDirectory(string rawPath)
    {
        var trimmed = rawPath.Trim().Trim('"').Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new InvalidOperationException("请选择安装位置。");
        }

        var fullPath = Path.GetFullPath(trimmed).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("安装位置必须是本地磁盘中的绝对路径。");
        }

        if (fullPath.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("当前版本暂不支持安装到网络位置。");
        }

        var drive = new DriveInfo(root);
        if (drive.DriveType == DriveType.Network)
        {
            throw new InvalidOperationException("当前版本暂不支持安装到网络磁盘。");
        }

        var leafName = Path.GetFileName(fullPath);
        if (!string.Equals(leafName, AppName, StringComparison.OrdinalIgnoreCase))
        {
            fullPath = Path.Combine(fullPath, AppName);
        }

        return Path.GetFullPath(fullPath);
    }

    public static IReadOnlyList<Process> FindRunningComponents(string installDirectory)
    {
        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            return [];
        }

        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(Path.Combine(installDirectory, AppExeName)),
            Path.GetFullPath(Path.Combine(installDirectory, "Plugins", "LittleFish", "LittleFish.exe"))
        };
        var result = new List<Process>();
        foreach (var processName in new[] { "SeaS", "LittleFish" })
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                try
                {
                    var executablePath = process.MainModule?.FileName;
                    if (executablePath is not null && targets.Contains(Path.GetFullPath(executablePath)))
                    {
                        result.Add(process);
                    }
                    else
                    {
                        process.Dispose();
                    }
                }
                catch
                {
                    process.Dispose();
                }
            }
        }

        return result;
    }

    public static void CloseRunningComponents(string installDirectory)
    {
        var running = FindRunningComponents(installDirectory);
        foreach (var process in running)
        {
            try
            {
                process.CloseMainWindow();
            }
            catch
            {
            }
        }

        foreach (var process in running)
        {
            try
            {
                if (!process.WaitForExit(1800))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(2500);
                }
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    public static Task<InstallResult> InstallAsync(InstallOptions options, IProgress<InstallProgress> progress)
    {
        return Task.Run(() => InstallCore(options, progress));
    }

    private static InstallResult InstallCore(InstallOptions options, IProgress<InstallProgress> progress)
    {
        // An existing registration is already the final destination. Do not append
        // another SeaS folder when an older installation uses a different name.
        var installDirectory = GetTargetInstallDirectory(options);
        var tempRoot = Path.Combine(Path.GetTempPath(), "SeaS.Setup", Guid.NewGuid().ToString("N"));
        var payloadPath = Path.Combine(tempRoot, "payload.zip");
        var stageDirectory = Path.Combine(tempRoot, "stage");
        var backupDirectory = Path.Combine(tempRoot, "backup");
        var targetExisted = Directory.Exists(installDirectory) && Directory.EnumerateFileSystemEntries(installDirectory).Any();
        var targetChanged = false;

        Directory.CreateDirectory(tempRoot);
        try
        {
            Report(progress, 5, "正在校验安装文件…");
            CopyAndVerifyPayload(payloadPath);

            Report(progress, 16, "正在准备安装文件…");
            ExtractPayload(payloadPath, stageDirectory);
            ValidatePayload(stageDirectory);
            ValidateTargetDirectory(installDirectory, options.AllowDowngrade);
            EnsureAvailableSpace(installDirectory, stageDirectory);

            CloseRunningComponents(installDirectory);

            if (targetExisted)
            {
                Report(progress, 28, "正在备份现有版本…");
                CopyDirectory(installDirectory, backupDirectory, overwrite: true);
            }

            Report(progress, 42, "正在写入 SeaS 程序文件…");
            Directory.CreateDirectory(installDirectory);
            targetChanged = true;
            var newFiles = CopyDirectory(stageDirectory, installDirectory, overwrite: true);
            RemoveObsoleteManagedFiles(installDirectory, newFiles);
            WriteManifest(installDirectory, newFiles);

            Report(progress, 76, "正在创建快捷方式…");
            UpdateShortcuts(options with { InstallDirectory = installDirectory });

            Report(progress, 90, "正在写入卸载信息…");
            RegisterUninstaller(options with { InstallDirectory = installDirectory }, stageDirectory);
            RemoveDuplicateUninstallRegistration(options.Scope, installDirectory);

            Report(progress, 100, "安装完成");
            return new InstallResult(installDirectory, Path.Combine(installDirectory, AppExeName));
        }
        catch
        {
            if (targetChanged)
            {
                TryRollbackTarget(installDirectory, targetExisted ? backupDirectory : null);
            }

            throw;
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static void CopyAndVerifyPayload(string destinationPath)
    {
        var payloadResource = Application.GetResourceStream(new Uri("Assets/payload.zip", UriKind.Relative));
        var hashResource = Application.GetResourceStream(new Uri("Assets/payload.sha256", UriKind.Relative));
        if (payloadResource is null || hashResource is null)
        {
            throw new InvalidOperationException("安装包缺少内置程序文件，请重新下载安装包。");
        }

        using (payloadResource.Stream)
        using (var destination = File.Create(destinationPath))
        {
            payloadResource.Stream.CopyTo(destination);
        }

        string expectedHash;
        using (hashResource.Stream)
        using (var reader = new StreamReader(hashResource.Stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            expectedHash = reader.ReadToEnd().Trim();
        }

        using var payload = File.OpenRead(destinationPath);
        var actualHash = Convert.ToHexString(SHA256.HashData(payload));
        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("安装文件校验失败，请重新下载安装包。");
        }
    }

    private static void ExtractPayload(string payloadPath, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        var destinationRoot = Path.GetFullPath(destinationDirectory) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(payloadPath);
        foreach (var entry in archive.Entries)
        {
            var relativePath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            var destinationPath = Path.GetFullPath(Path.Combine(destinationDirectory, relativePath));
            if (!destinationPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("安装包中包含无效文件路径。");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            entry.ExtractToFile(destinationPath, overwrite: true);
        }
    }

    private static void ValidatePayload(string stageDirectory)
    {
        foreach (var relativePath in new[]
                 {
                     AppExeName,
                     "SeaS.dll",
                     UninstallerExeName,
                     Path.Combine("Plugins", "LittleFish", "LittleFish.exe"),
                     Path.Combine("Plugins", "LittleFish", "LittleFish.dll")
                 })
        {
            if (!File.Exists(Path.Combine(stageDirectory, relativePath)))
            {
                throw new InvalidOperationException($"安装包缺少必要文件：{relativePath}");
            }
        }
    }

    private static void ValidateTargetDirectory(string installDirectory, bool allowDowngrade)
    {
        if (!Directory.Exists(installDirectory) || !Directory.EnumerateFileSystemEntries(installDirectory).Any())
        {
            return;
        }

        var existingExe = Path.Combine(installDirectory, AppExeName);
        var existingDll = Path.Combine(installDirectory, "SeaS.dll");
        if (!File.Exists(existingExe) || !File.Exists(existingDll))
        {
            var remainingEntries = Directory
                .EnumerateFileSystemEntries(installDirectory, "*", SearchOption.TopDirectoryOnly)
                .ToArray();
            if (remainingEntries.Length > 0
                && remainingEntries.All(path =>
                    string.Equals(
                        Path.GetFileName(path),
                        UninstallerExeName,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            throw new InvalidOperationException("目标文件夹不是可识别的 SeaS 安装目录。请选择其他位置，以免覆盖已有文件。");
        }

        var installedVersionText = FileVersionInfo.GetVersionInfo(existingExe).ProductVersion;
        if (Version.TryParse(NormalizeVersion(installedVersionText), out var installedVersion)
            && Version.TryParse(NormalizeVersion(GetCurrentVersion()), out var currentVersion)
            && installedVersion > currentVersion
            && !allowDowngrade)
        {
            throw new NewerVersionInstalledException(installedVersion.ToString(3));
        }
    }

    private static string NormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "0.0.0";
        }

        var separator = value.IndexOfAny(['+', '-']);
        return separator >= 0 ? value[..separator] : value;
    }

    private static string GetCurrentVersion()
    {
        var version = typeof(InstallerEngine).Assembly.GetName().Version;
        return version is null ? "1.1.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private static void EnsureAvailableSpace(string installDirectory, string stageDirectory)
    {
        var root = Path.GetPathRoot(installDirectory) ?? throw new InvalidOperationException("无法识别安装磁盘。");
        var requiredBytes = Directory.EnumerateFiles(stageDirectory, "*", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length) + 64L * 1024 * 1024;
        var drive = new DriveInfo(root);
        if (drive.AvailableFreeSpace < requiredBytes)
        {
            throw new IOException("安装磁盘空间不足。");
        }
    }

    private static List<string> CopyDirectory(string sourceDirectory, string destinationDirectory, bool overwrite)
    {
        var copiedFiles = new List<string>();
        var sourceRoot = Path.GetFullPath(sourceDirectory);
        foreach (var sourceFile in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            var destinationFile = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(sourceFile, destinationFile, overwrite);
            copiedFiles.Add(NormalizeRelativePath(relativePath));
        }

        return copiedFiles;
    }

    private static void RemoveObsoleteManagedFiles(string installDirectory, IReadOnlyCollection<string> newFiles)
    {
        var manifestPath = Path.Combine(installDirectory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return;
        }

        var newFileSet = new HashSet<string>(newFiles, StringComparer.OrdinalIgnoreCase);
        foreach (var relativePath in File.ReadAllLines(manifestPath))
        {
            var normalized = NormalizeRelativePath(relativePath);
            if (string.IsNullOrWhiteSpace(normalized)
                || string.Equals(normalized, ManifestFileName, StringComparison.OrdinalIgnoreCase)
                || newFileSet.Contains(normalized))
            {
                continue;
            }

            var obsoletePath = GetSafeManagedPath(installDirectory, normalized);
            if (File.Exists(obsoletePath))
            {
                File.Delete(obsoletePath);
            }
        }
    }

    private static void WriteManifest(string installDirectory, IEnumerable<string> files)
    {
        var manifestEntries = files
            .Append(ManifestFileName)
            .Select(NormalizeRelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
        File.WriteAllLines(Path.Combine(installDirectory, ManifestFileName), manifestEntries, new UTF8Encoding(false));
    }

    private static void UpdateShortcuts(InstallOptions options)
    {
        var isMachine = options.Scope == InstallScope.AllUsers;
        var desktopDirectory = Environment.GetFolderPath(isMachine
            ? Environment.SpecialFolder.CommonDesktopDirectory
            : Environment.SpecialFolder.DesktopDirectory);
        var programsDirectory = Environment.GetFolderPath(isMachine
            ? Environment.SpecialFolder.CommonPrograms
            : Environment.SpecialFolder.Programs);
        var startMenuDirectory = Path.Combine(programsDirectory, AppName);
        var appExe = Path.Combine(options.InstallDirectory, AppExeName);
        var uninstallerExe = Path.Combine(options.InstallDirectory, UninstallerExeName);
        var desktopShortcut = Path.Combine(desktopDirectory, $"{AppName}.lnk");

        if (options.CreateDesktopShortcut)
        {
            CreateShortcut(desktopShortcut, appExe, options.InstallDirectory, "打开 SeaS");
        }
        else
        {
            TryDeleteFile(desktopShortcut);
        }

        if (options.CreateStartMenuShortcut)
        {
            Directory.CreateDirectory(startMenuDirectory);
            CreateShortcut(Path.Combine(startMenuDirectory, $"{AppName}.lnk"), appExe, options.InstallDirectory, "打开 SeaS");
            CreateShortcut(
                Path.Combine(startMenuDirectory, $"卸载 {AppName}.lnk"),
                uninstallerExe,
                options.InstallDirectory,
                "卸载 SeaS",
                $"--uninstall \"{options.InstallDirectory}\" --scope {(isMachine ? "machine" : "user")}");
        }
        else
        {
            TryDeleteDirectory(startMenuDirectory);
        }
    }

    private static void CreateShortcut(string shortcutPath, string targetPath, string workingDirectory, string description, string arguments = "")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
                        ?? throw new InvalidOperationException("无法创建 Windows 快捷方式。");
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            dynamic dynamicShell = shell!;
            shortcut = dynamicShell.CreateShortcut(shortcutPath);
            dynamic dynamicShortcut = shortcut;
            dynamicShortcut.TargetPath = targetPath;
            dynamicShortcut.WorkingDirectory = workingDirectory;
            dynamicShortcut.Description = description;
            dynamicShortcut.Arguments = arguments;
            dynamicShortcut.IconLocation = $"{targetPath},0";
            dynamicShortcut.Save();
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut))
            {
                Marshal.FinalReleaseComObject(shortcut);
            }

            if (shell is not null && Marshal.IsComObject(shell))
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    private static void RegisterUninstaller(InstallOptions options, string stageDirectory)
    {
        var hive = options.Scope == InstallScope.AllUsers ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.CreateSubKey(UninstallKeyPath, writable: true)
                        ?? throw new InvalidOperationException("无法写入卸载信息。");
        var appExe = Path.Combine(options.InstallDirectory, AppExeName);
        var uninstallerExe = Path.Combine(options.InstallDirectory, UninstallerExeName);
        var scopeArgument = options.Scope == InstallScope.AllUsers ? "machine" : "user";
        var estimatedSizeKb = (int)Math.Min(
            int.MaxValue,
            Directory.EnumerateFiles(stageDirectory, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length) / 1024L);

        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", GetCurrentVersion());
        key.SetValue("Publisher", AppName);
        key.SetValue("InstallLocation", options.InstallDirectory);
        key.SetValue("DisplayIcon", $"{appExe},0");
        key.SetValue("UninstallString", $"\"{uninstallerExe}\" --uninstall \"{options.InstallDirectory}\" --scope {scopeArgument}");
        key.SetValue("EstimatedSize", estimatedSizeKb, RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("InstallScope", scopeArgument);
    }

    private static void AddRegisteredInstallation(
        ICollection<RegisteredInstallation> registrations,
        InstallScope scope)
    {
        try
        {
            var hive = scope == InstallScope.AllUsers ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(UninstallKeyPath);
            var installDirectory = key?.GetValue("InstallLocation") as string;
            if (string.IsNullOrWhiteSpace(installDirectory))
            {
                return;
            }

            registrations.Add(new RegisteredInstallation(
                scope,
                installDirectory,
                key?.GetValue("DisplayVersion") as string ?? "未知版本"));
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string NormalizeRegisteredInstallDirectory(string installDirectory)
    {
        var fullPath = Path.GetFullPath(installDirectory.Trim().Trim('"'));
        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    internal static string GetTargetInstallDirectory(InstallOptions options)
    {
        return options.IsExistingInstallation
            ? NormalizeRegisteredInstallDirectory(options.InstallDirectory)
            : NormalizeInstallDirectory(options.InstallDirectory);
    }

    private static void RemoveDuplicateUninstallRegistration(InstallScope installedScope, string installDirectory)
    {
        var duplicateScope = installedScope == InstallScope.AllUsers
            ? InstallScope.CurrentUser
            : InstallScope.AllUsers;
        var duplicate = ReadRegisteredInstallations()
            .FirstOrDefault(registration => registration.Scope == duplicateScope);
        if (duplicate is null
            || !string.Equals(
                NormalizeRegisteredInstallDirectory(duplicate.InstallDirectory),
                NormalizeRegisteredInstallDirectory(installDirectory),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var hive = duplicateScope == InstallScope.AllUsers ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        baseKey.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false);
    }

    internal static void RunPackageSelfTests()
    {
        var requested = new InstallOptions(
            InstallScope.AllUsers,
            @"C:\Requested\SeaS",
            CreateDesktopShortcut: true,
            CreateStartMenuShortcut: true);
        var freshInstall = ResolveRegisteredInstallation(requested with { InstallDirectory = @"C:\NewApps" }, []);
        if (freshInstall.IsExistingInstallation
            || freshInstall.Scope != requested.Scope
            || freshInstall.InstallDirectory != @"C:\NewApps\SeaS")
        {
            throw new InvalidOperationException("全新安装路径自检失败。");
        }

        var userInstall = ResolveRegisteredInstallation(requested, [
            new RegisteredInstallation(InstallScope.CurrentUser, @"D:\SeaS", "1.0.0")
        ]);
        if (!userInstall.IsExistingInstallation || userInstall.Scope != InstallScope.CurrentUser
            || !string.Equals(userInstall.InstallDirectory, @"D:\SeaS", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("当前用户安装记录解析自检失败。");
        }

        var customInstall = ResolveRegisteredInstallation(requested, [
            new RegisteredInstallation(InstallScope.AllUsers, @"D:\Reading Apps\SeaS Reader", "1.0.1")
        ]);
        if (!customInstall.IsExistingInstallation || customInstall.Scope != InstallScope.AllUsers
            || GetTargetInstallDirectory(customInstall) != @"D:\Reading Apps\SeaS Reader")
        {
            throw new InvalidOperationException("自定义目录覆盖安装自检失败：不应追加 SeaS 子目录。");
        }

        var duplicateInstall = ResolveRegisteredInstallation(requested, [
            new RegisteredInstallation(InstallScope.CurrentUser, @"D:\SeaS", "1.0.0"),
            new RegisteredInstallation(InstallScope.AllUsers, @"D:\SeaS", "1.0.0")
        ]);
        if (duplicateInstall.Scope != InstallScope.AllUsers)
        {
            throw new InvalidOperationException("跨范围安装记录合并自检失败。");
        }

        try
        {
            ResolveRegisteredInstallation(requested, [
                new RegisteredInstallation(InstallScope.CurrentUser, @"D:\SeaS", "1.0.0"),
                new RegisteredInstallation(InstallScope.AllUsers, @"C:\Program Files\SeaS", "1.0.0")
            ]);
            throw new InvalidOperationException("多位置安装记录自检未能阻止安装。");
        }
        catch (MultipleInstallationsException)
        {
        }

        var testRoot = Path.Combine(Path.GetTempPath(), "SeaS.Installer.SelfTest", Guid.NewGuid().ToString("N"));
        var staleInstallDirectory = Path.Combine(testRoot, AppName);
        try
        {
            Directory.CreateDirectory(staleInstallDirectory);
            File.WriteAllText(Path.Combine(staleInstallDirectory, UninstallerExeName), string.Empty);
            ValidateTargetDirectory(staleInstallDirectory, allowDowngrade: false);

            File.WriteAllText(Path.Combine(staleInstallDirectory, "unknown.txt"), string.Empty);
            try
            {
                ValidateTargetDirectory(staleInstallDirectory, allowDowngrade: false);
                throw new InvalidOperationException("未知残留文件自检未能阻止覆盖。");
            }
            catch (InvalidOperationException exception) when (exception.Message.StartsWith("目标文件夹不是可识别的", StringComparison.Ordinal))
            {
            }

            var existingDirectory = Path.Combine(testRoot, "Reading Apps", "SeaS Reader");
            var stageDirectory = Path.Combine(testRoot, "stage");
            Directory.CreateDirectory(existingDirectory);
            Directory.CreateDirectory(stageDirectory);
            File.WriteAllText(Path.Combine(existingDirectory, "SeaS.dll"), "old version");
            File.WriteAllText(Path.Combine(existingDirectory, "obsolete.dll"), "old component");
            File.WriteAllText(Path.Combine(existingDirectory, "reader-settings.json"), "user settings");
            WriteManifest(existingDirectory, ["SeaS.dll", "obsolete.dll"]);
            File.WriteAllText(Path.Combine(stageDirectory, "SeaS.dll"), "new version");
            var upgrade = ResolveRegisteredInstallation(requested, [
                new RegisteredInstallation(InstallScope.CurrentUser, existingDirectory, "1.0.1")
            ]);
            var targetDirectory = GetTargetInstallDirectory(upgrade);
            var newFiles = CopyDirectory(stageDirectory, targetDirectory, overwrite: true);
            RemoveObsoleteManagedFiles(targetDirectory, newFiles);
            WriteManifest(targetDirectory, newFiles);
            if (File.ReadAllText(Path.Combine(existingDirectory, "SeaS.dll")) != "new version"
                || File.Exists(Path.Combine(existingDirectory, "obsolete.dll"))
                || File.ReadAllText(Path.Combine(existingDirectory, "reader-settings.json")) != "user settings"
                || Directory.Exists(Path.Combine(existingDirectory, AppName)))
            {
                throw new InvalidOperationException("原目录覆盖更新和用户文件保留自检失败。");
            }
        }
        finally
        {
            TryDeleteDirectory(testRoot);
        }
    }

    private static void TryRollbackTarget(string installDirectory, string? backupDirectory)
    {
        try
        {
            if (Directory.Exists(installDirectory))
            {
                Directory.Delete(installDirectory, recursive: true);
            }

            if (backupDirectory is not null && Directory.Exists(backupDirectory))
            {
                CopyDirectory(backupDirectory, installDirectory, overwrite: true);
            }
        }
        catch
        {
        }
    }

    private static string GetSafeManagedPath(string installDirectory, string relativePath)
    {
        var root = Path.GetFullPath(installDirectory) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(installDirectory, relativePath));
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("安装清单中包含无效路径。");
        }

        return fullPath;
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Trim().Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
    }

    private static void Report(IProgress<InstallProgress> progress, int value, string status)
    {
        progress.Report(new InstallProgress(value, status));
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
