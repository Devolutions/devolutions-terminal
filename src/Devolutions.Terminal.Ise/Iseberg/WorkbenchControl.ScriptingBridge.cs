using System.Collections;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using Iseberg.Core;

namespace Iseberg;

public sealed partial class WorkbenchControl
{
    private readonly Dictionary<string, object> scriptingObjects = [];
    private readonly Dictionary<object, string> scriptingIds = new(ReferenceEqualityComparer.Instance);
    private readonly List<Action> scriptingSubscriptions = [];

    private string ScriptingId(object value)
    {
        if (scriptingIds.TryGetValue(value, out var id)) return id;
        id = Guid.NewGuid().ToString("N");
        scriptingIds.Add(value, id);
        scriptingObjects.Add(id, value);
        if (value is INotifyPropertyChanged properties)
        {
            PropertyChangedEventHandler handler = (_, args) => NotifyScriptingObservers(id, args.PropertyName ?? "");
            properties.PropertyChanged += handler;
            scriptingSubscriptions.Add(() => properties.PropertyChanged -= handler);
        }
        if (value is INotifyCollectionChanged collection)
        {
            NotifyCollectionChangedEventHandler handler = (_, _) => NotifyScriptingObservers(id, "Item[]");
            collection.CollectionChanged += handler;
            scriptingSubscriptions.Add(() => collection.CollectionChanged -= handler);
        }
        return id;
    }

    private void NotifyScriptingObservers(string id, string property)
    {
        // Notifications must never acquire a runspace execution gate while a script is waiting
        // for its UI callback. The client reader and the child notification reader stay live.
        foreach (var session in Workbench.Sessions.ToArray())
            _ = ObserveScriptingNotificationAsync(session, id, property);
    }

    private async Task ObserveScriptingNotificationAsync(SessionModel session, string id, string property)
    {
        try { await session.Engine.NotifyScriptingAsync(id, property); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException or ObjectDisposedException)
        {
            System.Diagnostics.Trace.TraceWarning("ISE scripting notification failed: {0}", exception.Message);
        }
    }

    private void DetachScriptingBridge()
    {
        foreach (var unsubscribe in scriptingSubscriptions) unsubscribe();
        scriptingSubscriptions.Clear();
        scriptingObjects.Clear();
        scriptingIds.Clear();
    }

