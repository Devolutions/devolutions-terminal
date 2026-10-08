using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Devolutions.Terminal.App.Actions;

namespace Devolutions.Terminal.App.Views;

public partial class MainWindow
{
    public static async Task<bool> RequestApplicationCloseAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        ArgumentNullException.ThrowIfNull(desktop);
        var windows = desktop.Windows.OfType<MainWindow>().ToArray();
        if (_applicationClosePending || windows.Any(window => window._closeConfirmationPending)) return false;
        _applicationClosePending = true;
        foreach (var window in windows) window._closeConfirmationPending = true;
        var completed = false;
        try
        {
            foreach (var window in windows)
                if (!await window.ConfirmTabsCloseAsync(window._tabs).ConfigureAwait(true)) return false;
            if (!desktop.Windows.OfType<MainWindow>().SequenceEqual(windows)) return false;
            foreach (var window in windows)
            {
                var layout = window.CaptureLayout();
                foreach (var content in window._tabs.Select(tab => tab.CustomContent).OfType<IHostedTabContent>())
                    if (!await window.CompleteHostedTabCloseAsync(content).ConfigureAwait(true)) return false;
                window.TryPersistCurrentLayout(layout);
                window._closeApproved = true;
                window.Close();
            }
            completed = true;
            desktop.Shutdown();
            return true;
        }
        catch (Exception error)
        {
            foreach (var window in windows.Where(window => !window._isClosed))
                window.ReportHostedTabError("Unable to quit DT", error);
            return false;
        }
        finally
        {
            _applicationClosePending = false;
            foreach (var window in windows)
            {
                window._closeConfirmationPending = false;
                window._closeApproved = false;
                if (!completed)
                    foreach (var content in window._tabs.Select(tab => tab.CustomContent).OfType<IHostedTabContent>())
                        content.CancelClosePreparation();
            }
        }
    }

    private void DetachPaneControls(TerminalTab tab)
    {
        if (tab.CustomContent is { } custom)
        {
            DetachControl(custom);
            return;
        }
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
            if (await ConfirmTabsCloseAsync(_tabs).ConfigureAwait(true) &&
                !_isClosed)
            {
                var layout = CaptureLayout();
                foreach (var content in _tabs.Select(tab => tab.CustomContent).OfType<IHostedTabContent>())
                    if (!await CompleteHostedTabCloseAsync(content).ConfigureAwait(true)) return;
                TryPersistCurrentLayout(layout);
                _closeApproved = true;
                Close();
            }
        }
        catch (Exception error)
        {
            ReportHostedTabError("Unable to close window", error);
            foreach (var content in _tabs.Select(tab => tab.CustomContent).OfType<IHostedTabContent>())
                content.CancelClosePreparation();
        }
        finally
        {
            if (!_isClosed)
                foreach (var content in _tabs.Select(tab => tab.CustomContent).OfType<IHostedTabContent>())
                    content.CancelClosePreparation();
            _closeConfirmationPending = false;
            _closeApproved = false;
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (AboutOverlay.IsVisible && !_closeApproved)
        {
            e.Cancel = true;
            CloseAbout();
            return;
        }

        base.OnClosing(e);
        var panes = _tabs.Where(tab => tab.IsTerminalTab).SelectMany(tab => tab.Panes.Leaves()).ToArray();
        if (!e.Cancel && !_closeApproved &&
            (_tabs.Any(tab => tab.CustomContent is IHostedTabContent) ||
            CloseConfirmationPolicy.RequiresConfirmation(_settings.ConfirmOnClose,
                panes.Count(pane => pane.Control.IsRunning),
                panes.Count(pane => pane.Control.HasUnsavedRecording))))
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
            if (tab.CustomContent is IHostedTabContent content)
            {
                try { await content.DisposeAsync().ConfigureAwait(true); }
                catch (Exception error) { ReportHostedTabError("Unable to dispose workbench", error); }
            }
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
