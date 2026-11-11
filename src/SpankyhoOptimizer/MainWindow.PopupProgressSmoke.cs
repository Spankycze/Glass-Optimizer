using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    // Explicit smoke only. Exercise the real popup state machine with controlled download delegates.
    private static async Task VerifyPopupRetryProgressAsync(bool assertReset)
    {
        var first = new TaskCompletionSource<DesktopUpdateCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<DesktopUpdateCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        IProgress<DesktopUpdateProgress>? oldProgress = null, currentProgress = null;
        int calls = 0;
        var popup = MakeContinuationPopup((progress, _) =>
        {
            if (++calls == 1) { oldProgress = progress; return first.Task; }
            currentProgress = progress; return second.Task;
        });
        Task? running = null;
        try
        {
            popup.ShowWithoutActivation(); await Task.Delay(100);
            running = InvokePopupPrimaryAsync(popup);
            oldProgress!.Report(new DesktopUpdateProgress(78, "FIRST_ATTEMPT", "old detail"));
            await Task.Delay(100);
            if (PopupField<Border>(popup, "_fill").Width <= 0)
                throw new InvalidOperationException("Missing first-attempt progress baseline.");
            first.SetException(new InvalidOperationException("controlled failure"));
            await running;
            running = InvokePopupPrimaryAsync(popup);
            if (calls != 2) throw new InvalidOperationException("Retry did not start exactly once.");
            if (assertReset)
            {
                if (PopupField<Border>(popup, "_fill").Width != 0 || PopupField<TextBlock>(popup, "_percent").Text != "0 %")
                    throw new InvalidOperationException("Retry shows a previous transfer's filled progress bar instead of zero.");
            }
            else
            {
                currentProgress!.Report(new DesktopUpdateProgress(12, "CURRENT_ATTEMPT", "current detail"));
                await Task.Delay(100);
                oldProgress.Report(new DesktopUpdateProgress(99, "STALE_ATTEMPT", "stale detail"));
                await Task.Delay(100);
                if (PopupField<TextBlock>(popup, "_status").Text != "CURRENT_ATTEMPT" ||
                    PopupField<TextBlock>(popup, "_percent").Text != "12 %" ||
                    PopupField<TextBlock>(popup, "_detail").Text != "current detail")
                    throw new InvalidOperationException("Late progress from a failed attempt overwrote the active retry.");
            }
        }
        finally
        {
            first.TrySetResult(new DesktopUpdateCompletion("Done", "Close"));
            second.TrySetResult(new DesktopUpdateCompletion("Done", "Close"));
            if (running != null) await running;
            popup.Close();
        }
    }
}
