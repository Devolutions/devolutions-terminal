namespace Devolutions.Terminal.Settings;

public static class IsebergThemes
{
    public const string Classic = "Classic ISE";
    public const string Dark = "Dark";
    public const string Light = "Light";
    public const string FollowDt = "Follow DT";
    public const string DarkConsoleLightEditor = "Dark Console, Light Editor (default)";
    public const string LightConsoleDarkEditor = "Light Console, Dark Editor";
    public const string DarkConsoleDarkEditor = "Dark Console, Dark Editor";
    public const string LightConsoleLightEditor = "Light Console, Light Editor";
    public const string MonochromeGreen = "Monochrome Green";
    public const string Presentation = "Presentation";

    public static IReadOnlyList<string> Choices { get; } =
        [DarkConsoleLightEditor, LightConsoleDarkEditor, DarkConsoleDarkEditor, LightConsoleLightEditor,
         MonochromeGreen, Presentation, Classic, Dark, Light, FollowDt];

    public static string Canonicalize(string name) =>
        Choices.FirstOrDefault(choice => string.Equals(choice, name, StringComparison.OrdinalIgnoreCase)) ?? name;

    public static bool IsSupported(string name) => Choices.Contains(Canonicalize(name));
}
