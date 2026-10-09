using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Management.Automation;
using System.Text;
using System.Text.Json;
using Iseberg.Core;

namespace Iseberg.PowerShellHost;

/// <summary>Portable live ISE API. Only explicit UI operations cross the private bridge.</summary>
public sealed class IseObjectProxy : INotifyPropertyChanged, IIseObjectProxyNotifications
{
    private readonly IIseScriptingBridge bridge;
    private readonly ConcurrentDictionary<string, IseProxy> objects = new(StringComparer.Ordinal);
    private string? rootId;
    public IseObjectProxy(IIseScriptingBridge bridge) => this.bridge = bridge;
    public event PropertyChangedEventHandler? PropertyChanged;
    public IsePowerShellTab CurrentPowerShellTab => Object<IsePowerShellTab>(Get(null, nameof(CurrentPowerShellTab)));
    public IseFile? CurrentFile => NullableObject<IseFile>(Get(null, nameof(CurrentFile)));
    public IseEditor? CurrentEditor => NullableObject<IseEditor>(Get(null, nameof(CurrentEditor)));
    public IsePowerShellTabCollection PowerShellTabs => Object<IsePowerShellTabCollection>(Get(null, nameof(PowerShellTabs)));
    public IseOptions Options => Object<IseOptions>(Get(null, nameof(Options)));
    public object VisibleHorizontalAddOnTools => Get(null, nameof(VisibleHorizontalAddOnTools));
    public object VisibleVerticalAddOnTools => Get(null, nameof(VisibleVerticalAddOnTools));

    public void Notify(string objectId, string propertyName) => bridge.ScheduleNotification(() =>
    {
        if (objects.TryGetValue(objectId, out var proxy)) proxy.Notify(propertyName);
        if (objectId == rootId)
            PropertyChanged?.Invoke(this, new(propertyName));
    });

    internal JsonElement Get(string? id, string member)
    {
        rootId ??= Request(null, "get", "Identity", []).GetProperty("id").GetString();
        return Request(id, "get", member, []);
    }
    internal JsonElement Call(string id, string member, params object?[] arguments) => Request(id, "call", member, arguments);
    internal void Set(string id, string member, object? value) => Request(id, "set", member, [value]);
    private JsonElement Request(string? id, string action, string member, object?[] arguments)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("id", id);
            writer.WriteString("action", action);
            writer.WriteString("member", member);
            writer.WriteStartArray("arguments");
            foreach (var argument in arguments)
            {
                switch (argument)
                {
                    case null: writer.WriteNullValue(); break;
                    case string value: writer.WriteStringValue(value); break;
                    case int value: writer.WriteNumberValue(value); break;
                    case double value: writer.WriteNumberValue(value); break;
                    case bool value: writer.WriteBooleanValue(value); break;
                    case ScriptEncoding value:
                        writer.WriteStartObject();
                        writer.WriteNumber("codePage", value.CodePage);
                        writer.WriteBoolean("emitBom", value.EmitBom);
                        writer.WriteEndObject();
                        break;
                    case IseProxy proxy:
                        if (proxy.Owner != this) throw new ArgumentException("This ISE object belongs to another workbench.");
                        writer.WriteStringValue(proxy.Id);
                        break;
                    default: throw new ArgumentException("This argument has no portable ISE representation.");
                }
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return bridge.Invoke("ise", document.RootElement);
    }
    internal T Object<T>(JsonElement value) where T : IseProxy
    {
        var id = value.GetProperty("id").GetString()!;
        var type = value.GetProperty("type").GetString();
        return (T)objects.GetOrAdd(id, _ => type switch
        {
            "options" => new IseOptions(this, id),
            "tab" => new IsePowerShellTab(this, id),
            "tabs" => new IsePowerShellTabCollection(this, id),
            "file" => new IseFile(this, id),
            "files" => new IseFileCollection(this, id),
            "editor" => new IseEditor(this, id),
            "menu" => new IseMenuItem(this, id),
            "menus" => new IseMenuItemCollection(this, id),
            "snippet" => new IseSnippet(this, id),
            "snippets" => new IseSnippetCollection(this, id),
            "tools" => new IseUnsupportedTools(this, id),
            _ => throw new InvalidDataException("The workbench returned an unknown ISE object type.")
        });
    }
    internal T? NullableObject<T>(JsonElement value) where T : IseProxy =>
        value.ValueKind == JsonValueKind.Null ? null : Object<T>(value);
    internal string Register(ScriptBlock action) => bridge.RegisterCallback(action);
    internal void Unregister(string handle) => bridge.RemoveCallback(handle);
}

