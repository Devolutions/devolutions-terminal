using Devolutions.Terminal.Settings;
using System.Text.Json.Nodes;
using Devolutions.Terminal.App.Models;
using Devolutions.Terminal.App.Panes;
using Devolutions.Terminal.App.Routing;
using Xunit;

namespace Devolutions.Terminal.App.Tests;

public sealed class LayoutDescriptorTests
{
    [Theory]
    [InlineData("persistedLayout")]
    [InlineData("persistedWindowLayout")]
    [InlineData("persistedLayoutAndContent")]
    [InlineData("PERSISTEDLAYOUT")]
    public void AllPersistedFirstWindowPreferencesRestoreLayouts(string preference)
    {
        Assert.True(TerminalLayoutStateStore.IsPersistedLayoutPreference(preference));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("defaultProfile")]
    public void NonPersistedFirstWindowPreferencesDoNotRestoreLayouts(string? preference)
    {
        Assert.False(TerminalLayoutStateStore.IsPersistedLayoutPreference(preference));
    }

    [Theory]
    [InlineData("persistedWindowLayout", false, true)]
    [InlineData("persistedWindowLayout", true, false)]
    [InlineData("defaultProfile", false, false)]
    [InlineData("defaultProfile", true, false)]
    public void IsolatedModeNeverUsesPersistedLayouts(string preference, bool isolated, bool expected)
    {
        Assert.Equal(expected, TerminalLayoutStateStore.ShouldUsePersistedLayout(preference, isolated));
    }

    [Fact]
    public void WindowTabPaneLayoutRoundTripsThroughApplicationState()
    {
        var first = Session("one");
        var second = Session("two");
        var layout = new TerminalWindowLayoutDescriptor
        {
            ActiveTabId = Guid.NewGuid(),
            Tabs =
            [
                new()
                {
                    TabId = Guid.NewGuid(),
                    ActiveSessionId = second.SessionId,
                    ZoomedSessionId = second.SessionId,
                    Title = "work",
                    Color = "#123456",
                    Root = new()
                    {
                        Orientation = PaneSplitOrientation.Vertical,
                        Ratio = 0.333333333,
                        First = new() { Session = first },
                        Second = new()
                        {
                            Session = second,
                            Presentation = new()
                            {
                                IsAdministrator = true,
                                IsReadOnly = true,
                                HasBellIndicator = true,
                                ProgressState = TerminalProgressState.Normal,
                                Progress = 0.42,
                            },
                        },
                    },
                },
            ],
        };
        layout.ActiveTabId = layout.Tabs[0].TabId;

        var state = TerminalLayoutSerializer.ToApplicationState(
            layout,
            "10,20",
            new WindowSizeState { Width = 1200, Height = 800 },
            LaunchMode.Maximized);
        var restored = TerminalLayoutSerializer.DeserializeTabs(state.TabLayout);

        Assert.NotNull(restored);
        Assert.Equal(layout.ActiveTabId, restored.ActiveTabId);
        var tab = Assert.Single(restored.Tabs);
        Assert.Equal("work", tab.Title);
        Assert.Equal(0.333333, tab.Root.Ratio);
        Assert.True(tab.Root.Second!.Presentation.IsReadOnly);
        Assert.True(tab.Root.Second.Presentation.IsAdministrator);
        Assert.Equal(0.42, tab.Root.Second.Presentation.Progress);
    }

    [Fact]
    public void InvalidOrUnknownLayoutsAreRejected()
    {
        Assert.Null(TerminalLayoutSerializer.DeserializeTabs([]));
        var invalid = ValidLayout();
        invalid.Version++;

        Assert.Throws<InvalidOperationException>(() =>
            TerminalLayoutSerializer.SerializeTabs(invalid));
    }

