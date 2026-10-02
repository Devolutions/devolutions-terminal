using Avalonia.Controls;
using Devolutions.Terminal.App.Actions;

namespace Devolutions.Terminal.App.Views;

public partial class MainWindow
{
    private void DetachPaneControls(TerminalTab tab)
    {
        foreach (var pane in tab.Panes.Leaves())
        {
            DetachControl(pane.Control);
            if (_paneScrollBars.TryGetValue(pane, out var scrollBar))
            {
                DetachControl(scrollBar);
            }
        }
    }

    private static void DetachControl(Control control)
    {
        if (control.Parent is Decorator decorator)
        {
            decorator.Child = null;
        }
        else if (control.Parent is Panel panel)
        {
            panel.Children.Remove(control);
        }
    }

    private async Task<bool> ConfirmCloseAsync(IEnumerable<TerminalPane> panes, bool automaticExit = false)
    {
        var snapshot = panes.ToArray();
        var sessions = snapshot.Select(pane => pane.Control.ProcessMetadata).ToArray();
        var running = snapshot.Count(pane => pane.Control.IsRunning);
        var unsavedRecordings = snapshot.Count(pane => pane.Control.HasUnsavedRecording);
        if (!CloseConfirmationPolicy.RequiresConfirmation(
                _settings.ConfirmOnClose,
                running,
                unsavedRecordings,
                automaticExit))
        {
            return true;
        }

        var message = (running, unsavedRecordings) switch
        {
            (> 0, > 0) =>
                $"Close {running} running terminal session(s)? " +
                $"{unsavedRecordings} unsaved recording(s) will be discarded.",
            (_, > 0) =>
                $"Close terminal sessions? {unsavedRecordings} unsaved recording(s) will be discarded.",
            _ => $"Close {running} running terminal session(s)? Unsaved work may be lost.",
        };
        return await _confirmationDialog.ShowAsync(this, "Close terminal sessions",
                   message, "Close sessions").ConfigureAwait(true) &&
               panes.SequenceEqual(snapshot) &&
               snapshot.Select(pane => pane.Control.ProcessMetadata).SequenceEqual(sessions);
    }

    private async Task ConfirmWindowCloseAsync()
    {
        _closeConfirmationPending = true;
        try
        {
            if (await ConfirmCloseAsync(_tabs.SelectMany(tab => tab.Panes.Leaves())).ConfigureAwait(true) &&
                !_isClosed)
            {
                _closeApproved = true;
                Close();
            }
        }
        finally
        {
            _closeConfirmationPending = false;
            _closeApproved = false;
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (AboutOverlay.IsVisible)
        {
            e.Cancel = true;
            CloseAbout();
            return;
        }

        base.OnClosing(e);
        var panes = _tabs.SelectMany(tab => tab.Panes.Leaves()).ToArray();
        if (!e.Cancel && !_closeApproved &&
            CloseConfirmationPolicy.RequiresConfirmation(_settings.ConfirmOnClose,
                panes.Count(pane => pane.Control.IsRunning),
                panes.Count(pane => pane.Control.HasUnsavedRecording)))
        {
            e.Cancel = true;
            if (!_closeConfirmationPending)
            {
                _ = ConfirmWindowCloseAsync();
            }
        }
    }

    protected override async void OnClosed(EventArgs e)
    {
        _isClosed = true;
        _recordingUiTimer.Stop();
        if (!_layoutPersisted && _tabs.Count > 0)
        {
            TryPersistCurrentLayout(CaptureLayout());
        }

        foreach (var tab in _tabs.ToArray())
        {
            foreach (var pane in tab.Panes.Leaves())
            {
                await pane.Control.CloseAsync().ConfigureAwait(true);
            }
        }

        foreach (var bitmap in _tabIconCache.Values)
        {
            bitmap.Dispose();
        }

        _tabIconCache.Clear();
        base.OnClosed(e);
    }
}
