using System.Text;
using System.Text.Json;

namespace Devolutions.Terminal.Core;

public sealed record AsciicastFrame(double Timestamp, string Data);

public enum AsciicastFormat
{
    V2 = 2,
    V3 = 3,
}

public sealed class AsciicastRecording
{
    private readonly Dictionary<string, string> _env;

    public AsciicastRecording(
        int width,
        int height,
        string? title = null,
        AsciicastFormat format = AsciicastFormat.V2)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        Title = title;
        Format = format;
        _env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TERM"] = "xterm-256color",
        };
    }

    public int Version => (int)Format;
    public AsciicastFormat Format { get; }
    public int Width { get; }
    public int Height { get; }
    public long Timestamp { get; set; }
    public string? Title { get; set; }
    public IReadOnlyDictionary<string, string> Env => _env;
    public List<AsciicastFrame> Frames { get; } = [];

    public void AppendFrame(double timestamp, string data)
    {
        if (!double.IsFinite(timestamp) ||
            timestamp < 0 ||
            timestamp > TimeSpan.MaxValue.TotalSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timestamp),
                timestamp,
                "An asciicast event timestamp must be a finite, non-negative number of seconds.");
        }

        ArgumentNullException.ThrowIfNull(data);
        Frames.Add(new AsciicastFrame(timestamp, data));
    }

    public AsciicastRecording Copy(AsciicastFormat? format = null)
    {
        var copy = new AsciicastRecording(Width, Height, Title, format ?? Format)
        {
            Timestamp = Timestamp,
        };
        copy._env.Clear();
        foreach (var pair in _env)
        {
            copy._env[pair.Key] = pair.Value;
        }

        copy.Frames.AddRange(Frames);
        return copy;
    }

    public static AsciicastRecording FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using var reader = new StringReader(json);
        var headerLine = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(headerLine))
        {
            throw InvalidRecording();
        }

        using var headerDocument = JsonDocument.Parse(headerLine);
        var header = headerDocument.RootElement;
        if (header.ValueKind != JsonValueKind.Object ||
            !header.TryGetProperty("version", out var versionElement) ||
            !versionElement.TryGetInt32(out var version) ||
            version is not (2 or 3))
        {
            throw InvalidRecording();
        }

        var format = (AsciicastFormat)version;
        var width = format == AsciicastFormat.V3
            ? ReadPositiveInt(header, "term", "cols", 80)
            : ReadPositiveInt(header, "width", 80);
        var height = format == AsciicastFormat.V3
            ? ReadPositiveInt(header, "term", "rows", 24)
            : ReadPositiveInt(header, "height", 24);
        var title = header.TryGetProperty("title", out var titleElement) &&
                    titleElement.ValueKind == JsonValueKind.String
            ? titleElement.GetString()
            : null;

        var recording = new AsciicastRecording(width, height, title, format);
        if (header.TryGetProperty("timestamp", out var timestampElement) &&
            timestampElement.TryGetInt64(out var timestamp))
        {
            recording.Timestamp = timestamp;
        }

        if (header.TryGetProperty("env", out var envElement) &&
            envElement.ValueKind == JsonValueKind.Object)
        {
            recording._env.Clear();
            foreach (var property in envElement.EnumerateObject())
            {
                recording._env[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.ToString();
            }
        }

        if (format == AsciicastFormat.V3 &&
            header.TryGetProperty("term", out var termElement) &&
            termElement.ValueKind == JsonValueKind.Object &&
            termElement.TryGetProperty("type", out var typeElement) &&
            typeElement.ValueKind == JsonValueKind.String &&
            (!header.TryGetProperty("env", out var headerEnv) ||
             headerEnv.ValueKind != JsonValueKind.Object ||
             !headerEnv.TryGetProperty("TERM", out _)))
        {
            recording._env["TERM"] = typeElement.GetString() ?? string.Empty;
        }

        var timestampOffset = 0d;
        while (reader.ReadLine() is { } eventLine)
        {
            if (eventLine.StartsWith('#') || string.IsNullOrWhiteSpace(eventLine))
            {
                continue;
            }

            using var eventDocument = JsonDocument.Parse(eventLine);
            var entry = eventDocument.RootElement;
            if (entry.ValueKind != JsonValueKind.Array ||
                entry.GetArrayLength() != 3 ||
                entry[0].ValueKind != JsonValueKind.Number ||
                entry[1].ValueKind != JsonValueKind.String ||
                entry[2].ValueKind != JsonValueKind.String)
            {
                throw InvalidRecording();
            }

            var eventTime = entry[0].GetDouble();
            if (format == AsciicastFormat.V3)
            {
                if (!double.IsFinite(eventTime) || eventTime < 0)
                {
                    throw InvalidRecording();
                }

                timestampOffset += eventTime;
                eventTime = timestampOffset;
            }

            var eventType = entry[1].GetString();
            if (eventType == "o")
            {
                recording.AppendFrame(eventTime, entry[2].GetString() ?? string.Empty);
            }
        }

        return recording;
    }

    public string ToJson() => ToJson(Format);

    public string ToJson(AsciicastFormat format)
    {
        using var stream = new MemoryStream();
        WriteHeader(stream, format);
        var previousTimestamp = 0d;
        foreach (var frame in Frames)
        {
            var timestamp = format == AsciicastFormat.V3
                ? Math.Max(0, frame.Timestamp - previousTimestamp)
                : frame.Timestamp;
            WriteEvent(stream, timestamp, frame.Data);
            previousTimestamp = frame.Timestamp;
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal byte[] CreateHeaderLine(AsciicastFormat format)
    {
        using var stream = new MemoryStream();
        WriteHeader(stream, format);
        return stream.ToArray();
    }

    internal static byte[] CreateEventLine(double timestamp, string data, AsciicastFormat format = AsciicastFormat.V2)
    {
        using var stream = new MemoryStream();
        WriteEvent(stream, timestamp, data, format);
        return stream.ToArray();
    }

    private void WriteHeader(Stream stream, AsciicastFormat format)
    {
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", (int)format);
            if (format == AsciicastFormat.V3)
            {
                writer.WritePropertyName("term");
                writer.WriteStartObject();
                writer.WriteNumber("cols", Width);
                writer.WriteNumber("rows", Height);
                if (Env.TryGetValue("TERM", out var terminalType))
                {
                    writer.WriteString("type", terminalType);
                }

                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNumber("width", Width);
                writer.WriteNumber("height", Height);
            }

            writer.WriteNumber("timestamp", Timestamp);
            if (Title is not null)
            {
                writer.WriteString("title", Title);
            }

            writer.WritePropertyName("env");
            writer.WriteStartObject();
            foreach (var pair in Env)
            {
                writer.WriteString(pair.Key, pair.Value);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
    }

    private static void WriteEvent(Stream stream, double timestamp, string data, AsciicastFormat format = AsciicastFormat.V2)
    {
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(timestamp);
            writer.WriteStringValue("o");
            writer.WriteStringValue(data);
            writer.WriteEndArray();
        }

        if (format == AsciicastFormat.V3)
        {
            stream.WriteByte((byte)'\n');
            return;
        }

        stream.WriteByte((byte)'\n');
    }

    private static int ReadPositiveInt(JsonElement element, string name, int fallback) =>
        element.TryGetProperty(name, out var property) &&
        property.TryGetInt32(out var value) &&
        value > 0
            ? value
            : fallback;

    private static int ReadPositiveInt(
        JsonElement element,
        string parentName,
        string name,
        int fallback)
    {
        return element.TryGetProperty(parentName, out var parent) &&
               parent.ValueKind == JsonValueKind.Object
            ? ReadPositiveInt(parent, name, fallback)
            : fallback;
    }

    private static InvalidOperationException InvalidRecording() =>
        new("The selected file is not a valid asciicast v2 or v3 recording.");
}
