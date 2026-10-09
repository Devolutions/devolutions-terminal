using System.Diagnostics;
using Iseberg.Core;
using Xunit;

namespace Devolutions.Terminal.App.Tests;

public sealed class PortableIseProcessTests
{
    [Fact]
    public async Task SingleAssemblyModuleAuthenticatesAndExecutesWithoutContractsDll()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dt-ise-single-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var module = Path.Combine(directory, "Iseberg.PowerShell.dll");
            File.Copy(PowerShellProcessDiscovery.ModulePath, module);
            Assert.Equal(new[] { module }, Directory.GetFiles(directory));
            await using var session = new PowerShellSession { ModulePath = module };
            await session.InitializeAsync();
            var childId = Assert.IsType<int>(session.ChildProcessId);
            var output = new List<OutputEntry>();
            session.Output += output.Add;
            await session.ExecuteAsync("""
                'DT-CONTRACT-OWNER:' + [Iseberg.Core.IseBridgeProtocol].Assembly.GetName().Name
                'DT-CONTRACT-REFERENCE:' + (@([Iseberg.Core.IseBridgeProtocol].Assembly.GetReferencedAssemblies() |
                    Where-Object Name -eq 'Iseberg.Contracts').Count)
                'DT-CONTRACT-LOADED:' + (@([AppDomain]::CurrentDomain.GetAssemblies() |
                    Where-Object { $_.GetName().Name -eq 'Iseberg.Contracts' }).Count)
                $global:dtSingleModuleValue = 42
                """);
            Assert.Equal(new[] { "DT-CONTRACT-OWNER:Iseberg.PowerShell", "DT-CONTRACT-REFERENCE:0", "DT-CONTRACT-LOADED:0" },
                output.Where(entry => entry.Kind == OutputKind.Output).Select(entry => entry.Text.TrimEnd('\r', '\n')).ToArray());
            Assert.DoesNotContain(output, entry => entry.Kind == OutputKind.Error);
            output.Clear();
            await session.ExecuteAsync("'DT-SINGLE-MODULE:' + $global:dtSingleModuleValue");
            Assert.Equal("DT-SINGLE-MODULE:42",
                Assert.Single(output, entry => entry.Kind == OutputKind.Output).Text.TrimEnd('\r', '\n'));
            Assert.DoesNotContain(output, entry => entry.Kind == OutputKind.Error);
            Assert.True(Assert.Single((await session.AnalyzeAsync("'unterminated")).Errors).IncompleteInput);
            Assert.Equal(childId, session.ChildProcessId);
            Assert.Equal(SessionState.Ready, session.State);
            AssertParentHasNoPowerShellEngine();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExecutionAndAnalysisStayInTheOwnedPowerShellChild()
    {
        await using var session = new PowerShellSession();
        await session.InitializeAsync();
        using var child = Process.GetProcessById(Assert.IsType<int>(session.ChildProcessId));
        Assert.NotEqual(Environment.ProcessId, child.Id);
        Assert.StartsWith("pwsh", child.ProcessName, StringComparison.OrdinalIgnoreCase);
        var output = new List<OutputEntry>();
        session.Output += output.Add;
        await session.ExecuteAsync("$global:dtIsolationValue = 42; 'DT-CHILD-PID:' + $PID");
        await session.ExecuteAsync("'DT-PERSISTENT-VALUE:' + $global:dtIsolationValue");
        Assert.Contains(output, entry => entry.Kind == OutputKind.Output &&
            entry.Text.TrimEnd('\r', '\n') == $"DT-CHILD-PID:{child.Id}");
        Assert.Contains(output, entry => entry.Kind == OutputKind.Output &&
            entry.Text.TrimEnd('\r', '\n') == "DT-PERSISTENT-VALUE:42");
        var analysis = await session.AnalyzeAsync("'unterminated");
        Assert.True(Assert.Single(analysis.Errors).IncompleteInput);
        Assert.Equal(SessionState.Ready, session.State);
        AssertParentHasNoPowerShellEngine();
    }

