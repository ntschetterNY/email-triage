using EmailTriage.Core.Models;

namespace EmailTriage.Core.Services;

/// <summary>
/// The rules that move action items between stages on their own, so the
/// board reflects reality without the user dragging cards about:
/// adding a blocker or hand-off puts an item in Waiting, and clearing the last
/// one brings it back to Doing.
/// </summary>
public static class ActionWorkflow
{
    public static readonly ActionStage[] Stages =
        { ActionStage.ToDo, ActionStage.Doing, ActionStage.Waiting, ActionStage.Done };

    /// <summary>The stage after a blocker or assignment was added, or null to stay put.</summary>
    public static ActionStage? AfterWaitAdded(ActionItem item) =>
        item.Stage is ActionStage.ToDo or ActionStage.Doing ? ActionStage.Waiting : null;

    /// <summary>The stage after a blocker or assignment was cleared, or null to stay put.</summary>
    public static ActionStage? AfterWaitCleared(ActionItem item) =>
        item.Stage == ActionStage.Waiting && !item.IsWaiting ? ActionStage.Doing : null;

    /// <summary>The next stage along (or back), stopping at either end.</summary>
    public static ActionStage Step(ActionStage from, int delta)
    {
        var index = Array.IndexOf(Stages, from);
        return Stages[Math.Clamp(index + delta, 0, Stages.Length - 1)];
    }

    /// <summary>
    /// Who the open waits are on, with how many items each person holds up -
    /// the "who do I chase" summary at the top of the board.
    /// </summary>
    public static IReadOnlyList<(string Person, int Count, bool AnyOverdue)> WaitingOn(IEnumerable<ActionItem> items)
    {
        var tally = new Dictionary<string, (string Name, HashSet<long> Items, bool Overdue)>(StringComparer.OrdinalIgnoreCase);

        void Add(string person, long itemId, bool overdue)
        {
            person = person.Trim();
            if (person.Length == 0) return;
            if (!tally.TryGetValue(person, out var t)) t = (person, new HashSet<long>(), false);
            t.Items.Add(itemId);
            tally[person] = (t.Name, t.Items, t.Overdue || overdue);
        }

        foreach (var item in items.Where(i => !i.IsComplete))
        {
            foreach (var b in item.Blockers.Where(b => !b.IsResolved)) Add(b.WaitingOn, item.Id, b.IsOverdue);
            foreach (var a in item.Assignments.Where(a => !a.IsDone)) Add(a.PersonName, item.Id, a.IsOverdue);
        }

        return tally.Values
            .Select(t => (t.Name, t.Items.Count, t.Overdue))
            .OrderByDescending(t => t.Overdue)
            .ThenByDescending(t => t.Count)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>True when the item involves the person, as blocker or assignee.</summary>
    public static bool Involves(ActionItem item, string person) =>
        item.Blockers.Any(b => !b.IsResolved && string.Equals(b.WaitingOn.Trim(), person, StringComparison.OrdinalIgnoreCase))
        || item.Assignments.Any(a => !a.IsDone && string.Equals(a.PersonName.Trim(), person, StringComparison.OrdinalIgnoreCase));
}
