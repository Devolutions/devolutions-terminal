using Devolutions.Terminal.Connection;
using Xunit;

namespace Devolutions.Terminal.Connection.Tests;

public sealed class UnixTerminalEnvironmentTests
{
    [Fact]
    public void MacOsEnvironmentSetsTermProgramAndPreservesPath()
    {
        var environment = new Dictionary<string, string?>
        {
            ["PATH"] = "/usr/bin:/bin",
        };

        UnixTerminalEnvironment.Apply(environment, isMacOs: true);

        Assert.Equal("xterm-256color", environment["TERM"]);
        Assert.Equal("truecolor", environment["COLORTERM"]);
        Assert.Equal(UnixTerminalEnvironment.TermProgram, environment["TERM_PROGRAM"]);
        Assert.Contains("/usr/bin", environment["PATH"], StringComparison.Ordinal);
        Assert.Contains("/bin", environment["PATH"], StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxEnvironmentDoesNotSetTermProgram()
    {
        var environment = new Dictionary<string, string?>();

        UnixTerminalEnvironment.Apply(environment, isMacOs: false);

        Assert.Equal("xterm-256color", environment["TERM"]);
        Assert.False(environment.ContainsKey("TERM_PROGRAM"));
        Assert.False(environment.ContainsKey("PATH"));
    }
}
