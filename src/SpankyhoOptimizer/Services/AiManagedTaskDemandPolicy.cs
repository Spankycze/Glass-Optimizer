namespace SpankyhoOptimizer.Services;

/// <summary>
/// A disabled but actively executing Task Scheduler task is live OS/application
/// demand. An AI-owned disable must not suppress its next scheduled execution.
/// This is a one-way restore gate, never admission for a new task disable.
/// </summary>
internal static class AiManagedTaskDemandPolicy
{
    internal static bool RequiresImmediateRestore(
        AiSystemRuntimeItemState state, TaskRuntimeState task) =>
        state.Kind.Equals("task", System.StringComparison.OrdinalIgnoreCase) &&
        state.Managed && state.Sleeping &&
        state.OriginalTaskEnabled == true &&
        !task.Enabled && task.Running;
}
