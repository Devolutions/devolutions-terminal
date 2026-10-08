namespace Iseberg.Core;

/// <summary>Exclusive ownership of one embedded workbench's settings and recovery namespace.</summary>
public sealed class WorkbenchPersistenceLease : IDisposable
{
    private readonly FileStream stream;

    public WorkbenchPersistenceLease(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        if (!Path.IsPathFullyQualified(settingsPath))
            throw new ArgumentException("An absolute settings path is required.", nameof(settingsPath));
        var directory = Path.GetDirectoryName(settingsPath)!;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        stream = new FileStream(settingsPath + ".lock", options);
    }

    public void Dispose() => stream.Dispose();
}