public abstract class IseProxy : INotifyPropertyChanged
{
    internal IseObjectProxy Owner { get; }
    internal string Id { get; }
    protected IseProxy(IseObjectProxy owner, string id) { Owner = owner; Id = id; }
    public event PropertyChangedEventHandler? PropertyChanged;
    internal virtual void Notify(string property) => PropertyChanged?.Invoke(this, new(property));
    protected JsonElement Get(string member) => Owner.Get(Id, member);
    protected string ReadText(string member) => Get(member).GetString()!;
    protected bool Boolean(string member) => Get(member).GetBoolean();
    protected int Integer(string member) => Get(member).GetInt32();
    protected double Number(string member) => Get(member).GetDouble();
    protected void Set(string member, object? value) => Owner.Set(Id, member, value);
    protected JsonElement Call(string member, params object?[] arguments) => Owner.Call(Id, member, arguments);
    protected T Object<T>(string member) where T : IseProxy => Owner.Object<T>(Get(member));
    protected T? NullableObject<T>(string member) where T : IseProxy => Owner.NullableObject<T>(Get(member));
}

public abstract class IseCollection<T>(IseObjectProxy owner, string id) : IseProxy(owner, id),
    IReadOnlyList<T>, INotifyCollectionChanged where T : IseProxy
{
    private T[] Snapshot() => Get("Items").EnumerateArray().Select(Owner.Object<T>).ToArray();
    public int Count => Snapshot().Length;
    public T this[int index] => Snapshot()[index];
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)Snapshot()).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;
    internal override void Notify(string property)
    {
        base.Notify(property);
        if (property != "Item[]") return;
        base.Notify(nameof(Count));
        CollectionChanged?.Invoke(this, new(NotifyCollectionChangedAction.Reset));
    }
}

public sealed class IsePowerShellTabCollection(IseObjectProxy owner, string id) : IseCollection<IsePowerShellTab>(owner, id)
{
    public IsePowerShellTab Add() => Owner.Object<IsePowerShellTab>(Call(nameof(Add)));
    public void Remove(IsePowerShellTab tab) => Call(nameof(Remove), tab);
    public void SetSelectedPowerShellTab(IsePowerShellTab tab) => Call(nameof(SetSelectedPowerShellTab), tab);
}

public sealed class IsePowerShellTab(IseObjectProxy owner, string id) : IseProxy(owner, id)
{
    public string DisplayName { get => ReadText(nameof(DisplayName)); set => Set(nameof(DisplayName), value); }
    public string Prompt => ReadText(nameof(Prompt));
    public bool CanInvoke => Boolean(nameof(CanInvoke));
    public string StatusText => ReadText(nameof(StatusText));
    public bool ExpandedScript { get => Boolean(nameof(ExpandedScript)); set => Set(nameof(ExpandedScript), value); }
    public bool ShowCommands { get => Boolean(nameof(ShowCommands)); set => Set(nameof(ShowCommands), value); }
    public double Zoom { get => Number(nameof(Zoom)); set => Set(nameof(Zoom), value); }
    public bool HorizontalAddOnToolsPaneOpened => Boolean(nameof(HorizontalAddOnToolsPaneOpened));
    public bool VerticalAddOnToolsPaneOpened => Boolean(nameof(VerticalAddOnToolsPaneOpened));
    public object ConsolePane => Get(nameof(ConsolePane));
    public IseFile? SelectedFile => NullableObject<IseFile>(nameof(SelectedFile));
    public IseFileCollection Files => Object<IseFileCollection>(nameof(Files));
    public IseMenuItem AddOnsMenu => Object<IseMenuItem>(nameof(AddOnsMenu));
    public IseSnippetCollection Snippets => Object<IseSnippetCollection>(nameof(Snippets));
    public IseUnsupportedTools HorizontalAddOnTools => Object<IseUnsupportedTools>(nameof(HorizontalAddOnTools));
    public IseUnsupportedTools VerticalAddOnTools => Object<IseUnsupportedTools>(nameof(VerticalAddOnTools));
    public void Invoke(string script) => Call(nameof(Invoke), script);
    public object InvokeSynchronous(string script) => Call(nameof(InvokeSynchronous), script);
    public object InvokeSynchronous(string script, bool useNewScope) => Call(nameof(InvokeSynchronous), script, useNewScope);
    public object InvokeSynchronous(string script, bool useNewScope, int millisecondsTimeout) =>
        Call(nameof(InvokeSynchronous), script, useNewScope, millisecondsTimeout);
}

