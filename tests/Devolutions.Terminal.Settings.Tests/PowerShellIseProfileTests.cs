using System.Text.Json.Nodes;
using Xunit;

namespace Devolutions.Terminal.Settings.Tests;

public sealed class PowerShellIseProfileTests
{
    private const string Defaults = """{"profiles":{"list":[]},"schemes":[{"name":"Campbell"}]}""";
    private const string Id = "{12345678-1234-5678-9abc-123456789abc}";
    private const string UnknownId = "{87654321-4321-8765-abcd-987654321abc}";

    [Theory]
    [InlineData("Windows PowerShell", @"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe")]
    [InlineData("PowerShell", "pwsh.exe")]
    public void LegacyPowerShellWithoutTypeIsTerminal(string name, string commandline)
    {
        var input = new JsonObject
        {
            ["guid"] = Id, ["name"] = name, ["commandline"] = commandline,
        };
        var profile = Assert.Single(SettingsLoader.Load(Defaults, Document(input)).Profiles);

        Assert.Equal(ProfileKind.Terminal, profile.Kind);
        Assert.Equal(commandline, profile.Commandline);
        Assert.Equal(Guid.Parse(Id), Guid.Parse(profile.Guid!));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void IseDiscriminatorAndExplicitOptInResolve(bool? option, bool expected)
    {
        var input = IseNode();
        if (option.HasValue) input["ise.loadProfiles"] = option.Value;
        var profile = Assert.Single(SettingsLoader.Load(Defaults, Document(input)).Profiles);

        Assert.Equal(ProfileKind.PowerShellIse, profile.Kind);
        Assert.Equal(expected, profile.IseLoadProfiles);
        Assert.Empty(profile.Commandline);
    }

    [Fact]
    public void UnknownProfileTypeIsUnsupportedWithError()
    {
        var input = IseNode();
        input["type"] = "futureWorkbench";
        var settings = SettingsLoader.Load(Defaults, Document(input));

        Assert.Equal(ProfileKind.Unsupported, Assert.Single(settings.Profiles).Kind);
        Assert.Contains(settings.Diagnostics, diagnostic =>
            diagnostic.Code == "UnsupportedProfileType" &&
            diagnostic.Severity == SettingsDiagnosticSeverity.Error);
        var saved = JsonNode.Parse(SettingsLoader.SerializeUserDocument(settings))!;
        Assert.Equal("futureWorkbench", saved["profiles"]!["list"]![0]!["type"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void EditedIseProfilePreservesKindOptionsIdentityAndUnknownFields(bool initial, bool edited)
    {
        var ise = IseNode();
        ise["source"] = "regression-provider";
        ise["ise.loadProfiles"] = initial;
        ise["futureProfile"] = JsonNode.Parse("""{"nested":[7,{"keep":"value"}]}""");
        var unsupported = new JsonObject
        {
            ["guid"] = UnknownId, ["name"] = "Future", ["type"] = "futureWorkbench",
            ["futureSibling"] = new JsonObject { ["keep"] = 42 },
        };
        var original = Document(ise, unsupported);
        var settings = SettingsLoader.Load(Defaults, original);
        var profile = settings.Profiles.Single(p => Guid.Parse(p.Guid!) == Guid.Parse(Id));
        profile.Name = "Edited Iseberg";
        profile.FontFace = "Consolas";
        profile.FontSize = 15;
        profile.StartingDirectory = @"C:\acceptance\scripts";
        profile.IseLoadProfiles = edited;

        var serialized = SettingsLoader.SerializeUserDocument(settings);
        var json = JsonNode.Parse(serialized)!;
        var saved = json["profiles"]!["list"]!.AsArray().Single(p => Guid.Parse(p!["guid"]!.GetValue<string>()) == Guid.Parse(Id))!;
        Assert.Equal("powershellIse", saved["type"]!.GetValue<string>());
        Assert.Equal(edited, saved["ise.loadProfiles"]!.GetValue<bool>());
        Assert.Equal(Guid.Parse(Id), Guid.Parse(saved["guid"]!.GetValue<string>()));
        Assert.Equal("regression-provider", saved["source"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(ise["futureProfile"], saved["futureProfile"]));
        Assert.Equal(19, json["futureRoot"]!["nested"]![0]!.GetValue<int>());
        var future = json["profiles"]!["list"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == "Future")!;
        Assert.Equal("futureWorkbench", future["type"]!.GetValue<string>());
        Assert.Equal(42, future["futureSibling"]!["keep"]!.GetValue<int>());

        var reloaded = SettingsLoader.Load(Defaults, serialized);
        var restored = Assert.Single(reloaded.Profiles, p => Guid.Parse(p.Guid!) == Guid.Parse(Id));
        Assert.Equal(ProfileKind.PowerShellIse, restored.Kind);
        Assert.Equal(edited, restored.IseLoadProfiles);
        Assert.Equal("Edited Iseberg", restored.Name);
        Assert.Equal("Consolas", restored.FontFace);
        Assert.Equal(15, restored.FontSize);
        Assert.Equal(@"C:\acceptance\scripts", restored.StartingDirectory);
        Assert.Equal("regression-provider", restored.Source);
        Assert.Equal(ProfileKind.Unsupported, reloaded.Profiles.Single(p => Guid.Parse(p.Guid!) == Guid.Parse(UnknownId)).Kind);
        Assert.Contains(reloaded.Diagnostics, d => d.Code == "UnsupportedProfileType" && d.Severity == SettingsDiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddedIseProfileRoundTripsKindOptionsAndIdentity(bool option)
    {
        var existing = new JsonObject
        {
            ["guid"] = Id, ["name"] = "Legacy", ["commandline"] = "pwsh.exe",
            ["futureProfile"] = new JsonObject { ["keep"] = 42 },
        };
        var settings = SettingsLoader.Load(Defaults, Document(existing));
        var added = ProfileSettings.CreatePowerShellIse();
        added.IseLoadProfiles = option;
        settings.Profiles.Add(added);

        var serialized = SettingsLoader.SerializeUserDocument(settings);
        var json = JsonNode.Parse(serialized)!;
        Assert.Equal(19, json["futureRoot"]!["nested"]![0]!.GetValue<int>());
        Assert.Equal(42, json["profiles"]!["list"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == "Legacy")!["futureProfile"]!["keep"]!.GetValue<int>());
        var saved = json["profiles"]!["list"]!.AsArray().Single(p => Guid.Parse(p!["guid"]!.GetValue<string>()) == Guid.Parse(added.Guid!))!;
        Assert.Equal("powershellIse", saved["type"]!.GetValue<string>());
        Assert.Equal(option, saved["ise"]!["loadProfiles"]!.GetValue<bool>());
        Assert.Equal("Classic ISE", saved["ise"]!["colorTheme"]!.GetValue<string>());
        Assert.True(saved["ise"]!["showLineNumbers"]!.GetValue<bool>());
        Assert.False(saved["ise"]!["wordWrap"]!.GetValue<bool>());
        Assert.True(saved["ise"]!["promptToSaveBeforeRun"]!.GetValue<bool>());
        Assert.Equal(2, saved["ise"]!["autoSaveMinutes"]!.GetValue<int>());
        Assert.False(saved.AsObject().ContainsKey("ise.loadProfiles"));
        Assert.False(saved.AsObject().ContainsKey("ise.colorTheme"));
        var restored = Assert.Single(SettingsLoader.Load(Defaults, serialized).Profiles, p => Guid.Parse(p.Guid!) == Guid.Parse(added.Guid!));
        Assert.Equal("Iseberg", restored.Name);
        Assert.Equal(ProfileKind.PowerShellIse, restored.Kind);
        Assert.Equal(option, restored.IseLoadProfiles);
    }

    [Fact]
    public void NewIseProfileUsesIsebergNameAndDistinctIdentity()
    {
        var first = ProfileSettings.CreatePowerShellIse();
        var second = ProfileSettings.CreatePowerShellIse();
        Assert.All(new[] { first, second }, profile =>
        {
            Assert.Equal("Iseberg", profile.Name);
            Assert.Equal(SettingsOrigin.User, profile.Origin);
            Assert.Equal(ProfileKind.PowerShellIse, profile.Kind);
            Assert.Empty(profile.Commandline);
            Assert.False(profile.IseLoadProfiles);
            Assert.NotEqual(Guid.Empty, Guid.Parse(profile.Guid!));
        });
        Assert.NotEqual(Guid.Parse(first.Guid!), Guid.Parse(second.Guid!));
    }

    [Fact]
    public void TerminalProfilesAndOverridesRemainTerminal()
    {
        Assert.All(new[] { new ProfileSettings(), ProfileSettings.CreatePowerShell(), ProfileSettings.CreatePwsh() },
            profile =>
            {
                Assert.Equal(ProfileKind.Terminal, profile.Kind);
                Assert.Equal(ProfileKind.Terminal, profile.WithOverrides(new NewTerminalArgs(TabTitle: "edited")).Kind);
            });
    }

    [Theory]
    [InlineData(ProfileKind.Terminal, false)]
    [InlineData(ProfileKind.Terminal, true)]
    [InlineData(ProfileKind.PowerShellIse, false)]
    [InlineData(ProfileKind.PowerShellIse, true)]
    [InlineData(ProfileKind.Unsupported, false)]
    [InlineData(ProfileKind.Unsupported, true)]
    public void OverridesPreserveProfileKindAndIseOptions(ProfileKind kind, bool option)
    {
        var original = new ProfileSettings
        {
            Kind = kind, IseLoadProfiles = option, Guid = Id,
            StartingDirectory = @"C:\original", TabTitle = "original",
        };
        var result = original.WithOverrides(new NewTerminalArgs(StartingDirectory: @"C:\edited", TabTitle: "edited"));

        Assert.NotSame(original, result);
        Assert.Equal(kind, result.Kind);
        Assert.Equal(option, result.IseLoadProfiles);
        Assert.Equal(Guid.Parse(Id), Guid.Parse(result.Guid!));
        Assert.Equal(@"C:\edited", result.StartingDirectory);
        Assert.Equal("edited", result.TabTitle);
        Assert.Equal(@"C:\original", original.StartingDirectory);
        Assert.Equal("original", original.TabTitle);
    }

    [Fact]
    public void IseThemeDefaultsToClassicWithoutAddingThemeToLegacyTerminal()
    {
        Assert.Equal("Classic ISE", ProfileSettings.CreatePowerShellIse().IseColorTheme);
        foreach (var explicitNull in new[] { false, true })
        {
            var input = IseNode();
            if (explicitNull) input["ise.colorTheme"] = null;
            var profile = Assert.Single(SettingsLoader.Load(Defaults, Document(input)).Profiles);
            Assert.Equal("Classic ISE", profile.IseColorTheme);
            Assert.Equal(ProfileKind.PowerShellIse, profile.Kind);
            Assert.False(profile.IseLoadProfiles);
        }
        var legacy = SettingsLoader.Load(Defaults, Document(new JsonObject
        {
            ["guid"] = Id, ["name"] = "PowerShell", ["commandline"] = "pwsh.exe",
        }));
        Assert.Equal(ProfileKind.Terminal, Assert.Single(legacy.Profiles).Kind);
        var saved = JsonNode.Parse(SettingsLoader.SerializeUserDocument(legacy))!["profiles"]!["list"]![0]!.AsObject();
        Assert.False(saved.ContainsKey("ise.colorTheme"));
        Assert.False(saved.ContainsKey("type"));
    }

    [Theory]
    [InlineData("classic ise", "Classic ISE")]
    [InlineData("dArK", "Dark")]
    [InlineData("LIGHT", "Light")]
    [InlineData("follow dt", "Follow DT")]
    public void SupportedIseThemeNamesCanonicalizeAndPreserveUneditedJson(string input, string expected)
    {
        Assert.Equal(new[]
        {
            "Dark Console, Light Editor (default)", "Light Console, Dark Editor", "Dark Console, Dark Editor",
            "Light Console, Light Editor", "Monochrome Green", "Presentation",
            "Classic ISE", "Dark", "Light", "Follow DT"
        }, IsebergThemes.Choices);
        Assert.True(IsebergThemes.IsSupported(input));
        Assert.Equal(expected, IsebergThemes.Canonicalize(input));
        var node = IseNode();
        node["ise.colorTheme"] = input;
        var settings = SettingsLoader.Load(Defaults, Document(node));
        Assert.Equal(expected, Assert.Single(settings.Profiles).IseColorTheme);
        var serialized = SettingsLoader.SerializeUserDocument(settings);
        var saved = JsonNode.Parse(serialized)!["profiles"]!["list"]![0]!;
        Assert.Equal(input, saved["ise.colorTheme"]!.GetValue<string>());
        Assert.Equal("powershellIse", saved["type"]!.GetValue<string>());
        Assert.Equal(expected, Assert.Single(SettingsLoader.Load(Defaults, serialized).Profiles).IseColorTheme);
    }

    [Theory]
    [InlineData("Classic ISE")]
    [InlineData("Dark")]
    [InlineData("Light")]
    [InlineData("Follow DT")]
    public void EditedAndAddedIseThemesPreserveIdentityOptionsAndUnknownFields(string theme)
    {
        var node = IseNode();
        node["ise.colorTheme"] = "Dark";
        node["futureThemeOptions"] = JsonNode.Parse("""{"contrast":17,"keep":[true,"future"]}""");
        var settings = SettingsLoader.Load(Defaults, Document(node));
        var edited = Assert.Single(settings.Profiles);
        Assert.Equal("Dark", edited.IseColorTheme);
        edited.IseColorTheme = theme;
        edited.IseLoadProfiles = true;
        edited.Name = "Edited theme";
        var added = ProfileSettings.CreatePowerShellIse();
        added.IseColorTheme = theme;
        settings.Profiles.Add(added);
        var overridden = edited.WithOverrides(new NewTerminalArgs(TabTitle: "overridden"));
        Assert.NotSame(edited, overridden);
        Assert.Equal(theme, overridden.IseColorTheme);
        Assert.Equal(ProfileKind.PowerShellIse, overridden.Kind);
        Assert.True(overridden.IseLoadProfiles);
        Assert.Equal(Id, overridden.Guid);
        Assert.Null(edited.TabTitle);
        var serialized = SettingsLoader.SerializeUserDocument(settings);
        var savedProfiles = JsonNode.Parse(serialized)!["profiles"]!["list"]!.AsArray();
        foreach (var profile in new[] { edited, added })
        {
            var saved = Assert.Single(savedProfiles, item => Guid.Parse(item!["guid"]!.GetValue<string>()) == Guid.Parse(profile.Guid!))!;
            if (ReferenceEquals(profile, edited))
            {
                Assert.Equal(theme, saved["ise.colorTheme"]!.GetValue<string>());
                Assert.True(saved["ise"]!["loadProfiles"]!.GetValue<bool>());
                if (theme == "Dark")
                    Assert.Null(saved["ise"]!["colorTheme"]);
                else
                    Assert.Equal(theme, saved["ise"]!["colorTheme"]!.GetValue<string>());
            }
            else
            {
                Assert.Equal(theme, saved["ise"]!["colorTheme"]!.GetValue<string>());
                Assert.False(saved.AsObject().ContainsKey("ise.colorTheme"));
            }
            Assert.Equal("powershellIse", saved["type"]!.GetValue<string>());
        }
        Assert.True(JsonNode.DeepEquals(node["futureThemeOptions"],
            savedProfiles.Single(item => item!["name"]!.GetValue<string>() == "Edited theme")!["futureThemeOptions"]));
        var restored = SettingsLoader.Load(Defaults, serialized);
        var restoredEdited = Assert.Single(restored.Profiles, profile => Guid.Parse(profile.Guid!) == Guid.Parse(Id));
        Assert.Equal(theme, restoredEdited.IseColorTheme);
        Assert.Equal("Edited theme", restoredEdited.Name);
        Assert.True(restoredEdited.IseLoadProfiles);
        Assert.Equal(theme, Assert.Single(restored.Profiles, profile => Guid.Parse(profile.Guid!) == Guid.Parse(added.Guid!)).IseColorTheme);
    }

    [Theory]
    [InlineData("Future Theme")]
    [InlineData("")]
    public void UnknownIseThemeIsPreservedWithoutDefaultOrTerminalFallback(string theme)
    {
        Assert.False(IsebergThemes.IsSupported(theme));
        Assert.Equal(theme, IsebergThemes.Canonicalize(theme));
        var node = IseNode();
        node["ise.colorTheme"] = theme;
        var settings = SettingsLoader.Load(Defaults, Document(node));
        var profile = Assert.Single(settings.Profiles);
        Assert.Equal(theme, profile.IseColorTheme);
        Assert.Equal(ProfileKind.PowerShellIse, profile.Kind);
        profile.Name = "Edited unknown theme";
        var serialized = SettingsLoader.SerializeUserDocument(settings);
        Assert.Equal(theme, JsonNode.Parse(serialized)!["profiles"]!["list"]![0]!["ise.colorTheme"]!.GetValue<string>());
        var restored = Assert.Single(SettingsLoader.Load(Defaults, serialized).Profiles);
        Assert.Equal(theme, restored.IseColorTheme);
        Assert.Equal(ProfileKind.PowerShellIse, restored.Kind);
        Assert.Equal(Guid.Parse(Id), Guid.Parse(restored.Guid!));
    }

    private static JsonObject IseNode() => new()
    {
        ["guid"] = Id, ["name"] = "Iseberg", ["type"] = "powershellIse", ["commandline"] = "",
    };

    private static string Document(params JsonObject[] profiles) => new JsonObject
    {
        ["futureRoot"] = JsonNode.Parse("""{"nested":[19,{"keep":true}]}"""),
        ["profiles"] = new JsonObject { ["list"] = new JsonArray(profiles.Cast<JsonNode?>().ToArray()) },
    }.ToJsonString();

    [Theory]
    [InlineData("Dark Console, Light Editor (default)")]
    [InlineData("Light Console, Dark Editor")]
    [InlineData("Dark Console, Dark Editor")]
    [InlineData("Light Console, Light Editor")]
    [InlineData("Monochrome Green")]
    [InlineData("Presentation")]
    public void OriginalBuiltInThemesRoundTripProfilesWithoutChangingOrdinaryTerminalOrUnknownFields(string theme)
    {
        var ise = IseNode();
        ise["ise.colorTheme"] = theme;
        ise["futureTheme"] = JsonNode.Parse("""{"keep":[17,"café"],"contrast":true}""");
        var terminal = new JsonObject
        {
            ["guid"] = UnknownId, ["name"] = "Ordinary PowerShell", ["commandline"] = "pwsh.exe",
            ["futureTerminal"] = 29
        };
        var settings = SettingsLoader.Load(Defaults, Document(ise, terminal));
        var profile = settings.Profiles.Single(item => item.Guid == Id);
        Assert.True(IsebergThemes.IsSupported(theme));
        Assert.Equal(theme, profile.IseColorTheme);
        Assert.Equal(ProfileKind.PowerShellIse, profile.Kind);
        profile.Name = "Edited original theme";
        profile.IseLoadProfiles = true;
        var saved = SettingsLoader.SerializeUserDocument(settings);
        var profiles = JsonNode.Parse(saved)!["profiles"]!["list"]!.AsArray();
        var savedIse = profiles.Single(item => item!["guid"]!.GetValue<string>() == Id)!;
        Assert.Equal(theme, savedIse["ise.colorTheme"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"keep":[17,"café"],"contrast":true}"""), savedIse["futureTheme"]));
        var savedTerminal = profiles.Single(item => item!["guid"]!.GetValue<string>() == UnknownId)!.AsObject();
        Assert.Equal("pwsh.exe", savedTerminal["commandline"]!.GetValue<string>());
        Assert.Equal(29, savedTerminal["futureTerminal"]!.GetValue<int>());
        Assert.False(savedTerminal.ContainsKey("ise.colorTheme"));
        Assert.False(savedTerminal.ContainsKey("type"));
        var restored = SettingsLoader.Load(Defaults, saved).Profiles.Single(item => item.Guid == Id);
        Assert.Equal(theme, restored.IseColorTheme);
        Assert.Equal(ProfileKind.PowerShellIse, restored.Kind);
        Assert.True(restored.IseLoadProfiles);
        Assert.Equal("Edited original theme", restored.Name);
        Assert.Equal(theme, restored.WithOverrides(new NewTerminalArgs(TabTitle: "duplicate title")).IseColorTheme);
    }

    [Theory]
    [InlineData("dark console, light editor (default)", "Dark Console, Light Editor (default)")]
    [InlineData("LIGHT CONSOLE, DARK EDITOR", "Light Console, Dark Editor")]
    [InlineData("dark console, dark editor", "Dark Console, Dark Editor")]
    [InlineData("light console, light editor", "Light Console, Light Editor")]
    [InlineData("MONOCHROME GREEN", "Monochrome Green")]
    [InlineData("presentation", "Presentation")]
    public void OriginalThemeCanonicalizationPreservesUneditedSourceSpelling(string input, string expected)
    {
        var node = IseNode();
        node["ise.colorTheme"] = input;
        var settings = SettingsLoader.Load(Defaults, Document(node));
        Assert.Equal(expected, Assert.Single(settings.Profiles).IseColorTheme);
        Assert.Equal(expected, IsebergThemes.Canonicalize(input));
        Assert.True(IsebergThemes.IsSupported(input));
        Assert.Equal(input, JsonNode.Parse(SettingsLoader.SerializeUserDocument(settings))!["profiles"]!["list"]![0]!["ise.colorTheme"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DynamicIseLayerPreservesExplicitKindOptInAndThemeBesideOrdinaryProfiles(bool optIn)
    {
        var ordinary = ProfileSettings.CreatePwsh("owned-pwsh.exe -NoProfile");
        ordinary.Source = "Phase4.Owned";
        var ise = new ProfileSettings
        {
            Guid = Id, Name = "Owned ISE", Kind = ProfileKind.PowerShellIse,
            Source = "Phase4.Owned", Origin = SettingsOrigin.Generated,
            Commandline = "", IseLoadProfiles = optIn, IseColorTheme = "Presentation",
            StartingDirectory = @"C:\owned", Icon = "owned-icon", PathTranslationStyle = "wsl"
        };
        var generation = await new DynamicProfileManager([new Phase4ProfileGenerator([ordinary, ise])]).GenerateAsync();
        Assert.Equal(new[] { Guid.Parse(ordinary.Guid!), Guid.Parse(Id) }.Order(), generation.GeneratedProfileIds.Order());
        var layer = generation.ToSettingsLayer();
        var nodes = JsonNode.Parse(layer.Json)!["profiles"]!["list"]!.AsArray();
        var terminal = nodes[0]!.AsObject();
        Assert.False(terminal.ContainsKey("type"));
        Assert.False(terminal.ContainsKey("ise.loadProfiles"));
        Assert.False(terminal.ContainsKey("ise.colorTheme"));
        Assert.Equal("owned-pwsh.exe -NoProfile", terminal["commandline"]!.GetValue<string>());
        var scripting = nodes[1]!;
        Assert.Equal("powershellIse", scripting["type"]!.GetValue<string>());
        Assert.Equal(optIn, scripting["ise.loadProfiles"]!.GetValue<bool>());
        Assert.Equal("Presentation", scripting["ise.colorTheme"]!.GetValue<string>());
        Assert.Equal("wsl", scripting["pathTranslationStyle"]!.GetValue<string>());
        var loaded = await DynamicSettingsLoader.LoadAsync(Defaults, Document(new JsonObject
        {
            ["guid"] = Id, ["name"] = "User ISE", ["futureOwned"] = 37,
            ["font"] = new JsonObject { ["face"] = "Consolas", ["size"] = 17 }
        }), [], new DynamicProfileManager([new Phase4ProfileGenerator([ordinary, ise])]));
        var profile = Assert.Single(loaded.Settings.Profiles, p => p.Guid == Id);
        Assert.Equal(ProfileKind.PowerShellIse, profile.Kind);
        Assert.Equal(optIn, profile.IseLoadProfiles);
        Assert.Equal("Presentation", profile.IseColorTheme);
        Assert.Equal(SettingsOrigin.Generated, profile.Origin);
        Assert.Equal("Consolas", profile.FontFace);
        Assert.Equal(17, profile.FontSize);
        profile.Name = "Edited dynamic ISE";
        var saved = SettingsLoader.SerializeUserDocument(loaded.Settings);
        Assert.Equal(37, JsonNode.Parse(saved)!["profiles"]!["list"]!.AsArray()
            .Single(n => n!["guid"]!.GetValue<string>() == Id)!["futureOwned"]!.GetValue<int>());
        var reloaded = await DynamicSettingsLoader.LoadAsync(Defaults, saved, [],
            new DynamicProfileManager([new Phase4ProfileGenerator([ordinary, ise])]));
        var restored = Assert.Single(reloaded.Settings.Profiles, p => p.Guid == Id);
        Assert.Equal("Edited dynamic ISE", restored.Name);
        Assert.Equal(ProfileKind.PowerShellIse, restored.Kind);
        Assert.Equal(optIn, restored.IseLoadProfiles);
        Assert.Equal("Presentation", restored.IseColorTheme);
        Assert.Equal("Consolas", restored.FontFace);
        Assert.Equal("owned-pwsh.exe -NoProfile", Assert.Single(reloaded.Settings.Profiles, p => p.Guid == ordinary.Guid).Commandline);
    }

    [Theory]
    [InlineData("Windows PowerShell")]
    [InlineData("PowerShell")]
    [InlineData("Cmd")]
    public async Task DynamicOrdinaryProfilesKeepTerminalSerializationOverridesFontAndUnknownFields(string kind)
    {
        var original = kind switch
        {
            "Windows PowerShell" => ProfileSettings.CreatePowerShell(),
            "PowerShell" => ProfileSettings.CreatePwsh(),
            _ => ProfileSettings.CreateCmd()
        };
        original.Source = "Phase4.Owned";
        var commandline = original.Commandline;
        var user = new JsonObject
        {
            ["guid"] = original.Guid, ["font"] = new JsonObject { ["face"] = "Consolas", ["size"] = 19 },
            ["futureOwned"] = new JsonObject { ["keep"] = 41 }
        };
        var loaded = await DynamicSettingsLoader.LoadAsync(Defaults, Document(user), [],
            new DynamicProfileManager([new Phase4ProfileGenerator([original])]));
        var profile = Assert.Single(loaded.Settings.Profiles);
        Assert.Equal(ProfileKind.Terminal, profile.Kind);
        Assert.Equal(commandline, profile.Commandline);
        var replacement = profile.WithOverrides(new NewTerminalArgs(
            Commandline: "-owned", AppendCommandLine: true, StartingDirectory: @"C:\owned", TabTitle: "Owned title"));
        Assert.Equal(commandline + " -owned", replacement.Commandline);
        Assert.Equal(commandline, profile.Commandline);
        loaded.Settings.Profiles[0] = replacement;
        var serialized = SettingsLoader.SerializeUserDocument(loaded.Settings);
        var saved = JsonNode.Parse(serialized)!["profiles"]!["list"]![0]!.AsObject();
        Assert.False(saved.ContainsKey("type"));
        Assert.False(saved.ContainsKey("ise"));
        Assert.False(saved.ContainsKey("ise.loadProfiles"));
        Assert.False(saved.ContainsKey("ise.colorTheme"));
        Assert.Equal(41, saved["futureOwned"]!["keep"]!.GetValue<int>());
        var restored = Assert.Single((await DynamicSettingsLoader.LoadAsync(Defaults, serialized, [],
            new DynamicProfileManager([new Phase4ProfileGenerator([original])]))).Settings.Profiles);
        Assert.Equal(ProfileKind.Terminal, restored.Kind);
        Assert.Equal(original.Guid, restored.Guid);
        Assert.Equal("Consolas", restored.FontFace);
        Assert.Equal(19, restored.FontSize);
        Assert.Equal(commandline + " -owned", restored.Commandline);
        Assert.Equal(@"C:\owned", restored.StartingDirectory);
        Assert.Equal("Owned title", restored.TabTitle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DynamicIseDisabledSourceAndOrphanReconciliationDoNotConvertOrdinarySibling(bool disabled)
    {
        var ise = new ProfileSettings { Guid = Id, Name = "Generated ISE", Source = "Phase4.Owned", Kind = ProfileKind.PowerShellIse };
        var ordinary = new JsonObject { ["guid"] = UnknownId, ["name"] = "Ordinary", ["commandline"] = "cmd.exe" };
        var user = JsonNode.Parse(Document(new JsonObject
        {
            ["guid"] = Id, ["name"] = "Owned ISE", ["type"] = "powershellIse", ["source"] = "Phase4.Owned"
        }, ordinary))!.AsObject();
        if (disabled) user["disabledProfileSources"] = new JsonArray("phase4.owned");
        var loaded = await DynamicSettingsLoader.LoadAsync(Defaults, user.ToJsonString(), [],
            new DynamicProfileManager([new Phase4ProfileGenerator([ise])]), [Guid.Parse(Id)]);
        var profile = Assert.Single(loaded.Settings.Profiles, p => p.Guid == Id);
        Assert.Equal(disabled, profile.Orphaned);
        Assert.Equal(disabled, profile.Hidden);
        Assert.Equal(ProfileKind.PowerShellIse, profile.Kind);
        Assert.False(profile.IseLoadProfiles);
        Assert.Equal("Classic ISE", profile.IseColorTheme);
        Assert.Equal(disabled ? UnknownId : Id, loaded.Settings.GetDefaultProfile().Guid);
        Assert.Equal(ProfileKind.Terminal, Assert.Single(loaded.Settings.Profiles, p => p.Guid == UnknownId).Kind);
        Assert.Equal(disabled ? 1 : 0, loaded.Settings.Diagnostics.Count(d => d.Code == "OrphanedGeneratedProfile"));
        var saved = JsonNode.Parse(SettingsLoader.SerializeUserDocument(loaded.Settings))!["profiles"]!["list"]!.AsArray();
        Assert.False(saved.Single(n => n!["guid"]!.GetValue<string>() == Id)!.AsObject().ContainsKey("hidden"));
        var state = new ApplicationStateData();
        loaded.Generation.UpdateState(state);
        Assert.Equal(disabled ? Array.Empty<Guid>() : new[] { Guid.Parse(Id) }, state.GeneratedProfiles.Order());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("nested-null")]
    [InlineData("nested-over-flat")]
    [InlineData("defaults")]
    public void ConcreteMissingAndDefaultIseProfilesRetainClassicCompatibilityWithoutConvertingTerminals(string fixture)
    {
        var input = IseNode();
        if (fixture == "nested-null") input["ise"] = new JsonObject { ["colorTheme"] = null };
        if (fixture == "nested-over-flat")
        {
            input["ise.colorTheme"] = "Dark";
            input["ise"] = new JsonObject { ["colorTheme"] = "Classic ISE" };
        }
        var terminal = new JsonObject { ["guid"] = UnknownId, ["name"] = "Cmd", ["commandline"] = "cmd.exe" };
        var document = JsonNode.Parse(Document(input, terminal))!.AsObject();
        if (fixture == "defaults") document["profiles"]!["defaults"] = new JsonObject
        {
            ["font"] = new JsonObject { ["face"] = "Consolas", ["size"] = 16 },
            ["ise"] = new JsonObject { ["showLineNumbers"] = false }
        };
        var settings = SettingsLoader.Load(Defaults, document.ToJsonString());
        var ise = Assert.Single(settings.Profiles, p => p.Guid == Id);
        Assert.Equal("Classic ISE", ise.IseColorTheme);
        Assert.Equal(ProfileKind.PowerShellIse, ise.Kind);
        Assert.False(ise.IseLoadProfiles);
        Assert.Equal(fixture != "defaults", ise.IseShowLineNumbers);
        if (fixture == "defaults")
        {
            Assert.Equal("Consolas", ise.FontFace);
            Assert.Equal(16, ise.FontSize);
        }
        var cmd = Assert.Single(settings.Profiles, p => p.Guid == UnknownId);
        Assert.Equal(ProfileKind.Terminal, cmd.Kind);
        Assert.Equal("cmd.exe", cmd.Commandline);
        Assert.Equal("Classic ISE", new ProfileSettings().IseColorTheme);
        Assert.Equal("Classic ISE", ProfileSettings.CreatePowerShellIse().IseColorTheme);
        Assert.Equal(ProfileKind.Terminal, SettingsService.CreateDefault().ProfileDefaults.Kind);
    }

    [Theory]
    [InlineData(-1, 0, 0, 1, -1, 0)]
    [InlineData(0, 0, 1, 1, 0, 0)]
    [InlineData(120, 120, 30, 30, 100, 100)]
    [InlineData(121, 120, 31, 30, 101, 100)]
    public void NestedIseNumericSettingsClampAdjacentBoundsWithoutConvertingOrdinaryProfile(
        int autoSave, int expectedAuto, int timeout, int expectedTimeout, int recent, int expectedRecent)
    {
        var input = IseNode();
        input["ise"] = new JsonObject
        {
            ["autoSaveMinutes"] = autoSave, ["intelliSenseTimeoutSeconds"] = timeout, ["recentFileCount"] = recent,
            ["loadProfiles"] = false, ["wordWrap"] = true, ["consoleIntelliSense"] = false,
            ["scriptCompletionOnEnter"] = false, ["useDefaultSnippets"] = false, ["futureNested"] = 43
        };
        var settings = SettingsLoader.Load(Defaults, Document(input));
        var profile = Assert.Single(settings.Profiles);
        Assert.Equal(expectedAuto, profile.IseAutoSaveMinutes);
        Assert.Equal(expectedTimeout, profile.IseIntelliSenseTimeoutSeconds);
        Assert.Equal(expectedRecent, profile.IseRecentFileCount);
        Assert.True(profile.IseWordWrap);
        Assert.False(profile.IseConsoleIntelliSense);
        Assert.False(profile.IseScriptCompletionOnEnter);
        Assert.False(profile.IseUseDefaultSnippets);
        profile.Name = "Edited nested";
        var serialized = SettingsLoader.SerializeUserDocument(settings);
        Assert.Equal(43, JsonNode.Parse(serialized)!["profiles"]!["list"]![0]!["ise"]!["futureNested"]!.GetValue<int>());
        var restored = Assert.Single(SettingsLoader.Load(Defaults, serialized).Profiles);
        Assert.Equal(expectedAuto, restored.IseAutoSaveMinutes);
        Assert.Equal(expectedTimeout, restored.IseIntelliSenseTimeoutSeconds);
        Assert.Equal(expectedRecent, restored.IseRecentFileCount);
        Assert.Equal(ProfileKind.PowerShellIse, restored.Kind);
        Assert.Equal(Id, restored.Guid);
    }

    private sealed class Phase4ProfileGenerator(IReadOnlyList<ProfileSettings> profiles) : IDynamicProfileGenerator
    {
        public string Source => "Phase4.Owned";
        public string DisplayName => "Owned generator";
        public string Icon => "owned-icon";
        public ValueTask<DynamicProfileGeneratorResult> GenerateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new DynamicProfileGeneratorResult(profiles, []));
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("malformed")]
    public void NestedIseMalformedAndOmittedNumericValuesUseLiteralDefaults(string fixture)
    {
        var input = IseNode();
        var ise = new JsonObject();
        input["ise"] = ise;
        if (fixture != "missing")
            foreach (var field in new[] { "autoSaveMinutes", "intelliSenseTimeoutSeconds", "recentFileCount" })
                ise[field] = fixture == "null" ? null : JsonValue.Create("not-integer");
        var profile = Assert.Single(SettingsLoader.Load(Defaults, Document(input)).Profiles);
        Assert.Equal(2, profile.IseAutoSaveMinutes);
        Assert.Equal(3, profile.IseIntelliSenseTimeoutSeconds);
        Assert.Equal(10, profile.IseRecentFileCount);
        Assert.Equal(ProfileKind.PowerShellIse, profile.Kind);
        Assert.False(profile.IseLoadProfiles);
    }

    [Theory]
    [InlineData("true", false, true)]
    [InlineData("false", true, false)]
    [InlineData("\"invalid\"", true, true)]
    public void NestedIseProfileOptInTakesPrecedenceOverLegacyFlatValueAndMalformedFallsBack(
        string nested, bool flat, bool expected)
    {
        var input = IseNode();
        input["ise.loadProfiles"] = flat;
        input["ise"] = new JsonObject { ["loadProfiles"] = JsonNode.Parse(nested) };
        var settings = SettingsLoader.Load(Defaults, Document(input));
        var profile = Assert.Single(settings.Profiles);
        Assert.Equal(expected, profile.IseLoadProfiles);
        Assert.Equal(ProfileKind.PowerShellIse, profile.Kind);
        profile.Name = "Edited opt-in";
        var restored = Assert.Single(SettingsLoader.Load(Defaults, SettingsLoader.SerializeUserDocument(settings)).Profiles);
        Assert.Equal(expected, restored.IseLoadProfiles);
        Assert.Equal("Classic ISE", restored.IseColorTheme);
        Assert.Equal(Id, restored.Guid);
    }
}
