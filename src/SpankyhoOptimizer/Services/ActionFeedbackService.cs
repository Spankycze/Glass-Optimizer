using System;
using System.Collections.Generic;
using System.Linq;

namespace SpankyhoOptimizer.Services;

public enum ActionFeedbackKind { Applied, Verified, RestartRequired, Unsupported, Failed, Info }
public sealed record ActionFeedbackEntry(DateTimeOffset Time, string Area, string Action, ActionFeedbackKind Kind, string Detail);

public static class ActionFeedbackService
{
    private static readonly object Gate = new();
    private static readonly LinkedList<ActionFeedbackEntry> Items = new();
    public static void Record(string area, string action, ActionFeedbackKind kind, string detail = "")
    {
        lock (Gate)
        {
            Items.AddFirst(new ActionFeedbackEntry(DateTimeOffset.Now, area, action, kind, detail));
            while (Items.Count > 30) Items.RemoveLast();
        }
    }
    public static ActionFeedbackEntry? Latest { get { lock (Gate) return Items.First?.Value; } }
    public static ActionFeedbackEntry[] Recent(int count = 10) { lock (Gate) return Items.Take(Math.Max(1, count)).ToArray(); }
    public static bool RestartPending { get { lock (Gate) return Items.Any(x => x.Kind == ActionFeedbackKind.RestartRequired); } }
}