public sealed class IseFileCollection(IseObjectProxy owner, string id) : IseCollection<IseFile>(owner, id)
{
    public IseFile? SelectedFile => NullableObject<IseFile>(nameof(SelectedFile));
    public IseFile Add() => Owner.Object<IseFile>(Call(nameof(Add)));
    public IseFile Add(string fullPath) => Owner.Object<IseFile>(Call(nameof(Add), fullPath));
    public void SetSelectedFile(IseFile file) => Call(nameof(SetSelectedFile), file);
    public void Remove(IseFile file) => Call(nameof(Remove), file);
    public void Remove(IseFile file, bool force) => Call(nameof(Remove), file, force);
}

public sealed class IseFile(IseObjectProxy owner, string id) : IseProxy(owner, id)
{
    public string DisplayName => ReadText(nameof(DisplayName));
    public string? FullPath => Get(nameof(FullPath)).GetString();
    public bool IsUntitled => Boolean(nameof(IsUntitled));
    public bool IsSaved => Boolean(nameof(IsSaved));
    public Encoding Encoding
    {
        get
        {
            var value = Get(nameof(Encoding));
            return new ScriptEncoding(value.GetProperty("codePage").GetInt32(), value.GetProperty("emitBom").GetBoolean()).CreateEncoding();
        }
    }
    public IseEditor Editor => Object<IseEditor>(nameof(Editor));
    public void Save() => Call(nameof(Save));
    public void Save(Encoding encoding) => Call(nameof(Save), new ScriptEncoding(encoding.CodePage, encoding.GetPreamble().Length > 0));
    public void SaveAs(string fullPath) => Call(nameof(SaveAs), fullPath);
    public void SaveAs(string fullPath, Encoding encoding) => Call(nameof(SaveAs), fullPath, new ScriptEncoding(encoding.CodePage, encoding.GetPreamble().Length > 0));
}

public sealed class IseEditor(IseObjectProxy owner, string id) : IseProxy(owner, id)
{
    public string Text { get => ReadText(nameof(Text)); set => Set(nameof(Text), value); }
    public int LineCount => Integer(nameof(LineCount));
    public bool CanGoToMatch => Boolean(nameof(CanGoToMatch));
    public int CaretLine => Integer(nameof(CaretLine));
    public int CaretColumn => Integer(nameof(CaretColumn));
    public string CaretLineText => ReadText(nameof(CaretLineText));
    public string SelectedText => ReadText(nameof(SelectedText));
    public int GetLineLength(int lineNumber) => Call(nameof(GetLineLength), lineNumber).GetInt32();
    public void GoToMatch() => Call(nameof(GoToMatch));
    public void ToggleOutliningExpansion() => Call(nameof(ToggleOutliningExpansion));
    public void SetCaretPosition(int lineNumber, int columnNumber) => Call(nameof(SetCaretPosition), lineNumber, columnNumber);
    public void Select(int startLine, int startColumn, int endLine, int endColumn) => Call(nameof(Select), startLine, startColumn, endLine, endColumn);
    public void SelectCaretLine() => Call(nameof(SelectCaretLine));
    public void InsertText(string text) => Call(nameof(InsertText), text);
    public void Clear() => Call(nameof(Clear));
    public void Focus() => Call(nameof(Focus));
    public void EnsureVisible(int lineNumber) => Call(nameof(EnsureVisible), lineNumber);
}

public sealed class IseMenuItem(IseObjectProxy owner, string id) : IseProxy(owner, id)
{
    public string DisplayName => ReadText(nameof(DisplayName));
    public IseMenuItemCollection Submenus => Object<IseMenuItemCollection>(nameof(Submenus));
    internal string? Callback { get; set; }
    internal void Release()
    {
        foreach (var child in Submenus) child.Release();
        if (Callback is not { } handle) return;
        Owner.Unregister(handle);
        Callback = null;
    }
}

