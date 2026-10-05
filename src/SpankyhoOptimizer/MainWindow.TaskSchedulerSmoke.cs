using System;
using System.Threading.Tasks;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    public async Task RunTaskSchedulerUiSmokeAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await ShowSectionAsync("tasks");

        if (_lastNavigationErrorId != null)
            throw new InvalidOperationException("Tasks -> AI Runtime navigation error: " + _lastNavigationErrorId);
        if (!string.Equals(_section, "ai-runtime", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Legacy Task Scheduler route did not redirect to AI System Runtime.");
        if (ContentStack.Children.Count == 0)
            throw new InvalidOperationException("AI System Runtime rendered no content for legacy Task Scheduler route.");

        AiSystemRuntimeConfig cfg = AiSystemResourceManagerService.LoadConfig();
        if (!cfg.ScheduledTasksEnabled)
            LogService.Info("tasks", "TASKS_AI_RUNTIME_REDIRECT_OK scheduledTasks=off");
        else
            LogService.Info("tasks", "TASKS_AI_RUNTIME_REDIRECT_OK scheduledTasks=on");

        LogService.Info("tasks", $"TASK_SCHEDULER_UI_SMOKE_OK replacedBy=ai-runtime ms={sw.ElapsedMilliseconds}");
    }
}
