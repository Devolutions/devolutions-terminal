using System.Text.Json.Serialization;

namespace Iseberg.Core;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(UserSettings))]
[JsonSerializable(typeof(HelpViewSettings))]
[JsonSerializable(typeof(ThemeFile))]
[JsonSerializable(typeof(WorkbenchState))]
[JsonSerializable(typeof(RecoveredScript))]
[JsonSerializable(typeof(string))]
public sealed partial class IseJsonContext : JsonSerializerContext;
