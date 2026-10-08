namespace Iseberg.Core;

public static class EditorThemePresets
{
    public const string DarkConsoleLightEditor = "Dark Console, Light Editor (default)";
    public const string LightConsoleDarkEditor = "Light Console, Dark Editor";
    public const string DarkConsoleDarkEditor = "Dark Console, Dark Editor";
    public const string LightConsoleLightEditor = "Light Console, Light Editor";
    public const string MonochromeGreen = "Monochrome Green";
    public const string Presentation = "Presentation";
    public static IReadOnlyList<string> OriginalNames { get; } =
        [DarkConsoleLightEditor, LightConsoleDarkEditor, DarkConsoleDarkEditor, LightConsoleLightEditor, MonochromeGreen, Presentation];

    public static IReadOnlyList<EditorTheme> OriginalBuiltIns() => OriginalNames.Select(Original).ToArray();

    public static double FontSizePoints(string name) => name switch
    {
        MonochromeGreen => 11,
        Presentation => 20,
        _ when OriginalNames.Contains(name) => 9,
        _ => throw new ArgumentException("Unknown original ISE theme.", nameof(name))
    };

    public static EditorTheme Original(string name)
    {
        if (!OriginalNames.Contains(name)) throw new ArgumentException("Unknown original ISE theme.", nameof(name));
        var darkScript = name is LightConsoleDarkEditor or DarkConsoleDarkEditor or MonochromeGreen;
        var darkConsole = name is DarkConsoleLightEditor or DarkConsoleDarkEditor or MonochromeGreen or Presentation;
        var theme = new EditorTheme { Name = name };
        Set(theme, "Script.Background", darkScript ? "#012456" : "#FFFFFF");
        Set(theme, "Script.Foreground", darkScript ? "#F5F5F5" : "#000000");
        Set(theme, "Console.Background", darkConsole ? "#012456" : "#FFFFFF");
        Set(theme, "Console.TextBackground", "#012456");
        Set(theme, "Console.Foreground", darkConsole ? "#F5F5F5" : "#626262");
        SetTokenPalette(theme, "Script", darkScript, defaultLight: name == DarkConsoleLightEditor);
        SetTokenPalette(theme, "Console", darkConsole, defaultLight: false);
        Set(theme, "Xml.Tag", "#8B0000");
        Set(theme, "Xml.Attribute", "#FF0000");
        Set(theme, "Xml.Value", "#00008B");
        Set(theme, "Stream.Error", darkConsole ? "#FF9494" : "#E50000");
        Set(theme, "Stream.Warning", darkConsole ? "#FF8C00" : "#B26200");
        Set(theme, "Stream.Verbose", darkConsole ? "#00FFFF" : "#007F7F");
        Set(theme, "Stream.Debug", darkConsole ? "#00FFFF" : "#007F7F");
        if (name is MonochromeGreen or Presentation)
        {
            Set(theme, "Console.Background", "#000000");
            Set(theme, "Console.TextBackground", "#000000");
            Set(theme, "Stream.Error", "#FF0000");
            Set(theme, "Stream.Warning", "#FF8C00");
            Set(theme, "Stream.Verbose", "#0000FF");
            Set(theme, "Stream.Debug", "#0000FF");
        }
        if (name == MonochromeGreen)
        {
            Set(theme, "Script.Background", "#000000");
            Set(theme, "Script.Foreground", "#00FF00");
            Set(theme, "Console.Foreground", "#00FF00");
            var script = new Dictionary<string, string>
            {
                ["Attribute"]="#009F00", ["Command"]="#00BF00", ["CommandArgument"]="#00DF00",
                ["Parameter"]="#00FF00", ["Comment"]="#007F00", ["Keyword"]="#00DF00",
                ["Label"]="#00FF00", ["Member"]="#00DF00", ["Number"]="#009F00",
                ["Operator"]="#007F00", ["String"]="#00DF00", ["Type"]="#00FF00", ["Variable"]="#00BF00"
            };
            var console = new Dictionary<string, string>
            {
                ["Attribute"]="#00FF00", ["Command"]="#00DF00", ["CommandArgument"]="#00BF00",
                ["Parameter"]="#009F00", ["Comment"]="#007F00", ["Keyword"]="#00DF00",
                ["Label"]="#00DF00", ["Member"]="#00BF00", ["Number"]="#009F00",
                ["Operator"]="#009F00", ["String"]="#00FF00", ["Type"]="#009F00", ["Variable"]="#00BF00"
            };
            foreach (var (token, color) in script) Set(theme, "Script." + token, color);
            foreach (var (token, color) in console) Set(theme, "Console." + token, color);
            Set(theme, "Script.Function", script["Command"]);
            Set(theme, "Console.Function", console["Command"]);
        }
        return theme;
    }

