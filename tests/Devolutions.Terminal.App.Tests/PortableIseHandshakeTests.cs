using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Iseberg.Core;
using Xunit;

namespace Devolutions.Terminal.App.Tests;

public sealed class PortableIseHandshakeTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    [Theory]
    [InlineData("protocol")]
    [InlineData("build")]
    [InlineData("proof")]
    [InlineData("pid")]
    [InlineData("start-time")]
    public async Task MismatchedHelloCannotInitializeButCorrectProofAuthenticates(string mismatch)
    {
        await using var child = await BridgeChild.StartAsync();
        var hello = child.Hello;
        var invalid = mismatch switch
        {
            "protocol" => hello with { Protocol = hello.Protocol + 1 },
            "build" => hello with { Build = hello.Build + "-mismatched" },
            "proof" => hello with { Authentication = (hello.Authentication[0] == '0' ? "1" : "0") + hello.Authentication[1..] },
            "pid" => hello with { ParentProcessId = -1 },
            "start-time" => hello with { ParentStartTimeUtcTicks = hello.ParentStartTimeUtcTicks + 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch))
        };
        var error = await Assert.ThrowsAsync<BridgeRemoteException>(() =>
            child.Connection.CallAsync("hello", BridgeJson.Element(invalid)).WaitAsync(Deadline));
        Assert.Equal("InvalidOperationException", error.Fault.Code);
        Assert.Equal("DT bridge authentication or build identity mismatch.", error.Fault.Message);
        await AssertInitializationRejectedAsync(child);
        await AuthenticateAndInitializeAsync(child);
        Assert.True((await child.Connection.CallAsync("dispose", BridgeJson.Element(true)).WaitAsync(Deadline)).GetBoolean());
    }

    [Fact]
    public async Task InitializationRequiresAuthenticationAndHelloCannotBeReplayed()
    {
        await using var child = await BridgeChild.StartAsync();
        await AssertInitializationRejectedAsync(child);
        await AuthenticateAndInitializeAsync(child);
        var error = await Assert.ThrowsAsync<BridgeRemoteException>(() =>
            child.Connection.CallAsync("hello", BridgeJson.Element(child.Hello)).WaitAsync(Deadline));
        Assert.Equal("InvalidOperationException", error.Fault.Code);
        Assert.Equal("The private bridge has already authenticated.", error.Fault.Message);
        Assert.True((await child.Connection.CallAsync("dispose", BridgeJson.Element(true)).WaitAsync(Deadline)).GetBoolean());
    }

    private static async Task AssertInitializationRejectedAsync(BridgeChild child)
    {
        var error = await Assert.ThrowsAsync<BridgeRemoteException>(() =>
            child.Connection.CallAsync("initialize", BridgeJson.Element(
                new SessionInitialize(child.SnippetDirectory, false))).WaitAsync(Deadline));
        Assert.Equal("InvalidOperationException", error.Fault.Code);
        Assert.Equal("Authenticate the private DT bridge before submitting operations.", error.Fault.Message);
    }

    private static async Task AuthenticateAndInitializeAsync(BridgeChild child)
    {
        var response = BridgeJson.Read<BridgeHelloResult>(
            await child.Connection.CallAsync("hello", BridgeJson.Element(child.Hello)).WaitAsync(Deadline));
        Assert.Equal(IseBridgeProtocol.Version, response.Protocol);
        Assert.Equal(IseBridgeProtocol.BuildIdentity, response.Build);
        Assert.Equal(child.ProcessId, response.ProcessId);
        Assert.Equal(IseBridgeProtocol.AuthenticationProof(child.Key, child.Hello.Challenge, "child"), response.AuthenticationProof);
        Assert.NotEqual(child.Hello.Authentication, response.AuthenticationProof);
        Assert.True((await child.Connection.CallAsync("initialize", BridgeJson.Element(
            new SessionInitialize(child.SnippetDirectory, false))).WaitAsync(Deadline)).GetBoolean());
    }

    private sealed class BridgeChild(
        Process process, BridgeConnection connection, BridgeHello hello, string key, string snippetDirectory,
        Task<string> output, Task<string> error) : IAsyncDisposable
    {
        public BridgeConnection Connection { get; } = connection;
        public BridgeHello Hello { get; } = hello;
        public string Key { get; } = key;
        public string SnippetDirectory { get; } = snippetDirectory;
        public int ProcessId => process.Id;

        public static async Task<BridgeChild> StartAsync()
        {
            var module = Path.Combine(AppContext.BaseDirectory, "Iseberg.PowerShell", "Iseberg.PowerShell.dll");
            Assert.True(File.Exists(module), $"The distributed bridge module is missing: {module}");
            var pipeName = "dt-ise-hello-test-" + Guid.NewGuid().ToString("N");
            var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var challenge = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            using var parent = Process.GetCurrentProcess();
            var parentTicks = parent.StartTime.ToUniversalTime().Ticks;
            var hello = new BridgeHello(IseBridgeProtocol.Version, IseBridgeProtocol.BuildIdentity,
                IseBridgeProtocol.AuthenticationProof(key, challenge, "parent"), challenge, parent.Id, parentTicks);
            var home = Environment.GetEnvironmentVariable("DT_ISEBERG_PSHOME");
            var executable = string.IsNullOrEmpty(home) ? "pwsh" :
                Path.Combine(home, OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh");
            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            var bootstrap = $"Import-Module '{module.Replace("'", "''", StringComparison.Ordinal)}' -ErrorAction Stop; " +
                $"Start-IsebergBridge -PipeName '{pipeName}' -ParentProcessId {parent.Id.ToString(CultureInfo.InvariantCulture)} " +
                $"-ParentStartTimeUtcTicks {parentTicks.ToString(CultureInfo.InvariantCulture)}";
            foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
                Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap)) })
                info.ArgumentList.Add(argument);
            info.Environment[IseBridgeProtocol.AuthenticationEnvironment] = key;
            var snippetDirectory = Path.Combine(Path.GetTempPath(), "dt-ise-handshake-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(snippetDirectory);
            var process = new Process { StartInfo = info };
            var started = false;
            try
            {
                started = process.Start();
                Assert.True(started);
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                await pipe.WaitForConnectionAsync().WaitAsync(Deadline);
                var connection = new BridgeConnection(pipe);
                connection.Start();
                return new(process, connection, hello, key, snippetDirectory, output, error);
            }
            catch
            {
                if (started && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(Deadline);
                }
                process.Dispose();
                pipe.Dispose();
                Directory.Delete(snippetDirectory, recursive: true);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Connection.DisposeAsync();
                try
                {
                    await process.WaitForExitAsync().WaitAsync(Deadline);
                }
                catch (TimeoutException)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(Deadline);
                    throw;
                }
                Assert.True(process.ExitCode == 0,
                    $"Bridge exited with {process.ExitCode}.\nstdout: {await output}\nstderr: {await error}");
            }
            finally
            {
                process.Dispose();
                Directory.Delete(SnippetDirectory, recursive: true);
            }
        }
    }
}
