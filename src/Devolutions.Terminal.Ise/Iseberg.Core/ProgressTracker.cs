namespace Iseberg.Core;

public sealed class ProgressTracker
{
    private readonly Dictionary<(long Source, int Activity), ProgressUpdate> activities = [];
    public IReadOnlyList<ProgressUpdate> Active => activities.Values.ToArray();

    public void Update(ProgressUpdate progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (progress.ActivityId < 0 || progress.ParentActivityId == progress.ActivityId || progress.Percent is < -1 or > 100)
            throw new ArgumentException("Invalid progress identity, hierarchy, or percentage.", nameof(progress));
        var key = (progress.SourceId, progress.ActivityId);
        if (!progress.Completed) { activities[key] = progress; return; }
        var removed = new HashSet<int> { progress.ActivityId };
        while (activities.Values.Any(activity => activity.SourceId == progress.SourceId &&
            removed.Contains(activity.ParentActivityId) && removed.Add(activity.ActivityId))) { }
        foreach (var activity in removed) activities.Remove((progress.SourceId, activity));
    }

    public int Depth(ProgressUpdate progress)
    {
        var depth = 0;
        var visited = new HashSet<int> { progress.ActivityId };
        while (progress.ParentActivityId >= 0 && visited.Add(progress.ParentActivityId) &&
            activities.TryGetValue((progress.SourceId, progress.ParentActivityId), out var parent))
        {
            depth++;
            progress = parent;
        }
        return depth;
    }

    public void Clear() => activities.Clear();
}