    private static void SetTokenPalette(EditorTheme theme, string prefix, bool dark, bool defaultLight)
    {
        var palette = dark ? new Dictionary<string, string>
        {
            ["Attribute"]="#B0C4DE", ["Command"]="#E0FFFF", ["CommandArgument"]="#EE82EE",
            ["Parameter"]="#FFE4B5", ["Comment"]="#98FB98", ["Keyword"]="#E0FFFF",
            ["Label"]="#E0FFFF", ["Member"]="#F5F5F5", ["Number"]="#FFE4C4",
            ["Operator"]="#D3D3D3", ["String"]="#DB7093", ["Type"]="#8FBC8F", ["Variable"]="#FF4500"
        } : new Dictionary<string, string>
        {
            ["Attribute"]="#00BFFF", ["Command"]="#0000FF", ["CommandArgument"]="#8A2BE2",
            ["Parameter"]="#000080", ["Comment"]="#006400", ["Keyword"]="#00008B",
            ["Label"]="#00008B", ["Member"]="#000000", ["Number"]="#800080",
            ["Operator"]=defaultLight ? "#696969" : "#A9A9A9", ["String"]="#8B0000",
            ["Type"]=defaultLight ? "#006161" : "#008080", ["Variable"]=defaultLight ? "#A82D00" : "#FF4500"
        };
        foreach (var (token, color) in palette) Set(theme, prefix + "." + token, color);
        Set(theme, prefix + ".Function", palette["Command"]);
    }

    public static EditorTheme Classic() => new() { Name = "Classic ISE" };

    public static EditorTheme Light()
    {
        var theme = Classic();
        theme.Name = "Light";
        Set(theme, "Console.Foreground", "#202020");
        Set(theme, "Console.Background", "#FFFFFF");
        Set(theme, "Console.TextBackground", "#FFFFFF");
        Set(theme, "Script.Variable", "#9A3412");
        Set(theme, "Script.Number", "#9333EA");
        Set(theme, "Script.Function", "#9333EA");
        Set(theme, "Script.CommandArgument", "#9333EA");
        Set(theme, "Stream.Warning", "#8A5A00");
        Set(theme, "Stream.Verbose", "#715C00");
        Set(theme, "Stream.Debug", "#715C00");
        return theme;
    }

    public static EditorTheme Dark()
    {
        var theme = Classic();
        theme.Name = "Dark";
        Set(theme, "Script.Foreground", "#D4D4D4");
        Set(theme, "Script.Background", "#1E1E1E");
        Set(theme, "Console.Foreground", "#D4D4D4");
        Set(theme, "Console.Background", "#1E1E1E");
        Set(theme, "Console.TextBackground", "#1E1E1E");
        foreach (var prefix in new[] { "Script", "Console" })
        {
            foreach (var (token, color) in new Dictionary<string, string>
            {
                ["Comment"] = "#8AB66B", ["Keyword"] = "#569CD6", ["String"] = "#CE9178",
                ["Variable"] = "#9CDCFE", ["Number"] = "#B5CEA8", ["Command"] = "#DCDCAA",
                ["Function"] = "#DCDCAA", ["Attribute"] = "#4EC9B0", ["CommandArgument"] = "#D4D4D4",
                ["Label"] = "#C586C0", ["Parameter"] = "#9CDCFE", ["Type"] = "#4EC9B0",
                ["Operator"] = "#D4D4D4", ["Member"] = "#D4D4D4",
            }) Set(theme, prefix + "." + token, color);
        }
        Set(theme, "Xml.Comment", "#8AB66B");
        Set(theme, "Xml.Tag", "#569CD6");
        Set(theme, "Xml.Attribute", "#9CDCFE");
        Set(theme, "Xml.Value", "#CE9178");
        Set(theme, "Stream.Error", "#F48771");
        Set(theme, "Stream.Warning", "#DCDCAA");
        Set(theme, "Stream.Verbose", "#DCDCAA");
        Set(theme, "Stream.Debug", "#DCDCAA");
        return theme;
    }

    private static void Set(EditorTheme theme, string key, string color) => theme.Colors[key] = color;
}
