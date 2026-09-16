namespace Devolutions.Terminal.Connection;

public sealed record TerminalLaunchOptions
{
    public required string CommandLine { get; init; }

    public string? WorkingDirectory { get; init; }

    public int Columns { get; init; } = 80;

    public int Rows { get; init; } = 30;

    /// <summary>
    /// Copies the host process environment into the child block. Ignored when
    /// <see cref="ReloadEnvironmentVariables"/> is set, which takes precedence.
    /// </summary>
    public bool InheritEnvironment { get; init; } = true;

    /// <summary>
    /// Regenerates the Windows environment from identity, session, and
    /// registry state instead of inheriting the host process environment.
    /// </summary>
    public bool ReloadEnvironmentVariables { get; init; }

    /// <summary>
    /// Profile overrides applied last. Values are expanded against the
    /// constructed environment, an empty value is a no-op, and a
    /// <see langword="null"/> value deletes the variable.
    /// </summary>
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } =
        new Dictionary<string, string?>();

    public TerminalCloseOnExitPolicy CloseOnExit { get; init; } =
        TerminalCloseOnExitPolicy.Automatic;

    public bool IsDefaultTerminalSession { get; init; }
}
