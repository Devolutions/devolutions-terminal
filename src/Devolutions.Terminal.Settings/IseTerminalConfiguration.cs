namespace Devolutions.Terminal.Settings;

public sealed record IseTerminalConfiguration(
    string Commandline,
    string? ConnectionType,
    bool Elevate,
    IReadOnlyDictionary<string, string?> Environment);
