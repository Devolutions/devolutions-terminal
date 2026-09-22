using System.Security.Cryptography;
using System.Text;
using Devolutions.Terminal.Connection;
using Devolutions.Terminal.Settings;

namespace Devolutions.Terminal.Browser.Host;

/// <summary>
/// Profiles the loopback host is willing to launch. Command lines are resolved
/// here and are never taken from the browser.
/// </summary>
public sealed class HostProfileCatalog
{
    private readonly Dictionary<string, TerminalLaunchOptions> _launches;

    private HostProfileCatalog(
        IReadOnlyList<HostProfileInfo> profiles,
        Dictionary<string, TerminalLaunchOptions> launches,
        string? defaultProfileId)
    {
        Profiles = profiles;
        _launches = launches;
        DefaultProfileId = defaultProfileId;
    }

    public IReadOnlyList<HostProfileInfo> Profiles { get; }

    public string? DefaultProfileId { get; }

    public static HostProfileCatalog Load()
    {
        try
        {
            return FromSettings(SettingsService.Load());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"settings load failed: {ex.Message}");
            return FromSettings(CreateFallbackSettings());
        }
    }

    public static HostProfileCatalog FromSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var profiles = new List<HostProfileInfo>();
        var launches = new Dictionary<string, TerminalLaunchOptions>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in settings.Profiles)
        {
            if (profile.Orphaned)
            {
                continue;
            }

            var id = NormalizeId(profile);
            if (!seen.Add(id))
            {
                continue;
            }

            var commandLine = profile.ExpandCommandline();
            var directory = profile.ExpandStartingDirectory();
            var launchable = IsLaunchable(profile, commandLine, out var reason);
            profiles.Add(new HostProfileInfo
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(profile.Name) ? "Profile" : profile.Name,
                Source = profile.Origin == SettingsOrigin.None ? profile.Source : profile.Origin.ToString(),
                ColorScheme = profile.ColorScheme,
                Launchable = launchable,
                Hidden = profile.Hidden,
                Reason = reason,
            });
            if (!launchable)
            {
                continue;
            }

            launches[id] = new TerminalLaunchOptions
            {
                CommandLine = commandLine,
                WorkingDirectory = directory,
                InheritEnvironment = true,
                ReloadEnvironmentVariables = profile.ReloadEnvironmentVariables,
                EnvironmentVariables = new Dictionary<string, string?>(profile.Environment, StringComparer.OrdinalIgnoreCase),
                IsDefaultTerminalSession = false,
            };
        }

        var fallback = settings.GetDefaultProfile();
        var defaultId = profiles.FirstOrDefault(profile =>
            profile.Launchable &&
            string.Equals(profile.Id, NormalizeId(fallback), StringComparison.OrdinalIgnoreCase))?.Id
            ?? profiles.FirstOrDefault(profile => profile.Launchable)?.Id;
        return new HostProfileCatalog(profiles, launches, defaultId);
    }

    public TerminalLaunchOptions CreateLaunchOptions(HostProfileInfo profile, int columns, int rows)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!_launches.TryGetValue(profile.Id, out var options))
        {
            throw new InvalidOperationException("Profile is not launchable.");
        }

        return options with
        {
            Columns = columns,
            Rows = rows,
        };
    }

    public HostSessionBroker CreateBroker() =>
        new(Profiles, CreateLaunchOptions, connectionFactory: null, DefaultProfileId);

    private static bool IsLaunchable(ProfileSettings profile, string commandLine, out string? reason)
    {
        if (Guid.TryParse(profile.ConnectionType, out var connectionType) &&
            connectionType == AzureCloudShellConnection.ConnectionTypeGuid)
        {
            reason = "Azure Cloud Shell is not bridged by this host.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(commandLine) ||
            string.Equals(commandLine, "dt-wasm", StringComparison.OrdinalIgnoreCase))
        {
            reason = "This profile has no local command line.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(profile.ConnectionType) &&
            !Guid.TryParse(profile.ConnectionType, out _))
        {
            reason = "Connection type is not available on this host.";
            return false;
        }

        if (profile.Elevate)
        {
            reason = "Elevation is not available from the browser host.";
            return false;
        }

        reason = null;
        return true;
    }

    private static AppSettings CreateFallbackSettings()
    {
        if (OperatingSystem.IsWindows())
        {
            return new AppSettings
            {
                Profiles =
                {
                    ProfileSettings.CreatePowerShell(),
                    ProfileSettings.CreateCmd(),
                },
                DefaultProfile = ProfileSettings.CreatePowerShell().Guid,
            };
        }

        var shell = OperatingSystem.IsMacOS() ? "/bin/zsh" : "/bin/bash";
        var profile = new ProfileSettings
        {
            Guid = "{7c4e2a91-1b6d-4f08-9c33-5e8a0d2b6f14}",
            Name = OperatingSystem.IsMacOS() ? "Zsh" : "Bash",
            Commandline = shell,
            Origin = SettingsOrigin.Generated,
        };
        return new AppSettings
        {
            Profiles = { profile },
            DefaultProfile = profile.Guid,
        };
    }

    private static string NormalizeId(ProfileSettings profile)
    {
        if (Guid.TryParse(profile.Guid, out var parsed))
        {
            return parsed.ToString("B");
        }

        var material = profile.Name + "\0" + profile.Commandline + "\0" + profile.Source;
        return "name:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }
}