    private Task<JsonElement> DispatchScriptingAsync(SessionModel caller, string operation, JsonElement arguments, CancellationToken cancellation) =>
        Task.Run(async () =>
        {
            cancellation.ThrowIfCancellationRequested();
            if (operation != "ise") throw new NotSupportedException("Unknown ISE callback operation.");
            var id = arguments.GetProperty("id").GetString();
            var member = arguments.GetProperty("member").GetString() ?? throw new ArgumentException("An ISE member is required.");
            var action = arguments.GetProperty("action").GetString();
            var args = arguments.GetProperty("arguments");
            var target = IseInvoke(() => id is null ? Scripting :
                scriptingObjects.TryGetValue(id, out var value) ? value : throw new ObjectDisposedException("ISE scripting object"));
            if (target is IseEditor editorTarget && member is "CanGoToMatch" or "GoToMatch")
                await EnsureScriptingAnalysisAsync(editorTarget.File, cancellation);
            string Text(int index) => args[index].GetString() ?? throw new ArgumentNullException("arguments");
            string? OptionalText(int index) => args.GetArrayLength() > index ? args[index].GetString() : null;
            int Number(int index) => args[index].GetInt32();
            bool Boolean(int index) => args[index].GetBoolean();
            Encoding ReadEncoding(int index) => new ScriptEncoding(args[index].GetProperty("codePage").GetInt32(),
                args[index].GetProperty("emitBom").GetBoolean()).CreateEncoding();
            T Reference<T>(int index) where T : class => IseInvoke(() =>
                scriptingObjects.TryGetValue(Text(index), out var value) && value is T typed ? typed :
                    throw new ArgumentException("The ISE object reference is invalid."));
            object? result = null;
            if (action == "get")
                result = target switch
                {
                    IseObjectModel root => member switch
                    {
                        "Identity" => root, "CurrentPowerShellTab" => root.CurrentPowerShellTab, "CurrentFile" => root.CurrentFile,
                        "CurrentEditor" => root.CurrentEditor, "PowerShellTabs" => root.PowerShellTabs, "Options" => root.Options,
                        "VisibleHorizontalAddOnTools" => root.VisibleHorizontalAddOnTools,
                        "VisibleVerticalAddOnTools" => root.VisibleVerticalAddOnTools, _ => Unknown()
                    },
                    IseOptions options => ReadScriptingOption(options, member),
                    IsePowerShellTab tab => member switch
                    {
                        "DisplayName" => tab.DisplayName, "Prompt" => tab.Prompt, "CanInvoke" => tab.CanInvoke,
                        "StatusText" => tab.StatusText, "ExpandedScript" => tab.ExpandedScript, "ShowCommands" => tab.ShowCommands,
                        "Zoom" => tab.Zoom, "SelectedFile" => tab.SelectedFile, "Files" => tab.Files, "AddOnsMenu" => tab.AddOnsMenu,
                        "Snippets" => tab.Snippets, "ConsolePane" => tab.ConsolePane,
                        "HorizontalAddOnToolsPaneOpened" => tab.HorizontalAddOnToolsPaneOpened,
                        "VerticalAddOnToolsPaneOpened" => tab.VerticalAddOnToolsPaneOpened,
                        "HorizontalAddOnTools" => tab.HorizontalAddOnTools, "VerticalAddOnTools" => tab.VerticalAddOnTools, _ => Unknown()
                    },
                    IseFile file => member switch
                    {
                        "DisplayName" => file.DisplayName, "FullPath" => file.FullPath, "IsUntitled" => file.IsUntitled,
                        "IsSaved" => file.IsSaved, "Encoding" => new ScriptEncoding(file.Encoding.CodePage, file.Encoding.GetPreamble().Length > 0),
                        "Editor" => file.Editor, _ => Unknown()
                    },
                    IseEditor editor => member switch
                    {
                        "Text" => editor.Text, "LineCount" => editor.LineCount, "CanGoToMatch" => editor.CanGoToMatch,
                        "CaretLine" => editor.CaretLine, "CaretColumn" => editor.CaretColumn,
                        "CaretLineText" => editor.CaretLineText, "SelectedText" => editor.SelectedText, _ => Unknown()
                    },
                    IseMenuItem menu => member switch { "DisplayName" => menu.DisplayName, "Submenus" => menu.Submenus, _ => Unknown() },
                    IseFileCollection files when member == "SelectedFile" => files.SelectedFile,
                    IseSnippet snippet => member switch
                    {
                        "Title" => snippet.Title, "DisplayTitle" => snippet.DisplayTitle, "Description" => snippet.Description,
                        "Author" => snippet.Author, "CodeFragment" => snippet.CodeFragment, "Code" => snippet.Code, "Text" => snippet.Text,
                        "CaretOffset" => snippet.CaretOffset, "Indent" => snippet.Indent, "IsBuiltIn" => snippet.IsBuiltIn,
                        "IsDefault" => snippet.IsDefault, "Compatibility" => snippet.Compatibility, "FullPath" => snippet.FullPath,
                        "SchemaVersion" => snippet.SchemaVersion, "IsTabSpecific" => snippet.IsTabSpecific, _ => Unknown()
                    },
                    IEnumerable collection when member == "Items" => collection.Cast<object>().ToArray(),
                    _ => Unknown()
                };
            else if (action == "set")
            {
                switch (target)
                {
                    case IseOptions options: WriteScriptingOption(options, member, args[0]); break;
                    case IsePowerShellTab tab:
                        switch (member)
                        {
                            case "DisplayName": tab.DisplayName = Text(0); break;
                            case "ExpandedScript": tab.ExpandedScript = Boolean(0); break;
                            case "ShowCommands": tab.ShowCommands = Boolean(0); break;
                            case "Zoom": tab.Zoom = args[0].GetDouble(); break;
                            default: Unknown(); break;
                        }
                        break;
                    case IseEditor editor when member == "Text": editor.Text = Text(0); break;
                    default: Unknown(); break;
                }
            }
            else if (action == "call")
            {
                switch (target)
                {
                    case IseOptions options:
                        switch (member)
                        {
                            case "RestoreDefaults": options.RestoreDefaults(); break;
                            case "RestoreDefaultTokenColors": options.RestoreDefaultTokenColors(); break;
                            case "RestoreDefaultConsoleTokenColors": options.RestoreDefaultConsoleTokenColors(); break;
                            case "RestoreDefaultXmlTokenColors": options.RestoreDefaultXmlTokenColors(); break;
                            default: Unknown(); break;
                        }
                        break;
                    case IsePowerShellTabCollection tabs:
                        switch (member)
                        {
                            case "SetSelectedPowerShellTab": tabs.SetSelectedPowerShellTab(Reference<IsePowerShellTab>(0)); break;
                            case "Add": result = tabs.Add(); break;
                            case "Remove": tabs.Remove(Reference<IsePowerShellTab>(0)); break;
                            default: Unknown(); break;
                        }
                        break;
                    case IsePowerShellTab tab:
                        if (member == "Invoke") tab.Invoke(Text(0));
                        else if (member == "InvokeSynchronous") result = tab.InvokeSynchronous(Text(0));
                        else Unknown();
                        break;
                    case IseFileCollection files:
                        switch (member)
                        {
                            case "Add": result = args.GetArrayLength() == 0 ? files.Add() : files.Add(Text(0)); break;
                            case "SetSelectedFile": files.SetSelectedFile(Reference<IseFile>(0)); break;
                            case "Remove": files.Remove(Reference<IseFile>(0), args.GetArrayLength() > 1 && Boolean(1)); break;
                            default: Unknown(); break;
                        }
                        break;
                    case IseFile file:
                        switch (member)
                        {
                            case "Save":
                                if (args.GetArrayLength() == 0) file.Save();
                                else file.Save(ReadEncoding(0));
                                break;
                            case "SaveAs":
                                if (args.GetArrayLength() == 1) file.SaveAs(Text(0));
                                else file.SaveAs(Text(0), ReadEncoding(1));
                                break;
                            default: Unknown(); break;
                        }
                        break;
                    case IseEditor editor:
                        switch (member)
                        {
                            case "GetLineLength": result = editor.GetLineLength(Number(0)); break;
                            case "GoToMatch": editor.GoToMatch(); break;
                            case "ToggleOutliningExpansion": editor.ToggleOutliningExpansion(); break;
                            case "SetCaretPosition": editor.SetCaretPosition(Number(0), Number(1)); break;
                            case "Select": editor.Select(Number(0), Number(1), Number(2), Number(3)); break;
                            case "SelectCaretLine": editor.SelectCaretLine(); break;
                            case "InsertText": editor.InsertText(Text(0)); break;
                            case "Clear": editor.Clear(); break;
                            case "Focus": editor.Focus(); break;
                            case "EnsureVisible": editor.EnsureVisible(Number(0)); break;
                            default: Unknown(); break;
                        }
                        break;
                    case IseMenuItemCollection menus:
                        switch (member)
                        {
                            case "Add":
                                IseInvoke(() =>
                                {
                                    if (menus.Parent.Tab.Model != caller || caller.Engine.IsRemote)
                                        throw new NotSupportedException("Register menu actions from their owning local PowerShell tab.");
                                    return true;
                                });
                                result = menus.Add(Text(0), OptionalText(1), OptionalText(2));
                                break;
                            case "Remove": result = menus.Remove(Reference<IseMenuItem>(0)); break;
                            case "Clear": menus.Clear(); break;
                            default: Unknown(); break;
                        }
                        break;
                    case IseSnippetCollection snippets when member == "Load": snippets.Load(Text(0)); break;
                    case IseSnippet snippet when member == "Expand":
                        var expanded = snippet.Expand(Text(0), Text(1));
                        result = new object[] { expanded.Text, expanded.Caret };
                        break;
                    case IseUnsupportedTools: throw WpfNotSupported();
                    default: Unknown(); break;
                }
            }
            else Unknown();
            return IseInvoke(() => SerializeScriptingResult(result));
            object Unknown() => throw new NotSupportedException($"The ISE member '{member}' does not support '{action}'.");
        }, cancellation);