public sealed class IseMenuItemCollection(IseObjectProxy owner, string id) : IseCollection<IseMenuItem>(owner, id)
{
    public IseMenuItem Add(string displayName, ScriptBlock? action, string? shortcut)
    {
        var handle = action is null ? null : Owner.Register(action);
        try
        {
            var item = Owner.Object<IseMenuItem>(Call(nameof(Add), displayName, handle, shortcut));
            item.Callback = handle;
            return item;
        }
        catch
        {
            if (handle is not null) Owner.Unregister(handle);
            throw;
        }
    }
    public void Clear()
    {
        var children = this.SelectMany(Descendants).ToArray();
        Call(nameof(Clear));
        foreach (var item in children)
            if (item.Callback is { } handle) { Owner.Unregister(handle); item.Callback = null; }
    }
    public bool Remove(IseMenuItem item)
    {
        var callbacks = Descendants(item).ToArray();
        var removed = Call(nameof(Remove), item).GetBoolean();
        if (removed)
            foreach (var descendant in callbacks)
                if (descendant.Callback is { } handle) { Owner.Unregister(handle); descendant.Callback = null; }
        return removed;
    }
    private static IEnumerable<IseMenuItem> Descendants(IseMenuItem item)
    {
        yield return item;
        foreach (var child in item.Submenus)
            foreach (var descendant in Descendants(child)) yield return descendant;
    }
}

public sealed class IseSnippetCollection(IseObjectProxy owner, string id) : IseCollection<IseSnippet>(owner, id)
{
    public void Load(string fullPath) => Call(nameof(Load), fullPath);
}

public sealed class IseSnippet(IseObjectProxy owner, string id) : IseProxy(owner, id)
{
    public string Title => ReadText(nameof(Title));
    public string DisplayTitle => ReadText(nameof(DisplayTitle));
    public string Description => ReadText(nameof(Description));
    public string Author => ReadText(nameof(Author));
    public string CodeFragment => ReadText(nameof(CodeFragment));
    public string Code => ReadText(nameof(Code));
    public string Text => ReadText(nameof(Text));
    public int CaretOffset => Integer(nameof(CaretOffset));
    public bool Indent => Boolean(nameof(Indent));
    public bool IsBuiltIn => Boolean(nameof(IsBuiltIn));
    public bool IsDefault => Boolean(nameof(IsDefault));
    public string Compatibility => ReadText(nameof(Compatibility));
    public string? FullPath => Get(nameof(FullPath)).GetString();
    public string? SchemaVersion => Get(nameof(SchemaVersion)).GetString();
    public bool? IsTabSpecific => Get(nameof(IsTabSpecific)) is { ValueKind: not JsonValueKind.Null } value ? value.GetBoolean() : null;
    public (string Text, int Caret) Expand(string indentation, string newLine)
    {
        var result = Call(nameof(Expand), indentation, newLine);
        return (result[0].GetString()!, result[1].GetInt32());
    }
    public override string ToString() => Title;
}

public sealed class IseUnsupportedTools(IseObjectProxy owner, string id) : IseProxy(owner, id)
{
    public object Add(string name, Type controlType) => throw Unsupported();
    public object Add(string name, Type controlType, bool isVisible) => throw Unsupported();
    public void Clear() => throw Unsupported();
    private static PSNotSupportedException Unsupported() => new("WPF ISE add-on tools are not supported by Iseberg's Avalonia host. Use script-based AddOnsMenu actions.");
}

