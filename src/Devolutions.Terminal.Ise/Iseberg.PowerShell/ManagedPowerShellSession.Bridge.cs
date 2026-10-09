using System.Management.Automation;
using Iseberg.Core;

namespace Iseberg.PowerShellHost;

public sealed partial class ManagedPowerShellSession
{
    private static Iseberg.Core.CompletionResult ToCompletion(System.Management.Automation.CompletionResult result) =>
        new(result.CompletionText, result.ListItemText, (Iseberg.Core.CompletionResultType)(long)result.ResultType, result.ToolTip);

    internal SessionStatus Status() => new(State, Prompt, Version, LocalRunspaceId, RunspaceId,
        IsRunspacePushed, IsRemote, RemoteComputerName, IsDebuggerPaused, IsNestedPromptActive, NestedPrompt);

    internal async Task<RemoteSaveResult> SaveRemoteDataAsync(RemoteSaveRequest request)
    {
        if (!IsRemote || request.RunspaceId != RunspaceId)
            throw new InvalidOperationException("The remote connection changed. Reopen the document before saving.");
        ScriptFile file;
        if (request.OriginalPath is { } original)
            file = ScriptFile.FromRemoteBytes(original, request.Bytes, request.RunspaceId, RemoteComputerName!);
        else
        {
            var decoded = ScriptFile.FromBytes(Path.GetFullPath("bridge-unsaved.ps1"), request.Bytes);
            file = ScriptFile.CreateUntitled("Untitled.ps1", decoded.EncodingChoice);
            file.Text = decoded.Text;
        }
        await SaveRemoteFileCoreAsync(file, request.Path, request.ExpectedVersion, request.UseSavedVersion);
        return new(file.Path!, RunspaceId, RemoteComputerName!);
    }

    internal static void ValidateBreakpoint(BreakpointSpec spec)
    {
        spec.Validate();
        if (!string.IsNullOrWhiteSpace(spec.Condition)) _ = ScriptBlock.Create($"if ({spec.Condition}) {{ break }}");
        if (!string.IsNullOrWhiteSpace(spec.Action)) _ = ScriptBlock.Create(spec.Action);
    }

    internal Task DispatchNotificationAsync(Action notification) =>
        State == Iseberg.Core.SessionState.Debugging ? PausedQueryAsync(() =>
        {
            notification();
            return true;
        }) : State == Iseberg.Core.SessionState.NestedPrompt ? NestedQueryAsync(shell =>
        {
            shell.AddCommand("Invoke-IsebergNotification").AddParameter("Callback", notification).Invoke();
            return true;
        }) : QueryAsync(shell =>
        {
            shell.AddCommand("Invoke-IsebergNotification").AddParameter("Callback", notification).Invoke();
            ThrowQueryErrors(shell);
            return true;
        }, waitForGate: true);
}

[Cmdlet(VerbsLifecycle.Invoke, "IsebergNotification")]
public sealed class InvokeIsebergNotificationCommand : PSCmdlet
{
    [Parameter(Mandatory = true)] public Action Callback { get; set; } = null!;
    protected override void EndProcessing() => Callback();
}
