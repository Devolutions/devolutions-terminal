using Devolutions.Terminal.Connection;
using Xunit;

namespace Devolutions.Terminal.Connection.Tests;

public sealed class WslCommandLineTests
{
    [Fact]
    public void UnquotesSingleTokenDistributionName()
    {
        Assert.Equal(
            @"C:\WINDOWS\system32\wsl.exe -d Ubuntu-24.04",
            WslCommandLine.Normalize(@"C:\WINDOWS\system32\wsl.exe -d ""Ubuntu-24.04"""));
        Assert.Equal(
            @"""C:\WINDOWS\system32\wsl.exe"" -d Ubuntu-24.04",
            WslCommandLine.Normalize(@"""C:\WINDOWS\system32\wsl.exe"" -d ""Ubuntu-24.04"""));
        Assert.Equal(
            "wsl.exe --distribution Ubuntu",
            WslCommandLine.Normalize("wsl.exe --distribution \"Ubuntu\""));
    }

    [Fact]
    public void LeavesDistributionIdAndSpacedNamesAlone()
    {
        const string byId = @"C:\WINDOWS\system32\wsl.exe --distribution-id {34aae364-1deb-493b-91a5-17e0e07e321e}";
        Assert.Equal(byId, WslCommandLine.Normalize(byId));
        Assert.Equal(
            @"wsl.exe -d ""Ubuntu 24.04""",
            WslCommandLine.Normalize(@"wsl.exe -d ""Ubuntu 24.04"""));
        Assert.Equal(
            @"C:\Windows\System32\cmd.exe /c ""echo -d \""Ubuntu\""""",
            WslCommandLine.Normalize(@"C:\Windows\System32\cmd.exe /c ""echo -d \""Ubuntu\"""""));
    }
}
