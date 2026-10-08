using System.Diagnostics;
using Avalonia.Controls;
using Devolutions.Terminal.App.Connections;
using Devolutions.Terminal.App.Models;
using Devolutions.Terminal;
using Devolutions.Terminal.Core;
using Devolutions.Terminal.Settings;

namespace Devolutions.Terminal.App.Views;

public partial class MainWindow
{
    public static bool SupportsPowerShellIse =>
#if POWERSHELL_ISE
        true;
#else
        false;
#endif

    private async Task<TerminalTab> CreatePowerShellIseTabAsync(
        ProfileSettings profile, TabLayoutDescriptor? restored = null, bool regenerateIdentities = false)
    {
#if POWERSHELL_ISE
        if (_isClosed || _closeConfirmationPending || _applicationClosePending)
            throw new InvalidOperationException("Cannot open Iseberg while DT is closing.");
        await PowerShellIseRuntime.InitializeAsync().ConfigureAwait(true);
        return await CreatePowerShellIseTabCoreAsync(profile, restored, regenerateIdentities).ConfigureAwait(true);
#else
        await Task.CompletedTask;
        throw new NotSupportedException("This terminal-only build does not include Iseberg. Use an Iseberg-enabled managed DT distribution.");
#endif
    }

#if POWERSHELL_ISE
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private async Task<TerminalTab> CreatePowerShellIseTabCoreAsync(
        ProfileSettings profile, TabLayoutDescriptor? restored, bool regenerateIdentities)
    {
        if (_isClosed || _closeConfirmationPending || _applicationClosePending)
            throw new InvalidOperationException("Cannot open Iseberg while DT is closing.");
        PowerShellIseTab.ValidateProfile(profile);
        var session = restored?.Root.Session is { } saved ? CloneSession(saved) : CreateSessionDescriptor(profile);
        if (regenerateIdentities) session.SessionId = Guid.NewGuid();
        if (restored is null && !regenerateIdentities)
        {
            var recovered = await PowerShellIseWorkspaceCatalog.FindRecoverableAsync(
                Path.Combine(SettingsService.SettingsDirectory, "PowerShellIse"), profile.Guid);
            if (recovered is { } workspaceId) session.SessionId = workspaceId;
        }
        var pane = CreatePane(profile, session);
        var tab = new TerminalTab(
            restored is not null && !regenerateIdentities ? restored.TabId : Guid.NewGuid(),
            new Panes.PaneTree<TerminalPane>(pane))
        {
            Title = restored?.Title ?? pane.Title,
            CustomTitle = restored?.CustomTitle,
            Color = restored?.Color ?? profile.TabColor,
        };
        var content = new PowerShellIseTab(profile, pane.Control, BuildPaneLeaf(tab, pane), session.SessionId);
        tab.CustomContent = content;
        content.Workbench.CloseRequested += async (_, _) => await CloseTabAsync(tab).ConfigureAwait(true);
        content.Workbench.ErrorOccurred += (_, error) => ReportHostedTabError(error.Title, error.Exception);
        content.Workbench.OptionsRequested += (_, _) => OpenSettingsTab(profile.Guid);
        _tabCollection.Add(tab);
        ActivateTab(tab);
        try
        {
            await content.InitializeAsync().ConfigureAwait(true);
            if (ReferenceEquals(_activeTab, tab)) content.FocusContent();
            return tab;
        }
        catch
        {
            try { await content.DisposeAsync().ConfigureAwait(true); }
            finally
            {
                _tabCollection.Remove(tab);
                DetachControl(content);
                if (_tabCollection.ActiveTab is { } replacement) ActivateTab(replacement);
                else { _activeTab = null; RebuildTabs(); }
            }
            throw;
        }
    }
#endif

    private void ReportHostedTabError(string title, Exception error)
    {
        Trace.TraceError("{0}: {1}", title, error);
        if (!_isClosed) ShowNotification(new TerminalNotification(title, error.Message));
    }

    private async Task<bool> CompleteHostedTabCloseAsync(IHostedTabContent content)
    {
        try { return await content.RequestCloseAsync().ConfigureAwait(true); }
        catch (Exception error)
        {
            ReportHostedTabError("Unable to close Iseberg cleanly", error);
            // Disposal failures must not leave an unusable, already-disposed tab attached.
            return content.IsDisposed;
        }
    }

    private async Task<bool> ConfirmTabsCloseAsync(IEnumerable<TerminalTab> tabs, bool automaticExit = false)
    {
        var snapshot = tabs.ToArray();
        var hosted = snapshot.Select(tab => tab.CustomContent).OfType<IHostedTabContent>().ToArray();
        var approved = false;
        try
        {
            if (!await ConfirmCloseAsync(snapshot.Where(tab => tab.IsTerminalTab ||
                    tab.CustomContent is IHostedTerminalContent { Terminal.HasUnsavedRecording: true })
                    .SelectMany(tab => tab.Panes.Leaves()), automaticExit).ConfigureAwait(true))
                return false;
            foreach (var content in hosted)
                if (!await content.PrepareCloseAsync().ConfigureAwait(true)) return false;
            approved = snapshot.All(tab => _tabs.Contains(tab) && !tab.IsClosing);
            return approved;
        }
        catch (Exception error)
        {
            ReportHostedTabError("Unable to close workbench", error);
            return false;
        }
        finally
        {
            if (!approved)
                foreach (var content in hosted) content.CancelClosePreparation();
        }
    }
}
