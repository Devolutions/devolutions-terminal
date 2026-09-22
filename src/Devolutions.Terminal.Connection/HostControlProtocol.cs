using System.Text.Json.Serialization;

namespace Devolutions.Terminal.Connection;

/// <summary>
/// JSON control channel for the loopback host. The browser may list profiles
/// and launch by id. It cannot supply a command line.
/// </summary>
public static class HostControlProtocol
{
    public const string Hello = "hello";
    public const string ListProfiles = "listProfiles";
    public const string Profiles = "profiles";
    public const string ListSessions = "listSessions";
    public const string Sessions = "sessions";
    public const string Launch = "launch";
    public const string Launched = "launched";
    public const string Close = "close";
    public const string Closed = "closed";
    public const string Error = "error";

    public const int MaxMessageBytes = 256 * 1024;
    public const int MaxSessions = 32;

    public static bool IsSessionTransport(string? transport, string sessionId) =>
        HostPtyProtocol.TryParseSessionId(transport, out var parsed) &&
        parsed.Equals(sessionId, StringComparison.OrdinalIgnoreCase);
}

public sealed record HostControlMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("requestId")]
    public string? RequestId { get; init; }

    [JsonPropertyName("profileId")]
    public string? ProfileId { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }

    [JsonPropertyName("cols")]
    public int? Cols { get; init; }

    [JsonPropertyName("rows")]
    public int? Rows { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>
    /// Relative session path (<c>/pty/{id}</c>). Never an absolute or off-host URL.
    /// </summary>
    [JsonPropertyName("transport")]
    public string? Transport { get; init; }

    /// <summary>
    /// <c>wss</c> when the page is HTTPS, otherwise <c>ws</c>. The client still
    /// builds the socket from its own page origin.
    /// </summary>
    [JsonPropertyName("scheme")]
    public string? Scheme { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("defaultProfileId")]
    public string? DefaultProfileId { get; init; }

    [JsonPropertyName("profiles")]
    public List<HostProfileInfo>? Profiles { get; init; }

    [JsonPropertyName("sessions")]
    public List<HostSessionInfo>? Sessions { get; init; }
}

public sealed record HostProfileInfo
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("source")]
    public string? Source { get; init; }

    /// <summary>
    /// Server-side only. A local command line is never written to the browser.
    /// </summary>
    [JsonIgnore]
    public string? CommandLine { get; init; }

    /// <summary>Server-side only. A local path is never written to the browser.</summary>
    [JsonIgnore]
    public string? StartingDirectory { get; init; }

    [JsonPropertyName("colorScheme")]
    public string? ColorScheme { get; init; }

    [JsonPropertyName("launchable")]
    public bool Launchable { get; init; }

    [JsonPropertyName("hidden")]
    public bool Hidden { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed record HostSessionInfo
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("profileId")]
    public string ProfileId { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("transport")]
    public string Transport { get; init; } = "";
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HostControlMessage))]
[JsonSerializable(typeof(HostProfileInfo))]
[JsonSerializable(typeof(HostSessionInfo))]
public sealed partial class HostControlJsonContext : JsonSerializerContext;
