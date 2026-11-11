using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private List<UIElement>? _aiRuntimePageCache;
    private bool _aiRuntimePageCacheSmokeActive;

    private bool TryAttachAiRuntimePageCache()
    {
        // Build a fresh runtime page to avoid stale asynchronous controls.
        InvalidateAiRuntimePageCache();
        return false;
    }

    private void CaptureAiRuntimePageCache()
    {
        // Production never reuses detached interactive controls. Avoid keeping
        // their visual trees rooted solely for a diagnostic smoke assertion.
        if (!_aiRuntimePageCacheSmokeActive) return;
        _aiRuntimePageCache = SnapshotChildren(ContentStack);
    }

    private void InvalidateAiRuntimePageCache()
    {
        _aiRuntimePageCache = null;
        // No retained timestamp: diagnostic snapshots are intentionally ephemeral.
    }

    private Task RefreshAiRuntimePageAsync()
    {
        InvalidateAiRuntimePageCache();
        // A real refresh must rebuild even if the current runtime page is
        // already attached. Ordinary repeat sidebar clicks remain no-op.
        return ShowSectionAsync("ai-runtime", forceNavigation: true);
    }
}

public sealed partial class MainWindow
{
    internal async Task RunAiRuntimePageCacheSmokeAsync()
    {
        _aiRuntimePageCacheSmokeActive = true;
        try { await RunAiRuntimePageCacheSmokeCoreAsync(); }
        finally
        {
            _aiRuntimePageCacheSmokeActive = false;
            InvalidateAiRuntimePageCache();
        }
    }

    private async Task RunAiRuntimePageCacheSmokeCoreAsync()
    {
        InvalidateAiRuntimePageCache();
        await ShowSectionAsync("ai-runtime");
        var first = _aiRuntimePageCache
            ?? throw new InvalidOperationException("AI Runtime page snapshot was not captured.");

        var firstBody = first.OfType<Microsoft.UI.Xaml.Controls.StackPanel>()
            .Single(x => string.Equals(x.Tag as string, "AiRuntimeBodyHost", StringComparison.Ordinal));
        int firstBodyCount = firstBody.Children.Count;
        await ShowSectionAsync("overview");
        await Task.Delay(100);
        if (firstBody.Children.Count != firstBodyCount)
            throw new InvalidOperationException("A cancelled AI Runtime load changed its detached page.");

        await ShowSectionAsync("ai-runtime");

        var second = _aiRuntimePageCache
            ?? throw new InvalidOperationException("AI Runtime fresh page snapshot was not captured.");

        if (ReferenceEquals(first, second))
            throw new InvalidOperationException("AI Runtime reused cached interactive controls; fresh rebuild is required.");

        var body = second.OfType<Microsoft.UI.Xaml.Controls.StackPanel>()
            .Single(x => string.Equals(x.Tag as string, "AiRuntimeBodyHost", StringComparison.Ordinal));
        bool observedIntermediateBody = false;
        bool observeBody = true;
        void ObserveBody()
        {
            if (!observeBody) return;
            if (body.Children.Count is > 0 and < 4 &&
                body.Children[0] is Microsoft.UI.Xaml.Controls.Grid)
                observedIntermediateBody = true;
            if (body.Children.Count < 4)
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ObserveBody);
        }
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ObserveBody);
        for (int i = 0; i < 80 && body.Children.Count < 4; i++)
            await Task.Delay(25);
        observeBody = false;
        if (body.Children.Count < 4)
            throw new InvalidOperationException("AI Runtime did not render its real status and controls after deferred loading.");
        if (!observedIntermediateBody)
            throw new InvalidOperationException("AI Runtime body construction never returned control to the dispatcher before completing.");

        await ShowSectionAsync("overview");
        await ShowSectionAsync("ai-runtime");
        var partialBody = (_aiRuntimePageCache ?? throw new InvalidOperationException("Missing partial Runtime page."))
            .OfType<Microsoft.UI.Xaml.Controls.StackPanel>()
            .Single(x => string.Equals(x.Tag as string, "AiRuntimeBodyHost", StringComparison.Ordinal));
        var cancelled = new TaskCompletionSource<(int ChildCount, Task Navigation)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bool watchPartial = true;
        void CancelDuringBody()
        {
            if (!watchPartial) return;
            if (partialBody.Children.Count is > 0 and < 4 &&
                partialBody.Children[0] is Microsoft.UI.Xaml.Controls.Grid)
            {
                int count = partialBody.Children.Count;
                Task navigation = ShowSectionAsync("overview");
                cancelled.TrySetResult((count, navigation));
                return;
            }
            if (partialBody.Children.Count >= 4)
            {
                cancelled.TrySetException(new InvalidOperationException("Runtime completed before mid-render cancellation could be exercised."));
                return;
            }
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, CancelDuringBody);
        }
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, CancelDuringBody);
        try
        {
            var result = await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await result.Navigation;
            await Task.Delay(100);
            if (_section != "overview" || partialBody.Children.Count != result.ChildCount)
                throw new InvalidOperationException("Cancelled Runtime stages changed the old body or replaced Overview.");
        }
        finally { watchPartial = false; }

        LogService.Info("performance", "AI_RUNTIME_PAGE_FRESH_REBUILD_SMOKE_OK interactiveControlsFresh=true cacheReuse=false deferredBody=true dispatcherResponsive=true midRenderCancel=true staleLoadGuard=true");
    }
}
