using System;
using System.Threading;
using System.Threading.Tasks;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private async Task LoadShellToolkitStatusAsync(CancellationToken token, int generation)
    {
        try
        {
            var cmd = await ShellToolkitService.GetCmdStatusAsync(token);

            if (token.IsCancellationRequested ||
                generation != _psCenterLoadGeneration ||
                _section != "powershell")
                return;

            if (_cmdOptimizerStatus != null)
            {
                string clink = cmd.ClinkInstalled
                    ? (string.IsNullOrWhiteSpace(cmd.ClinkVersion) ? "Installed" : cmd.ClinkVersion)
                    : "Missing";
                bool ready = cmd.ClinkInstalled && cmd.ClinkAutorun && cmd.ForceV2 && cmd.VirtualTerminal;
                _cmdOptimizerStatus.Text =
                    $"CMD {(ready ? "PŘIPRAVENO" : "VYŽADUJE ÚPRAVU")} • " +
                    $"Clink {(cmd.ClinkInstalled ? "AKTIVNÍ" : "CHYBÍ")} • " +
                    $"AutoRun {(cmd.ClinkAutorun ? "ON" : "OFF")} • VT {(cmd.VirtualTerminal ? "ON" : "OFF")}";
                _cmdOptimizerStatus.Foreground = ready ? UiBrush("SpankySuccessBrush") : UiBrush("SpankyWarningBrush");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            LogService.Info("powershell", "Shell Toolkit status cancelled by navigation.");
        }
        catch (Exception ex)
        {
            LogService.Error("powershell", "Shell Toolkit status failed", ex);
            if (_cmdOptimizerStatus != null && !token.IsCancellationRequested)
                _cmdOptimizerStatus.Text = "CMD stav selhal: " + ex.Message;
        }
    }
}
