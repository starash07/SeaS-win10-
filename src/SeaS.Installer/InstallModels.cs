namespace SeaS.Installer;

internal enum InstallScope
{
    CurrentUser,
    AllUsers
}

internal sealed record InstallOptions(
    InstallScope Scope,
    string InstallDirectory,
    bool CreateDesktopShortcut,
    bool CreateStartMenuShortcut,
    bool AllowDowngrade = false,
    bool IsExistingInstallation = false);

internal readonly record struct InstallProgress(int Value, string Status);

internal sealed record InstallResult(string InstallDirectory, string ExecutablePath);

internal sealed record RegisteredInstallation(
    InstallScope Scope,
    string InstallDirectory,
    string DisplayVersion);

internal sealed class MultipleInstallationsException : InvalidOperationException
{
    public MultipleInstallationsException()
        : base("检测到多个不同位置的 SeaS 安装记录。请先在系统设置中清理旧版本，再继续安装。")
    {
    }
}

internal sealed class NewerVersionInstalledException(string installedVersion)
    : InvalidOperationException($"目标位置已经安装较新的 SeaS {installedVersion}。")
{
    public string InstalledVersion { get; } = installedVersion;
}
