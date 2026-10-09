using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;

namespace Iseberg.Core;

public static class IseBridgeProtocol
{
    public const int Version = 1;
    public const int MaximumFrameBytes = 8 * 1024 * 1024;
    public const int MaximumPendingRequests = 128;
    public const int MaximumQueuedFrames = 512;
    public const int MaximumOutputCharacters = 16 * 1024;
    public const string AuthenticationEnvironment = "DT_ISEBERG_BRIDGE_AUTH";
    public const string ModuleDirectory = "Iseberg.PowerShell";
    public static string BuildIdentity => IseBridgeBuild.Identity;
    public static string AuthenticationProof(string key, string challenge, string role) =>
        Convert.ToHexString(HMACSHA256.HashData(Convert.FromHexString(key), Encoding.UTF8.GetBytes(role + ":" + challenge)));
}

public sealed record BridgeEnvelope(string Kind, long Id, string Operation, JsonElement Payload,
    BridgeFault? Fault = null);
public sealed record BridgeFault(string Code, string Message, string? FilePath = null,
    string? ExpectedVersion = null, string? ActualVersion = null);
public sealed record BridgeHello(int Protocol, string Build, string Authentication, string Challenge, int ParentProcessId,
    long ParentStartTimeUtcTicks);
public sealed record BridgeHelloResult(int Protocol, string Build, string PowerShellVersion, int ProcessId,
    string AuthenticationProof);
public sealed record SessionStatus(SessionState State, string Prompt, string Version, Guid LocalRunspaceId,
    Guid RunspaceId, bool IsRunspacePushed, bool IsRemote, string? RemoteComputerName,
    bool IsDebuggerPaused, bool IsNestedPromptActive, string NestedPrompt);
public sealed record SessionInitialize(string? SnippetDirectory, bool ScriptingEnabled);
public sealed record TextRequest(string Text);
public sealed record AnalyzeRequest(string Text, string? DocumentPath = null);
public sealed record ExecuteRequest(string Script, string? FilePath = null);
public sealed record CompleteRequest(string Text, int Cursor);
public sealed record CommandFormRequest(string Name, string? Module = null);
public sealed record TerminalSizeRequest(int Columns, int Rows);
public sealed record ResumeRequest(DebuggerResumeAction Action);
public sealed record InspectRequest(string[] Watches, int FrameIndex);
public sealed record ValueChildrenRequest(long Reference, int Offset, int Count);
public sealed record BreakpointUpdateRequest(int Id, BreakpointSpec Spec);
public sealed record BreakpointIdRequest(int Id, bool Enabled = true);
public sealed record LineBreakpointsRequest(string Path, BreakpointSpec[] Specs);
public sealed record ScriptNotification(string ObjectId, string PropertyName);
public sealed record HostInput(string Caption, string Message, bool Secret, IReadOnlyList<PromptChoice> Choices,
    IReadOnlyList<int> DefaultChoices, bool MultipleChoice);
public sealed record HostShowCommand(CommandFormDescription? Command, IReadOnlyList<CommandDescription> Commands,
    string HelpText, CommandHelpDocument? HelpDocument, Uri? HelpUri, bool PassThru, double Width, double Height);
public sealed record HostResponse(string? Text);
public sealed record RemoteOpenRequest(string Path, ScriptEncoding? Encoding = null);
public sealed record RemoteFileData(string Path, byte[] Bytes, Guid RunspaceId, string ComputerName);
public sealed record RemoteSaveRequest(string Path, byte[] Bytes, string? ExpectedVersion, string? OriginalPath,
    bool UseSavedVersion, Guid RunspaceId);
public sealed record RemoteSaveResult(string Path, Guid RunspaceId, string ComputerName);
public enum RemoteConnectionKind { Ssh, WSMan }
public sealed record RemoteConnectionInfo(RemoteConnectionKind Kind, string ComputerName,
    string? UserName = null, int Port = 0, string? KeyFilePath = null, string? Subsystem = null,
    string? ConnectionUri = null, string? ConfigurationName = null, string? Password = null);