    [Fact]
    public void NativeActionArrayIsRejectedWithDiagnosticAndRemainsUnchanged()
    {
        var native = new JsonArray
        {
            new JsonObject
            {
                ["command"] = new JsonObject { ["action"] = "newTab" },
            },
        };
        var before = native.ToJsonString();

        Assert.False(TerminalLayoutSerializer.TryDeserializeTabs(
            native,
            out var layout,
            out var diagnostic));

        Assert.Null(layout);
        Assert.Contains("Native Windows Terminal", diagnostic);
        Assert.Equal(before, native.ToJsonString());
    }

    [Theory]
    [InlineData(LaunchMode.Maximized)]
    [InlineData(LaunchMode.Fullscreen)]
    [InlineData(LaunchMode.Focus)]
    [InlineData(LaunchMode.MaximizedFocus)]
    public void ApplicationStatePreservesGeometryAndLaunchMode(LaunchMode launchMode)
    {
        var state = TerminalLayoutSerializer.ToApplicationState(
            ValidLayout(),
            "25,50",
            new WindowSizeState { Width = 1024, Height = 768 },
            launchMode);

        Assert.Equal("25,50", state.InitialPosition);
        Assert.Equal(1024, state.InitialSize!.Width);
        Assert.Equal(768, state.InitialSize.Height);
        Assert.Equal(launchMode, state.LaunchMode);
    }

    [Fact]
    public void ExplicitNullRequiredMembersAreRejectedWithoutThrowing()
    {
        var document = Assert.IsType<JsonObject>(
            TerminalLayoutSerializer.SerializeTabs(ValidLayout())[0]);
        document["tabs"] = null;

        Assert.Null(TerminalLayoutSerializer.DeserializeTabs(
            [Assert.IsType<JsonObject>(document.DeepClone())]));
    }

    [Fact]
    public void DuplicateSessionIdentifiersAreRejected()
    {
        var session = Session("duplicate");
        var layout = ValidLayout();
        layout.Tabs[0].Root = new()
        {
            Orientation = PaneSplitOrientation.Horizontal,
            First = new() { Session = session },
            Second = new() { Session = session },
        };
        layout.Tabs[0].ActiveSessionId = session.SessionId;

        Assert.Throws<InvalidOperationException>(() =>
            TerminalLayoutSerializer.SerializeTabs(layout));
    }

    [Fact]
    public void LayoutPersistsThroughStateJson()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"TerminalLayoutTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new ApplicationStateStore(directory);
            var layout = ValidLayout();
            TerminalLayoutStateStore.SaveWindow(
                store,
                1,
                layout,
                "40,50",
                new WindowSizeState { Width = 900, Height = 600 },
                LaunchMode.Default);

            var reloaded = new ApplicationStateStore(directory);
            var restored = TerminalLayoutStateStore.ReadWindow(reloaded, 1);

            Assert.Equal(layout.ActiveTabId, restored?.ActiveTabId);
            Assert.Equal("40,50", Assert.Single(reloaded.Data.PersistedWindowLayouts).InitialPosition);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MultipleWindowLayoutsDoNotOverwriteEachOther()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"TerminalLayoutTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new ApplicationStateStore(directory);
            var first = ValidLayout();
            var second = ValidLayout();
            TerminalLayoutStateStore.SaveWindow(store, 1, first, null, null, LaunchMode.Default);
            TerminalLayoutStateStore.SaveWindow(store, 2, second, null, null, LaunchMode.Maximized);

            var reloaded = new ApplicationStateStore(directory);

