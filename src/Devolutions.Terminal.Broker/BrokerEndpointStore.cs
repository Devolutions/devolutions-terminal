using System.Security.Cryptography;
using System.Text.Json;

namespace Devolutions.Terminal.Broker;

public sealed class BrokerEndpointStore
{
    private readonly string _endpointPath;

    public BrokerEndpointStore(string? rootDirectory = null, string instanceKey = "default")
    {
        var root = rootDirectory ??
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Devolutions",
                "Terminal");
        Directory.CreateDirectory(root);
        _endpointPath = Path.Combine(root, $"broker-v{BrokerProtocol.Version}-{SafeName(instanceKey)}.json");
    }

    public string EndpointPath => _endpointPath;

    public BrokerEndpoint? Read()
    {
        try
        {
            // Allow the host to replace or delete the file while a client reads it.
            using var stream = new FileStream(
                _endpointPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize(stream, BrokerJsonContext.Default.BrokerEndpoint);
        }
        catch (IOException)
        {
            // Missing, or briefly locked by a concurrent replace: no broker to use.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Write(BrokerEndpoint endpoint)
    {
        var temporaryPath = $"{_endpointPath}.{Environment.ProcessId}.{RandomNumberGenerator.GetHexString(8)}.tmp";
        using (var stream = new FileStream(
                   temporaryPath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None,
                   4096,
                   FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, endpoint, BrokerJsonContext.Default.BrokerEndpoint);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporaryPath, _endpointPath, overwrite: true);
    }

    public void DeleteIfMatches(BrokerEndpoint endpoint)
    {
        var current = Read();
        if (current == endpoint)
        {
            try
            {
                File.Delete(_endpointPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A stale endpoint is harmless: clients validate it before use.
            }
        }
    }

    private static string SafeName(string value)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes.AsSpan(0, 8));
    }
}