public sealed record CommandFormBuildRequest(CommandFormDescription Description, string ParameterSet,
    Dictionary<string, CommandParameterValue> Values);
public sealed record ExpressionValidation(string Text, bool IsValid, string? Error = null);
public sealed record SnippetCreateRequest(string Title, string Description, string Text, string Author,
    int CaretOffset, bool Force);
public sealed record SnippetImportRequest(string Path, bool Recurse);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(BridgeEnvelope))]
[JsonSerializable(typeof(BridgeHello))]
[JsonSerializable(typeof(BridgeHelloResult))]
[JsonSerializable(typeof(SessionStatus))]
[JsonSerializable(typeof(SessionInitialize))]
[JsonSerializable(typeof(TextRequest))]
[JsonSerializable(typeof(AnalyzeRequest))]
[JsonSerializable(typeof(ExecuteRequest))]
[JsonSerializable(typeof(CompleteRequest))]
[JsonSerializable(typeof(CommandFormRequest))]
[JsonSerializable(typeof(TerminalSizeRequest))]
[JsonSerializable(typeof(ResumeRequest))]
[JsonSerializable(typeof(InspectRequest))]
[JsonSerializable(typeof(ValueChildrenRequest))]
[JsonSerializable(typeof(BreakpointSpec))]
[JsonSerializable(typeof(BreakpointUpdateRequest))]
[JsonSerializable(typeof(BreakpointIdRequest))]
[JsonSerializable(typeof(LineBreakpointsRequest))]
[JsonSerializable(typeof(ScriptNotification))]
[JsonSerializable(typeof(HostInput))]
[JsonSerializable(typeof(HostShowCommand))]
[JsonSerializable(typeof(HostResponse))]
[JsonSerializable(typeof(RemoteOpenRequest))]
[JsonSerializable(typeof(RemoteFileData))]
[JsonSerializable(typeof(RemoteSaveRequest))]
[JsonSerializable(typeof(RemoteSaveResult))]
[JsonSerializable(typeof(RemoteConnectionInfo))]
[JsonSerializable(typeof(CommandFormBuildRequest))]
[JsonSerializable(typeof(CommandFormResult))]
[JsonSerializable(typeof(ExpressionValidation))]
[JsonSerializable(typeof(SnippetCreateRequest))]
[JsonSerializable(typeof(SnippetImportRequest))]
[JsonSerializable(typeof(SnippetLoadResult))]
[JsonSerializable(typeof(ScriptAnalysis))]
[JsonSerializable(typeof(CompletionSet))]
[JsonSerializable(typeof(CommandDescription[]))]
[JsonSerializable(typeof(CommandFormDescription))]
[JsonSerializable(typeof(CommandHelpDocument))]
[JsonSerializable(typeof(DebugSnapshot))]
[JsonSerializable(typeof(DebugChildren))]
[JsonSerializable(typeof(DebugBreakpoint))]
[JsonSerializable(typeof(DebugBreakpoint[]))]
[JsonSerializable(typeof(OutputEntry))]
[JsonSerializable(typeof(ProgressUpdate))]
[JsonSerializable(typeof(DebugLocation))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(JsonElement))]
public partial class IseBridgeJsonContext : JsonSerializerContext;

public static class BridgeJson
{
    public static JsonElement Element<T>(T value) =>
        JsonSerializer.SerializeToElement(value, TypeInfo<T>());
    public static T Read<T>(JsonElement value) =>
        value.Deserialize(TypeInfo<T>()) ?? throw new InvalidDataException($"Missing {typeof(T).Name} bridge payload.");
    public static System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> TypeInfo<T>() =>
        (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)(IseBridgeJsonContext.Default.GetTypeInfo(typeof(T))
            ?? throw new InvalidOperationException($"No private JSON contract for {typeof(T).Name}."));
    public static JsonElement Empty => Element(true);
}
