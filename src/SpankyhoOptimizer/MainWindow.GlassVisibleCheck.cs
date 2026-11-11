using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using SpankyhoOptimizer.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using WinRT.Interop;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    // Explicit diagnostic opt-in only; normal application startup never calls this path.
    private async Task RunVisibleGlassCaptureIfRequestedAsync()
    {
        if (Environment.GetEnvironmentVariable("SPANKY_GLASS_CAPTURE") != "1" && !Array.Exists(Environment.GetCommandLineArgs(), arg => arg == "--visible-glass-capture")) return;
        var main = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_windowHandle));
        var oldPosition = main.Position; var oldSize = main.Size;
        var presenter = main.Presenter as OverlappedPresenter;
        bool oldTop = presenter?.IsAlwaysOnTop ?? false;
        var area = DisplayArea.GetFromWindowId(main.Id, DisplayAreaFallback.Primary).WorkArea;
        var pending = new TaskCompletionSource<DesktopUpdateCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        IProgress<DesktopUpdateProgress>? reporter = null;
        var popup = new DesktopUpdateNotificationWindow("TEST AKTUALIZACE", "Spankyho Optimizer", "Kontrola vzhledu",
            "Pouze vizu\u00e1ln\u00ed test. Nic se nestahuje ani neinstaluje.",
            (progress, _) => { reporter = progress; return pending.Task; });
        Task? action = null;
        try
        {
            await ShowSectionAsync("overview");
            main.MoveAndResize(new RectInt32(area.X + 20, area.Y + 20,
                Math.Min(1320, area.Width - 40), Math.Min(860, area.Height - 40)));
            if (presenter != null) presenter.IsAlwaysOnTop = true;
            main.Show(false);
            popup.ShowWithoutActivation();
            var popupHwnd = WindowNative.GetWindowHandle(popup);
            var toast = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(popupHwnd));
            var toastPresenter = toast.Presenter as OverlappedPresenter;
            if (toastPresenter != null) toastPresenter.IsAlwaysOnTop = true;
            toast.Move(new PointInt32(area.X + area.Width - toast.Size.Width - 18,
                area.Y + area.Height - toast.Size.Height - 18));
            toast.Show(false);
            action = InvokePopupPrimaryAsync(popup);
            reporter!.Report(new DesktopUpdateProgress(42, "Kontrola pr\u016fhlednosti a \u010ditelnosti",
                "Testuje se podrobn\u00fd pr\u016fb\u011bh a um\u00edst\u011bn\u00ed tla\u010d\u00edtek. \u017d\u00e1dn\u00e1 skute\u010dn\u00e1 aktualizace neb\u011b\u017e\u00ed."));
            await Task.Delay(800);
            string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "SpankyhoOptimizer", "visible-glass-ready.json");
            var payload = new { pid = Environment.ProcessId, version = typeof(MainWindow).Assembly.GetName().Version?.ToString(),
                main = new { hwnd = _windowHandle.ToInt64(), x = main.Position.X, y = main.Position.Y, width = main.Size.Width, height = main.Size.Height },
                popup = new { hwnd = popupHwnd.ToInt64(), x = toast.Position.X, y = toast.Position.Y, width = toast.Size.Width, height = toast.Size.Height },
                utc = DateTime.UtcNow };
            // Unique sidecar + stale reclaim: the diagnostic never leaves a
            // fixed .tmp orphan in ProgramData if the process dies mid-write.
            await AtomicFileService.WriteTextAsync(file, JsonSerializer.Serialize(payload));
            await Task.Delay(14000); // Bounded capture/resource sample; only this diagnostic branch waits.
        }
        finally
        {
            pending.TrySetResult(new DesktopUpdateCompletion("Test dokon\u010den", "Zav\u0159\u00edt"));
            if (action != null) await action;
            popup.Close();
            if (presenter != null) presenter.IsAlwaysOnTop = oldTop;
            main.MoveAndResize(new RectInt32(oldPosition.X, oldPosition.Y, oldSize.Width, oldSize.Height));
        }
    }
}
