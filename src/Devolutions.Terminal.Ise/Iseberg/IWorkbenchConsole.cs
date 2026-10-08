using Avalonia.Controls;
using Iseberg.Core;

namespace Iseberg;

/// <summary>Host-owned console presentation and transport for the workbench's existing runspace.</summary>
public interface IWorkbenchConsole : IAsyncDisposable
{
    Control View { get; }
    bool HasSelection { get; }
    bool IsInputEnabled { get; }
    Task StartAsync(WorkbenchControl workbench, SessionModel session);
    void Write(OutputEntry entry);
    void RefreshState(bool acceptsCommands, bool inputDisabled);
    void Focus();
    void Clear();
    Task CopyAsync();
    Task PasteAsync();
    void SelectAll();
    Task CompleteAsync(bool backwards = false);
    /// <summary>Sets the code font size in device-independent pixels, including zoom.</summary>
    void SetFontSize(double size);
    void ApplyAppearance(EditorTheme theme, string fontFamily);
    bool HasPendingInput { get; }
    bool CanUndo => false;
    bool CanRedo => false;
    bool CanCut => false;
    string InputText => "";
    int CaretOffset => 0;
    void Undo() => throw new NotSupportedException("Console undo is not supported.");
    void Redo() => throw new NotSupportedException("Console redo is not supported.");
    Task CutAsync() => throw new NotSupportedException("Console cut is not supported.");
    void ApplyPreferences(UserSettings preferences) { }
}
