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
        if (!CloseConfirmationPolicy.RequiresConfirmation(_settings.ConfirmOnClose, running, automaticExit))
        {
            return true;
        }

        return await _confirmationDialog.ShowAsync(this, "Close terminal sessions",
                   $"Close {running} running terminal session(s)? Unsaved work may be lost.", "Close sessions").ConfigureAwait(true) &&
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
        if (!e.Cancel && !_closeApproved &&
            CloseConfirmationPolicy.RequiresConfirmation(_settings.ConfirmOnClose,
                _tabs.SelectMany(tab => tab.Panes.Leaves()).Count(pane => pane.Control.IsRunning)))
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