    private async Task EnsureScriptingAnalysisAsync(IseFile file, CancellationToken cancellation)
    {
        var snapshot = IseInvoke(() =>
        {
            file.Check();
            return (Text: file.Model.Document.Text, Path: file.FullPath, Current: file.Model.CurrentAnalysis);
        });
        if (snapshot.Current is not null) return;
        var parsed = EditorAnalysis.IsXmlDocument(snapshot.Path) ? EditorAnalysis.AnalyzeXml(snapshot.Text) :
            await file.Tab.Model.Engine.AnalyzeAsync(snapshot.Text, snapshot.Path, cancellation);
        IseInvoke(() =>
        {
            file.Check();
            if (file.Model.Document.Text != snapshot.Text)
                throw new InvalidOperationException("The document changed while checking brace matching.");
            file.Model.Analysis = parsed;
            file.Model.AnalysisText = snapshot.Text;
            return true;
        });
    }

    private JsonElement SerializeScriptingResult(object? value)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        Write(value);
        writer.Flush();
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
        void Write(object? result)
        {
            switch (result)
            {
                case null: writer.WriteNullValue(); break;
                case string text: writer.WriteStringValue(text); break;
                case bool boolean: writer.WriteBooleanValue(boolean); break;
                case int number: writer.WriteNumberValue(number); break;
                case double number: writer.WriteNumberValue(number); break;
                case ScriptEncoding encoding:
                    writer.WriteStartObject();
                    writer.WriteNumber("codePage", encoding.CodePage);
                    writer.WriteBoolean("emitBom", encoding.EmitBom);
                    writer.WriteEndObject();
                    break;
                case EditorTheme theme:
                    writer.WriteStartObject();
                    writer.WriteString("Name", theme.Name);
                    writer.WriteStartObject("Colors");
                    foreach (var color in theme.Colors) writer.WriteString(color.Key, color.Value);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    break;
                case object[] array:
                    writer.WriteStartArray();
                    foreach (var item in array) Write(item);
                    writer.WriteEndArray();
                    break;
                default:
                    var type = result switch
                    {
                        IseObjectModel => "root", IseOptions => "options", IsePowerShellTab => "tab",
                        IsePowerShellTabCollection => "tabs", IseFile => "file", IseFileCollection => "files",
                        IseEditor => "editor", IseMenuItem => "menu", IseMenuItemCollection => "menus",
                        IseSnippet => "snippet", IseSnippetCollection => "snippets", IseUnsupportedTools => "tools",
                        _ => throw new NotSupportedException("This ISE result has no portable representation.")
                    };
                    writer.WriteStartObject();
                    writer.WriteString("id", ScriptingId(result));
                    writer.WriteString("type", type);
                    writer.WriteEndObject();
                    break;
            }
        }
    }

    private static object? ReadScriptingOption(IseOptions options, string member) => member switch
    {
        "FontName" => options.FontName, "FontSize" => options.FontSize, "Zoom" => options.Zoom,
        "SelectedScriptPaneState" => options.SelectedScriptPaneState, "ShowLineNumbers" => options.ShowLineNumbers,
        "ShowOutlining" => options.ShowOutlining, "WordWrap" => options.WordWrap, "ShowToolBar" => options.ShowToolBar,
        "ShowWarningBeforeSavingOnRun" => options.ShowWarningBeforeSavingOnRun,
        "ShowWarningForDuplicateFiles" => options.ShowWarningForDuplicateFiles, "ShowDefaultSnippets" => options.ShowDefaultSnippets,
        "ShowIntellisenseInScriptPane" => options.ShowIntellisenseInScriptPane,
        "ShowIntellisenseInConsolePane" => options.ShowIntellisenseInConsolePane,
        "UseEnterToSelectInScriptPaneIntellisense" => options.UseEnterToSelectInScriptPaneIntellisense,
        "UseEnterToSelectInConsolePaneIntellisense" => options.UseEnterToSelectInConsolePaneIntellisense,
        "IntellisenseTimeoutInSeconds" => options.IntellisenseTimeoutInSeconds, "AutoSaveMinuteInterval" => options.AutoSaveMinuteInterval,
        "MruCount" => options.MruCount, "UseLocalHelp" => options.UseLocalHelp, "FixedWidthFontsOnly" => options.FixedWidthFontsOnly,
        "LoadProfiles" => options.LoadProfiles, "TokenColors" => options.TokenColors, "ConsoleTokenColors" => options.ConsoleTokenColors,
        "XmlTokenColors" => options.XmlTokenColors,
        "Theme" => options.Theme,
        _ => throw new NotSupportedException("Unknown ISE option.")
    };

    private static void WriteScriptingOption(IseOptions options, string member, JsonElement value)
    {
        switch (member)
        {
            case "FontName": options.FontName = value.GetString()!; break;
            case "FontSize": options.FontSize = value.GetDouble(); break;
            case "Zoom": options.Zoom = value.GetDouble(); break;
            case "SelectedScriptPaneState": options.SelectedScriptPaneState = value.GetString()!; break;
            case "ShowLineNumbers": options.ShowLineNumbers = value.GetBoolean(); break;
            case "ShowOutlining": options.ShowOutlining = value.GetBoolean(); break;
            case "WordWrap": options.WordWrap = value.GetBoolean(); break;
            case "ShowToolBar": options.ShowToolBar = value.GetBoolean(); break;
            case "ShowWarningBeforeSavingOnRun": options.ShowWarningBeforeSavingOnRun = value.GetBoolean(); break;
            case "ShowWarningForDuplicateFiles": options.ShowWarningForDuplicateFiles = value.GetBoolean(); break;
            case "ShowDefaultSnippets": options.ShowDefaultSnippets = value.GetBoolean(); break;
            case "ShowIntellisenseInScriptPane": options.ShowIntellisenseInScriptPane = value.GetBoolean(); break;
            case "ShowIntellisenseInConsolePane": options.ShowIntellisenseInConsolePane = value.GetBoolean(); break;
            case "UseEnterToSelectInScriptPaneIntellisense": options.UseEnterToSelectInScriptPaneIntellisense = value.GetBoolean(); break;
            case "UseEnterToSelectInConsolePaneIntellisense": options.UseEnterToSelectInConsolePaneIntellisense = value.GetBoolean(); break;
            case "IntellisenseTimeoutInSeconds": options.IntellisenseTimeoutInSeconds = value.GetInt32(); break;
            case "AutoSaveMinuteInterval": options.AutoSaveMinuteInterval = value.GetInt32(); break;
            case "MruCount": options.MruCount = value.GetInt32(); break;
            case "UseLocalHelp": options.UseLocalHelp = value.GetBoolean(); break;
            case "FixedWidthFontsOnly": options.FixedWidthFontsOnly = value.GetBoolean(); break;
            case "LoadProfiles": options.LoadProfiles = value.GetBoolean(); break;
            case "Theme" or "TokenColors" or "ConsoleTokenColors" or "XmlTokenColors":
                throw new NotSupportedException("Token colors and themes are owned by the host's theme settings.");
            default: throw new NotSupportedException("Unknown ISE option.");
        }
    }
}
