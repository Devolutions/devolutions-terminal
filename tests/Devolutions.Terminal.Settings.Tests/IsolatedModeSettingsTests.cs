using Devolutions.Terminal.Settings;
using Xunit;

namespace Devolutions.Terminal.Settings.Tests;

public sealed class IsolatedModeSettingsTests
{
    [Theory]
    [InlineData("""{ "compatibility.isolatedMode": true }""", true)]
    [InlineData("""{ "compatibility.isolatedMode": false }""", false)]
    [InlineData("""{ "compatibility.isolatedMode": "true" }""", false)]
    [InlineData("""{ "compatibility.isolatedMode": null }""", false)]
    [InlineData("""{ "compatibility.allowHeadless": true }""", false)]
    [InlineData("""{ "profiles": { "defaults": { "compatibility.isolatedMode": true } } }""", false)]
    [InlineData("[]", false)]
    [InlineData("{ not json", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ReadsOnlyTheRootBoolean(string? userJson, bool expected)
    {
        Assert.Equal(expected, SettingsLoader.ReadIsolatedMode(userJson));
    }

    [Fact]
    public void ReadsJsoncLikeTheFullLoader()
    {
        const string user = """
            {
                // RDM writes this before every launch.
                "compatibility.isolatedMode": true,
            }
            """;

        Assert.True(SettingsLoader.ReadIsolatedMode(user));
        Assert.True(SettingsLoader.Load(SettingsLoader.ReadEmbeddedDefaults(), user).IsolatedMode);
    }

    [Fact]
    public void IsOffByDefaultAndSerializedOnlyWhenChanged()
    {
        var settings = SettingsLoader.Load(SettingsLoader.ReadEmbeddedDefaults());
        Assert.False(settings.IsolatedMode);
        Assert.DoesNotContain(
            "compatibility.isolatedMode",
            SettingsLoader.SerializeUserDocument(settings),
            StringComparison.Ordinal);

        settings.IsolatedMode = true;
        var output = SettingsLoader.SerializeUserDocument(settings);

        Assert.True(SettingsLoader.ReadIsolatedMode(output));
        Assert.True(SettingsLoader.Load(SettingsLoader.ReadEmbeddedDefaults(), output).IsolatedMode);
    }

    [Fact]
    public void ReadsTheSettingsFileAtThePath()
    {
        var root = Path.Combine(Path.GetTempPath(), "dterm-isolated-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "settings.json");
        try
        {
            Assert.False(SettingsService.IsIsolatedModeEnabled(path));

            File.WriteAllText(path, """{ "compatibility.isolatedMode": true }""");
            Assert.True(SettingsService.IsIsolatedModeEnabled(path));

            File.WriteAllText(path, """{ "compatibility.isolatedMode": false }""");
            Assert.False(SettingsService.IsIsolatedModeEnabled(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
