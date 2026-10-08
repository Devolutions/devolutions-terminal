using Iseberg.Core;

namespace Iseberg;

public sealed partial class WorkbenchControl
{
    private readonly Dictionary<SessionModel, ProgressTracker> progressTrackers = [];

    private void UpdateProgress(SessionModel session, ProgressUpdate progress)
    {
        if (windowClosed || !Workbench.Sessions.Contains(session)) return;
        if (!progressTrackers.TryGetValue(session, out var tracker))
            progressTrackers.Add(session, tracker = new());
        tracker.Update(progress);
        if (session == displayedSession) RenderProgress();
    }

    private void RenderProgress()
    {
        if (windowClosed || displayedSession is not { } session || !progressTrackers.TryGetValue(session, out var tracker))
        {
            ProgressPanel.IsVisible = false;
            return;
        }
        if (session.Engine.State == SessionState.Ready) tracker.Clear();
        var active = tracker.Active;
        ProgressPanel.IsVisible = active.Count > 0;
        ProgressText.Text = string.Join(Environment.NewLine, active.Select(progress =>
            new string(' ', tracker.Depth(progress) * 2) + progress.Activity + " - " + progress.Status +
            (string.IsNullOrEmpty(progress.CurrentOperation) ? "" : " - " + progress.CurrentOperation)));
        var selected = active.LastOrDefault();
        ScriptProgress.IsIndeterminate = selected?.Percent < 0;
        ScriptProgress.Value = Math.Max(0, selected?.Percent ?? 0);
    }
}