            Assert.Equal(first.ActiveTabId, TerminalLayoutStateStore.ReadWindow(reloaded, 1)?.ActiveTabId);
            Assert.Equal(second.ActiveTabId, TerminalLayoutStateStore.ReadWindow(reloaded, 2)?.ActiveTabId);
            Assert.Equal(2, reloaded.Data.PersistedWindowLayouts.Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void InvalidDefaultSlotCanSurviveFallbackAndCloseWithoutReplacement()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"TerminalLayoutTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new ApplicationStateStore(directory);
            var invalid = new WindowLayoutState
            {
                TabLayout =
                [
                    new JsonObject
                    {
                        ["command"] = new JsonObject { ["action"] = "newTab" },
                    },
                ],
            };
            store.SavePersistedWindowLayout(0, invalid);

            var slot = TerminalLayoutStateStore.ReadWindowState(store, 1);
            Assert.NotNull(slot);
            Assert.False(TerminalLayoutStateStore.TryRead(slot, out _, out _));

            var fallback = ValidLayout();
            Assert.False(TerminalLayoutStateStore.TrySaveWindow(
                store,
                1,
                fallback,
                null,
                null,
                LaunchMode.Default,
                blockedByInvalidRestore: true));

            var reloaded = new ApplicationStateStore(directory);
            var preserved = Assert.Single(reloaded.Data.PersistedWindowLayouts);
            Assert.Same(preserved, TerminalLayoutStateStore.ReadWindowState(reloaded, 1));
            Assert.False(TerminalLayoutStateStore.TryRead(preserved, out _, out _));
            Assert.Equal(invalid.TabLayout.ToJsonString(), preserved.TabLayout.ToJsonString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ActivationResolverSelectsSlotsAndConsumesValidWorkspaces()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"TerminalLayoutTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new ApplicationStateStore(directory);
            var state = TerminalLayoutSerializer.ToApplicationState(ValidLayout());
            store.SavePersistedWindowLayout(2, state);
            store.SaveWorkspace("build", state);
            var fallback = new TerminalWindowActivation(
                null,
                null,
                null,
                null,
                TerminalWindowLaunchMode.Default,
                []);

            var slot = TerminalLayoutActivationResolver.ResolveSavedSlot(store, 2, fallback);
            var workspace = TerminalLayoutActivationResolver.ResolveWorkspace(
                store,
                "build",
                fallback);

            Assert.NotNull(slot.PersistedLayout);
            Assert.True(TerminalLayoutStateStore.TryRead(
                slot.PersistedLayout,
                out var slotLayout,
                out _));
            Assert.Equal(
                TerminalLayoutSerializer.DeserializeTabs(state.TabLayout)!.ActiveTabId,
                slotLayout!.ActiveTabId);
            Assert.NotNull(workspace.PersistedLayout);
            Assert.Equal("build", workspace.WorkspaceName);
            Assert.Null(new ApplicationStateStore(directory).GetWorkspace("build"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ActivationResolverDiagnosesUnsupportedWorkspaceWithoutConsumingIt()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"TerminalLayoutTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new ApplicationStateStore(directory);
            store.SaveWorkspace("native", new WindowLayoutState
            {
                TabLayout =
                [
                    new JsonObject
                    {
                        ["command"] = new JsonObject { ["action"] = "newTab" },
                    },
                ],
            });
            var fallback = new TerminalWindowActivation(
                null,
                null,
                null,
                null,
                TerminalWindowLaunchMode.Default,
                []);

            var resolved = TerminalLayoutActivationResolver.ResolveWorkspace(
                store,
                "native",
                fallback);

            Assert.Null(resolved.PersistedLayout);
            Assert.Contains("Native Windows Terminal", resolved.PersistedLayoutDiagnostic);
            Assert.NotNull(new ApplicationStateStore(directory).GetWorkspace("native"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TearOffContractCarriesOnlyTransferableState()
    {
        var layout = ValidLayout();
        var request = new TabTearOffRequest(
            Guid.NewGuid(),
            7,
            layout.Tabs[0],
            new PixelPosition(100, 200));

        Assert.Equal(7, request.SourceWindowId);
        Assert.Equal("cmd.exe", request.Tab.Root.Session!.Commandline);
        Assert.Equal(request.TransferId, new TabTransferResult(request.TransferId, true).TransferId);
    }

    private static TerminalWindowLayoutDescriptor ValidLayout()
    {
        var session = Session("one");
        var tab = new TabLayoutDescriptor
        {
            ActiveSessionId = session.SessionId,
            Root = new() { Session = session },
        };
        return new()
        {
            ActiveTabId = tab.TabId,
            Tabs = [tab],
        };
    }

    private static TerminalSessionDescriptor Session(string name) =>
        new()
        {
            ProfileName = name,
            Commandline = "cmd.exe",
            StartingDirectory = @"C:\",
        };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WholeTabIseLayoutRoundTripsKindOptionsAndIdentity(bool option)
    {
        var layout = IseLayout(option);
        TerminalLayoutSerializer.Validate(layout);
        var restored = TerminalLayoutSerializer.DeserializeTabs(TerminalLayoutSerializer.SerializeTabs(layout))!;
        Assert.Equal(layout.ActiveTabId, restored.ActiveTabId);
        var tab = Assert.Single(restored.Tabs);
        Assert.Equal(layout.Tabs[0].TabId, tab.TabId);
        Assert.Equal(layout.Tabs[0].ActiveSessionId, tab.ActiveSessionId);
        Assert.Null(tab.ZoomedSessionId);
        Assert.Equal("Iseberg work", tab.Title);
        Assert.Equal("Custom ISE title", tab.CustomTitle);
        Assert.Equal("#123456", tab.Color);
        Assert.True(tab.Root.IsLeaf);
        Assert.Null(tab.Root.First);
        Assert.Null(tab.Root.Second);
        var session = tab.Root.Session!;
        Assert.Equal(ProfileKind.PowerShellIse, session.Kind);
        Assert.Equal(option, session.IseLoadProfiles);
        Assert.Equal(layout.Tabs[0].Root.Session!.SessionId, session.SessionId);
        Assert.Equal("{12345678-1234-5678-9abc-123456789abc}", session.ProfileId);
        Assert.Equal("Iseberg", session.ProfileName);
        Assert.Empty(session.Commandline);
        Assert.Equal(@"C:\scripts", session.StartingDirectory);
        Assert.Equal("ISE session title", session.TabTitle);
        Assert.Equal("#abcdef", session.TabColor);
        Assert.Equal("\uE943", session.Icon);
        Assert.True(tab.Root.Presentation.IsReadOnly);
    }

    [Fact]
    public void LayoutCurrentVersionIsTwo()
    {
        Assert.Equal(2, TerminalWindowLayoutDescriptor.CurrentVersion);
        Assert.Equal(2, new TerminalWindowLayoutDescriptor().Version);
        Assert.Equal(2, TerminalLayoutSerializer.SerializeTabs(IseLayout(false))[0]!["version"]!.GetValue<int>());
    }

    [Fact]
    public void VersionOneWithoutKindRestoresTerminal()
    {
        var input = JsonNode.Parse("""
            [{"version":1,"activeTabId":"11111111-1111-1111-1111-111111111111",
              "tabs":[{"tabId":"11111111-1111-1111-1111-111111111111",
                "activeSessionId":"22222222-2222-2222-2222-222222222222",
                "title":"Legacy PowerShell","root":{"session":{
                  "sessionId":"22222222-2222-2222-2222-222222222222",
                  "profileId":"{33333333-3333-3333-3333-333333333333}",
                  "profileName":"PowerShell","commandline":"pwsh.exe","startingDirectory":"C:\\scripts"}}}]}]
            """)!.AsArray();
        Assert.True(TerminalLayoutSerializer.TryDeserializeTabs(input, out var layout, out var diagnostic), diagnostic);
        Assert.Null(diagnostic);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), layout!.ActiveTabId);
        var tab = Assert.Single(layout.Tabs);
        var session = tab.Root.Session!;
        Assert.Equal(ProfileKind.Terminal, session.Kind);
        Assert.False(session.IseLoadProfiles);
        Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), session.SessionId);
        Assert.Equal(session.SessionId, tab.ActiveSessionId);
        Assert.Equal("{33333333-3333-3333-3333-333333333333}", session.ProfileId);
        Assert.Equal("pwsh.exe", session.Commandline);
    }

    [Theory]
    [InlineData("Unsupported")]
    [InlineData("futureWorkbench")]
    public void UnsupportedSessionLayoutIsRejectedWithoutMutation(string kind)
    {
        var layout = IseLayout(false);
        var input = TerminalLayoutSerializer.SerializeTabs(layout);
        input[0]!["tabs"]![0]!["root"]!["session"]!["kind"] = kind;
        AssertRejectedIseJson(input);
        layout.Tabs[0].Root.Session!.Kind = ProfileKind.Unsupported;
        Assert.Throws<InvalidOperationException>(() => TerminalLayoutSerializer.SerializeTabs(layout));
    }

    [Fact]
    public void SplitContainingIseIsRejected()
    {
        var layout = IseLayout(false);
        var input = TerminalLayoutSerializer.SerializeTabs(layout);
        var ise = layout.Tabs[0].Root;
        var terminal = Session("PowerShell terminal");
        layout.Tabs[0].Root = new PaneLayoutDescriptor
        {
            Orientation = PaneSplitOrientation.Vertical,
            First = new() { Session = terminal },
            Second = ise,
        };
        var splitJson = new JsonObject
        {
            ["orientation"] = "Vertical", ["ratio"] = 0.5,
            ["first"] = new JsonObject
            {
                ["session"] = new JsonObject { ["kind"] = "Terminal", ["sessionId"] = terminal.SessionId.ToString() },
            },
            ["second"] = input[0]!["tabs"]![0]!["root"]!.DeepClone(),
        };
        input[0]!["tabs"]![0]!["root"] = splitJson;
        Assert.Contains("whole tab", Assert.Throws<InvalidOperationException>(() => TerminalLayoutSerializer.Validate(layout)).Message);
        Assert.Throws<InvalidOperationException>(() => TerminalLayoutSerializer.SerializeTabs(layout));
        AssertRejectedIseJson(input);
    }

    [Fact]
    public void ZoomedWholeTabIseIsRejected()
    {
        var layout = IseLayout(false);
        var input = TerminalLayoutSerializer.SerializeTabs(layout);
        layout.Tabs[0].ZoomedSessionId = layout.Tabs[0].ActiveSessionId;
        input[0]!["tabs"]![0]!["zoomedSessionId"] = layout.Tabs[0].ActiveSessionId.ToString();
        Assert.Contains("whole tab", Assert.Throws<InvalidOperationException>(() => TerminalLayoutSerializer.Validate(layout)).Message);
        Assert.Throws<InvalidOperationException>(() => TerminalLayoutSerializer.SerializeTabs(layout));
        AssertRejectedIseJson(input);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void IseLayoutWithoutColorThemeDefaultsToClassic(int version)
    {
        var input = TerminalLayoutSerializer.SerializeTabs(IseLayout(false));
        input[0]!["version"] = version;
        Assert.True(input[0]!["tabs"]![0]!["root"]!["session"]!.AsObject().Remove("iseColorTheme"));
        Assert.True(TerminalLayoutSerializer.TryDeserializeTabs(input, out var layout, out var diagnostic), diagnostic);
        Assert.Null(diagnostic);
        Assert.Equal(version, layout!.Version);
        var session = Assert.Single(layout.Tabs).Root.Session!;
        Assert.Equal("Classic ISE", session.IseColorTheme);
        Assert.Equal(ProfileKind.PowerShellIse, session.Kind);
        Assert.False(session.IseLoadProfiles);
        Assert.Equal("{12345678-1234-5678-9abc-123456789abc}", session.ProfileId);
    }

    [Theory]
    [InlineData("Classic ISE")]
    [InlineData("Dark")]
    [InlineData("Light")]
    [InlineData("Follow DT")]
    [InlineData("Future Theme")]
    [InlineData("")]
    public void IseLayoutPreservesColorThemeIncludingUnknownValues(string theme)
    {
        var layout = IseLayout(true);
        var original = layout.Tabs[0].Root.Session!;
        original.IseColorTheme = theme;
        var saved = TerminalLayoutSerializer.SerializeTabs(layout);
        Assert.Equal(theme, saved[0]!["tabs"]![0]!["root"]!["session"]!["iseColorTheme"]!.GetValue<string>());
        var restored = TerminalLayoutSerializer.DeserializeTabs(saved)!;
        var tab = Assert.Single(restored.Tabs);
        var session = tab.Root.Session!;
        Assert.Equal(theme, session.IseColorTheme);
        Assert.Equal(ProfileKind.PowerShellIse, session.Kind);
        Assert.True(session.IseLoadProfiles);
        Assert.Equal(original.SessionId, session.SessionId);
        Assert.Equal(original.ProfileId, session.ProfileId);
        Assert.Equal(layout.ActiveTabId, restored.ActiveTabId);
        Assert.Equal(original.SessionId, tab.ActiveSessionId);
    }

    private static void AssertRejectedIseJson(JsonArray input)
    {
        var before = input.ToJsonString();
        Assert.False(TerminalLayoutSerializer.TryDeserializeTabs(input, out var layout, out var diagnostic));
        Assert.Null(layout);
        Assert.False(string.IsNullOrWhiteSpace(diagnostic));
        Assert.Equal(before, input.ToJsonString());
    }

    private static TerminalWindowLayoutDescriptor IseLayout(bool option)
    {
        var session = new TerminalSessionDescriptor
        {
            Kind = ProfileKind.PowerShellIse, IseLoadProfiles = option,
            ProfileId = "{12345678-1234-5678-9abc-123456789abc}",
            ProfileName = "Iseberg", Commandline = "", StartingDirectory = @"C:\scripts",
            TabTitle = "ISE session title", TabColor = "#abcdef", Icon = "\uE943",
        };
        var tab = new TabLayoutDescriptor
        {
            ActiveSessionId = session.SessionId, Title = "Iseberg work",
            CustomTitle = "Custom ISE title", Color = "#123456",
            Root = new() { Session = session, Presentation = new() { IsReadOnly = true } },
        };
        return new() { ActiveTabId = tab.TabId, Tabs = [tab] };
    }

    [Theory]
    [InlineData("Dark Console, Light Editor (default)")]
    [InlineData("Light Console, Dark Editor")]
    [InlineData("Dark Console, Dark Editor")]
    [InlineData("Light Console, Light Editor")]
    [InlineData("Monochrome Green")]
    [InlineData("Presentation")]
    public void OriginalIseThemeLayoutRoundTripPreservesIdentityPresentationAndMetadata(string theme)
    {
        var layout = IseLayout(true);
        var original = layout.Tabs[0].Root.Session!;
        original.IseColorTheme = theme;
        var input = TerminalLayoutSerializer.SerializeTabs(layout);
        var before = input.ToJsonString();
        Assert.True(TerminalLayoutSerializer.TryDeserializeTabs(input, out var restored, out var diagnostic));
        Assert.Null(diagnostic);
        Assert.NotNull(restored);
        var tab = Assert.Single(restored.Tabs);
        var session = tab.Root.Session!;
        Assert.Equal(theme, session.IseColorTheme);
        Assert.Equal(ProfileKind.PowerShellIse, session.Kind);
        Assert.True(session.IseLoadProfiles);
        Assert.Equal(original.SessionId, session.SessionId);
        Assert.Equal(original.ProfileId, session.ProfileId);
        Assert.Equal(layout.ActiveTabId, restored.ActiveTabId);
        Assert.Equal(session.SessionId, tab.ActiveSessionId);
        Assert.Equal("Iseberg work", tab.Title);
        Assert.Equal("Custom ISE title", tab.CustomTitle);
        Assert.Equal("#123456", tab.Color);
        Assert.Equal("ISE session title", session.TabTitle);
        Assert.Equal(@"C:\scripts", session.StartingDirectory);
        Assert.True(tab.Root.Presentation.IsReadOnly);
        Assert.Equal(before, input.ToJsonString());
    }
}