public sealed class IseOptions(IseObjectProxy owner, string id) : IseProxy(owner, id)
{
    public string FontName { get => ReadText(nameof(FontName)); set => Set(nameof(FontName), value); }
    public double FontSize { get => Number(nameof(FontSize)); set => Set(nameof(FontSize), value); }
    public double Zoom { get => Number(nameof(Zoom)); set => Set(nameof(Zoom), value); }
    public string SelectedScriptPaneState { get => ReadText(nameof(SelectedScriptPaneState)); set => Set(nameof(SelectedScriptPaneState), value); }
    public bool ShowLineNumbers { get => Boolean(nameof(ShowLineNumbers)); set => Set(nameof(ShowLineNumbers), value); }
    public bool ShowOutlining { get => Boolean(nameof(ShowOutlining)); set => Set(nameof(ShowOutlining), value); }
    public bool WordWrap { get => Boolean(nameof(WordWrap)); set => Set(nameof(WordWrap), value); }
    public bool ShowToolBar { get => Boolean(nameof(ShowToolBar)); set => Set(nameof(ShowToolBar), value); }
    public bool ShowWarningBeforeSavingOnRun { get => Boolean(nameof(ShowWarningBeforeSavingOnRun)); set => Set(nameof(ShowWarningBeforeSavingOnRun), value); }
    public bool ShowWarningForDuplicateFiles { get => Boolean(nameof(ShowWarningForDuplicateFiles)); set => Set(nameof(ShowWarningForDuplicateFiles), value); }
    public bool ShowDefaultSnippets { get => Boolean(nameof(ShowDefaultSnippets)); set => Set(nameof(ShowDefaultSnippets), value); }
    public bool ShowIntellisenseInScriptPane { get => Boolean(nameof(ShowIntellisenseInScriptPane)); set => Set(nameof(ShowIntellisenseInScriptPane), value); }
    public bool ShowIntellisenseInConsolePane { get => Boolean(nameof(ShowIntellisenseInConsolePane)); set => Set(nameof(ShowIntellisenseInConsolePane), value); }
    public bool UseEnterToSelectInScriptPaneIntellisense { get => Boolean(nameof(UseEnterToSelectInScriptPaneIntellisense)); set => Set(nameof(UseEnterToSelectInScriptPaneIntellisense), value); }
    public bool UseEnterToSelectInConsolePaneIntellisense { get => Boolean(nameof(UseEnterToSelectInConsolePaneIntellisense)); set => Set(nameof(UseEnterToSelectInConsolePaneIntellisense), value); }
    public int IntellisenseTimeoutInSeconds { get => Integer(nameof(IntellisenseTimeoutInSeconds)); set => Set(nameof(IntellisenseTimeoutInSeconds), value); }
    public int AutoSaveMinuteInterval { get => Integer(nameof(AutoSaveMinuteInterval)); set => Set(nameof(AutoSaveMinuteInterval), value); }
    public int MruCount { get => Integer(nameof(MruCount)); set => Set(nameof(MruCount), value); }
    public bool UseLocalHelp { get => Boolean(nameof(UseLocalHelp)); set => Set(nameof(UseLocalHelp), value); }
    public bool FixedWidthFontsOnly { get => Boolean(nameof(FixedWidthFontsOnly)); set => Set(nameof(FixedWidthFontsOnly), value); }
    public bool LoadProfiles { get => Boolean(nameof(LoadProfiles)); set => Set(nameof(LoadProfiles), value); }
    public IseThemeSnapshot Theme
    {
        get
        {
            var value = Get(nameof(Theme));
            return new()
            {
                Name = value.GetProperty("Name").GetString()!,
                Colors = value.GetProperty("Colors").EnumerateObject().ToDictionary(color => color.Name, color => color.Value.GetString()!)
            };
        }
        set => Set(nameof(Theme), null);
    }
    public object TokenColors { get => Get(nameof(TokenColors)); set => Set(nameof(TokenColors), null); }
    public object ConsoleTokenColors { get => Get(nameof(ConsoleTokenColors)); set => Set(nameof(ConsoleTokenColors), null); }
    public object XmlTokenColors { get => Get(nameof(XmlTokenColors)); set => Set(nameof(XmlTokenColors), null); }
    public void RestoreDefaults() => Call(nameof(RestoreDefaults));
    public void RestoreDefaultTokenColors() => Call(nameof(RestoreDefaultTokenColors));
    public void RestoreDefaultConsoleTokenColors() => Call(nameof(RestoreDefaultConsoleTokenColors));
    public void RestoreDefaultXmlTokenColors() => Call(nameof(RestoreDefaultXmlTokenColors));
}

public sealed class IseThemeSnapshot
{
    public string Name { get; set; } = "";
    public Dictionary<string, string> Colors { get; set; } = [];
    public IseThemeSnapshot Copy() => new() { Name = Name, Colors = new(Colors) };
}