    [Fact]
    public async Task ResizingDuringInitializationDefersIpcUntilTheSessionIsReady()
    {
        await using var session = new PowerShellSession();
        var output = new List<OutputEntry>();
        session.Output += output.Add;
        session.SetTerminalSize(83, 29);
        var initialization = session.InitializeAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!initialization.IsCompleted)
        {
            session.SetTerminalSize(83, 29);
            await Task.Delay(1, deadline.Token);
        }
        await initialization;
        var childId = Assert.IsType<int>(session.ChildProcessId);
        await session.ExecuteAsync("'DT-STARTUP-SIZE:' + $Host.UI.RawUI.WindowSize.Width + ':' + $Host.UI.RawUI.WindowSize.Height");
        Assert.Equal("DT-STARTUP-SIZE:83:29",
            Assert.Single(output, entry => entry.Kind == OutputKind.Output).Text.TrimEnd('\r', '\n'));
        Assert.DoesNotContain(output, entry => entry.Kind == OutputKind.Error);
        output.Clear();
        session.SetTerminalSize(91, 31);
        await session.ExecuteAsync("'DT-READY-SIZE:' + $Host.UI.RawUI.WindowSize.Width + ':' + $Host.UI.RawUI.WindowSize.Height");
        Assert.Equal("DT-READY-SIZE:91:31",
            Assert.Single(output, entry => entry.Kind == OutputKind.Output).Text.TrimEnd('\r', '\n'));
        Assert.DoesNotContain(output, entry => entry.Kind == OutputKind.Error);
        Assert.Equal(childId, session.ChildProcessId);
        Assert.Equal(SessionState.Ready, session.State);
        AssertParentHasNoPowerShellEngine();
    }

    [Fact]
    public async Task DisposingSessionTerminatesItsOwnedChild()
    {
        var session = new PowerShellSession();
        try
        {
            await session.InitializeAsync();
            using var child = Process.GetProcessById(Assert.IsType<int>(session.ChildProcessId));
            await session.DisposeAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(child.HasExited);
            Assert.Equal(SessionState.Disposed, session.State);
        }
        finally
        {
            await session.DisposeAsync();
        }
        AssertParentHasNoPowerShellEngine();
    }

    [Fact]
    public async Task UnexpectedChildExitFailsThePendingCallAndSession()
    {
        await using var session = new PowerShellSession();
        await session.InitializeAsync();
        using var child = Process.GetProcessById(Assert.IsType<int>(session.ChildProcessId));
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.StateChanged += state =>
        {
            if (state == SessionState.Failed) failed.TrySetResult();
        };
        await Assert.ThrowsAnyAsync<IOException>(() =>
            session.ExecuteAsync("[System.Environment]::Exit(37)").WaitAsync(TimeSpan.FromSeconds(10)));
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(child.HasExited);
        Assert.Equal(SessionState.Failed, session.State);
        Assert.True(session.ChildProcessId is null || session.ChildProcessId == child.Id,
            "Session loss must not automatically launch a replacement child.");
        AssertParentHasNoPowerShellEngine();
    }

    [Fact]
    public async Task SeparateSessionsOwnSeparateProcessesAndSurviveTheOtherSessionClosing()
    {
        await using var first = new PowerShellSession();
        await using var second = new PowerShellSession();
        await first.InitializeAsync();
        await second.InitializeAsync();
        var firstId = Assert.IsType<int>(first.ChildProcessId);
        var secondId = Assert.IsType<int>(second.ChildProcessId);
        Assert.NotEqual(firstId, secondId);
        Assert.NotEqual(first.LocalRunspaceId, second.LocalRunspaceId);
        var firstOutput = new List<OutputEntry>();
        var secondOutput = new List<OutputEntry>();
        first.Output += firstOutput.Add;
        second.Output += secondOutput.Add;
        await first.ExecuteAsync("$global:dtSeparateSession = 11; 'DT-FIRST:' + $global:dtSeparateSession");
        await second.ExecuteAsync("$global:dtSeparateSession = 29; 'DT-SECOND:' + $global:dtSeparateSession");
        Assert.Equal("DT-FIRST:11", Assert.Single(firstOutput, entry => entry.Kind == OutputKind.Output).Text.TrimEnd('\r', '\n'));
        Assert.Equal("DT-SECOND:29", Assert.Single(secondOutput, entry => entry.Kind == OutputKind.Output).Text.TrimEnd('\r', '\n'));
        using var firstChild = Process.GetProcessById(firstId);
        using var secondChild = Process.GetProcessById(secondId);
        await first.DisposeAsync();
        await firstChild.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(firstChild.HasExited);
        Assert.False(secondChild.HasExited);
        secondOutput.Clear();
        await second.ExecuteAsync("'DT-SECOND-RETAINED:' + $global:dtSeparateSession");
        Assert.Equal("DT-SECOND-RETAINED:29",
            Assert.Single(secondOutput, entry => entry.Kind == OutputKind.Output).Text.TrimEnd('\r', '\n'));
        Assert.Equal(secondId, second.ChildProcessId);
        Assert.Equal(SessionState.Ready, second.State);
        AssertParentHasNoPowerShellEngine();
    }

    [Fact]
    public async Task CanceledAnalysisDoesNotFaultOrReplaceTheExecutionSession()
    {
        await using var session = new PowerShellSession();
        await session.InitializeAsync();
        var childId = session.ChildProcessId;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.AnalyzeAsync("if ($true) { 'not executed' }", null, cancellation.Token));
        Assert.Equal(SessionState.Ready, session.State);
        var output = new List<OutputEntry>();
        session.Output += output.Add;
        await session.ExecuteAsync("'DT-AFTER-ANALYSIS-CANCEL'");
        Assert.Equal("DT-AFTER-ANALYSIS-CANCEL",
            Assert.Single(output, entry => entry.Kind == OutputKind.Output).Text.TrimEnd('\r', '\n'));
        Assert.Empty((await session.AnalyzeAsync("$value = 42")).Errors);
        Assert.Equal(childId, session.ChildProcessId);
        Assert.Equal(SessionState.Ready, session.State);
        AssertParentHasNoPowerShellEngine();
    }

    [Fact]
    public async Task RawChildStandardStreamsCannotContaminateThePrivateProtocol()
    {
        await using var session = new PowerShellSession();
        await session.InitializeAsync();
        var childId = session.ChildProcessId;
        var output = new List<OutputEntry>();
        session.Output += output.Add;
        await session.ExecuteAsync("""
            [Console]::Out.WriteLine('DT-RAW-STDOUT')
            [Console]::Error.WriteLine('DT-RAW-STDERR')
            'DT-IPC-RESULT'
            """);
        Assert.Equal("DT-IPC-RESULT",
            Assert.Single(output, entry => entry.Kind == OutputKind.Output).Text.TrimEnd('\r', '\n'));
        Assert.DoesNotContain(output, entry => entry.Kind != OutputKind.Command &&
            entry.Text.Contains("DT-RAW-", StringComparison.Ordinal));
        Assert.Empty((await session.AnalyzeAsync("if ($true) { $value = 42 }")).Errors);
        Assert.Equal(childId, session.ChildProcessId);
        Assert.Equal(SessionState.Ready, session.State);
        AssertParentHasNoPowerShellEngine();
    }

    private static void AssertParentHasNoPowerShellEngine() =>
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(),
            assembly => assembly.GetName().Name == "System.Management.Automation");
}
