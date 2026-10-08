#if POWERSHELL_ISE
using System.Diagnostics;
using System.Text.Json;
using Iseberg.Core;

namespace Devolutions.Terminal.App.Views;

public static class PowerShellIseWorkspaceCatalog
{
    public static async Task RecordProfileAsync(string workspaceDirectory, string? profileId)
    {
        if (!Path.IsPathFullyQualified(workspaceDirectory)) throw new ArgumentException("An absolute workspace directory is required.", nameof(workspaceDirectory));
        if (string.IsNullOrWhiteSpace(profileId)) return;
        var path = Path.Combine(workspaceDirectory, "profile.json");
        if (File.Exists(path))
        {
            var ownerProfile = JsonSerializer.Deserialize<string>(await File.ReadAllTextAsync(path))
                ?? throw new InvalidDataException($"Empty ISE profile identity: {path}");
            if (!string.Equals(ownerProfile, profileId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("This ISE workspace belongs to a different profile.");
            return;
        }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(profileId));
            File.Move(temporary, path, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>Finds one abandoned dirty workspace for the same profile; never consumes another live tab.</summary>
    public static async Task<Guid?> FindRecoverableAsync(string stateRoot, string? profileId)
    {
        if (!Path.IsPathFullyQualified(stateRoot)) throw new ArgumentException("An absolute state root is required.", nameof(stateRoot));
        if (string.IsNullOrWhiteSpace(profileId)) return null;
        var workspaces = Path.Combine(stateRoot, "Workspaces");
        if (!Directory.Exists(workspaces)) return null;
        foreach (var directory in Directory.EnumerateDirectories(workspaces).OrderByDescending(Directory.GetLastWriteTimeUtc))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var id)) continue;
            var marker = Path.Combine(directory, "profile.json");
            if (!File.Exists(marker)) continue;
            var ownerProfile = JsonSerializer.Deserialize<string>(await File.ReadAllTextAsync(marker))
                ?? throw new InvalidDataException($"Empty ISE profile identity: {marker}");
            if (!string.Equals(ownerProfile, profileId, StringComparison.OrdinalIgnoreCase)) continue;
            var settings = Path.Combine(directory, "settings.json");
            var state = await new WorkbenchStateStore(settings + ".workbench.json").LoadAsync();
            if (state is null) continue;
            WorkbenchPersistenceLease lease;
            try { lease = new(settings); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 or 11 or 35)
            {
                Trace.TraceInformation("ISE recovery workspace is still owned: {0}", directory);
                continue;
            }
            using (lease)
                if ((await new ScriptRecovery(settings + ".recovery").ReadAsync()).Count > 0) return id;
        }
        return null;
    }
}
#endif
