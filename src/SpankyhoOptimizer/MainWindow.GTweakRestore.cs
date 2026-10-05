using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private void AddGTweakRestoreActions(StackPanel actions)
    {
        actions.Children.Add(QuickButton("Export registry (.reg)", RunGTweakRegistryExportAsync));
        actions.Children.Add(QuickButton("Export Optimizer config", RunOptimizerConfigExportAsync));
        actions.Children.Add(QuickButton("Import Optimizer config", RunOptimizerConfigImportAsync));
    }

    private async Task RunGTweakRegistryExportAsync()
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedFileName = "Spankyho-Registry-Backup-" + DateTime.Now.ToString("yyyyMMdd-HHmm")
        };
        picker.FileTypeChoices.Add("Registry backup", new List<string> { ".reg" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file == null) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        try
        {
            await GTweakRegistryExportService.ExportAsync(file.Path, cts.Token);
            await ShowMessageAsync("Registry backup", "Registry export byl dokončen:\n" + file.Path);
        }
        catch (Exception ex)
        {
            LogService.Error("restore", "Advanced registry export failed", ex);
            await ShowMessageAsync("Registry backup", "Export selhal: " + ex.Message);
        }
    }
    private async Task RunOptimizerConfigExportAsync()
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = "Spankyho-Optimizer-Config-" + DateTime.Now.ToString("yyyyMMdd-HHmm") };
        picker.FileTypeChoices.Add("Spankyho config", new List<string> { ".spankyconfig" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file == null) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        try { await OptimizerConfigService.ExportAsync(file.Path, cts.Token); await ShowMessageAsync("Optimizer config", "Konfigurace byla exportována:\n" + file.Path); }
        catch (Exception ex) { LogService.Error("restore", "Config export failed", ex); await ShowMessageAsync("Optimizer config", "Export selhal: " + ex.Message); }
    }

    private async Task RunOptimizerConfigImportAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".spankyconfig");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file == null) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        try
        {
            var errors = await OptimizerConfigService.ImportAndApplyAsync(file.Path, cts.Token);
            await ShowMessageAsync("Optimizer config", errors.Count == 0 ? "Konfigurace byla aplikována a ověřena." : "Import dokončen s chybami:\n" + string.Join("\n", errors));
        }
        catch (Exception ex) { LogService.Error("restore", "Config import failed", ex); await ShowMessageAsync("Optimizer config", "Import selhal: " + ex.Message); }
    }
}
