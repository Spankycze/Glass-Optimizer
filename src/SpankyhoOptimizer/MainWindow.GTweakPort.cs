using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private StackPanel BuildGTweakStorageActions()
    {
        var root = new StackPanel { Spacing = 8 };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(QuickButton("NTFS komprese složky", async () => await RunNtfsCompressionAsync(true)));
        row.Children.Add(QuickButton("NTFS dekomprese složky", async () => await RunNtfsCompressionAsync(false)));
        row.Children.Add(QuickButton("Windows.old cleanup", RunWindowsOldCleanupAsync));
        root.Children.Add(row);

        var row2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row2.Children.Add(QuickButton("Vyčistit Temp / shader cache", RunSafeCacheCleanupAsync));
        row2.Children.Add(QuickButton("Vyčistit Event Viewer logy", RunEventLogCleanupAsync));
        row2.Children.Add(QuickButton("Obnovit icon cache", RunIconCacheCleanupAsync));
        row2.Children.Add(QuickButton("Obnovit thumbnail cache", RunThumbnailCacheCleanupAsync));
        root.Children.Add(row2);
        return root;
    }

    private async Task RunNtfsCompressionAsync(bool compress)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;
        if (!GTweakMaintenanceService.IsNtfs(folder.Path))
        {
            await ShowMessageAsync("NTFS", "Vybraná složka není na NTFS svazku.");
            return;
        }
        string action = compress ? "komprimovat" : "dekomprimovat";
        if (!await ShowStyledConfirmAsync("NTFS " + action + "?",
            $"Operace projde vybranou složku rekurzivně:\n{folder.Path}\n\nMůže trvat delší dobu a lze ji zrušit zavřením/progress operací.",
            compress ? "Komprimovat" : "Dekomprimovat", "Zrušit", "Úložiště / NVMe")) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        try
        {
            await GTweakMaintenanceService.SetNtfsCompressionAsync(folder.Path, compress, cts.Token);
            await ShowMessageAsync("NTFS", compress ? "Komprese byla dokončena." : "Dekomprese byla dokončena.");
        }
        catch (Exception ex)
        {
            LogService.Error("storage", "NTFS compression action failed", ex);
            await ShowMessageAsync("NTFS", "Operace selhala: " + ex.Message);
        }
    }

    private async Task RunWindowsOldCleanupAsync()
    {
        if (!GTweakMaintenanceService.HasWindowsOld)
        {
            await ShowMessageAsync("Windows.old", "Složka Windows.old nebyla nalezena.");
            return;
        }
        if (!await ShowStyledConfirmAsync("Odstranit Windows.old?",
            "Tím odstraníš soubory předchozí instalace Windows a můžeš přijít o možnost návratu na předchozí build. Operace je nevratná.",
            "Odstranit", "Zrušit", "Úložiště / NVMe")) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(35));
        try
        {
            await GTweakMaintenanceService.RemoveWindowsOldAsync(cts.Token);
            await ShowMessageAsync("Windows.old", "Windows.old byl odstraněn.");
        }
        catch (Exception ex)
        {
            LogService.Error("storage", "Windows.old cleanup failed", ex);
            await ShowMessageAsync("Windows.old", "Cleanup selhal: " + ex.Message);
        }
    }

    private async Task RunIconCacheCleanupAsync()
    {
        if (!await ShowStyledConfirmAsync("Obnovit Windows icon cache?",
            "Smaže iconcache soubory a restartuje Explorer. Otevřená okna Exploreru se zavřou.",
            "Obnovit", "Zrušit", "UI / Explorer")) return;
        try
        {
            await GTweakMaintenanceService.ClearIconCacheAsync(_loadCts?.Token ?? _windowLifetimeCts.Token);
            await ShowMessageAsync("Icon cache", "Icon cache byla vyčištěna a Explorer restartován.");
        }
        catch (Exception ex)
        {
            LogService.Error("ui", "Icon cache cleanup failed", ex);
            await ShowMessageAsync("Icon cache", "Obnova selhala: " + ex.Message);
        }
    }
    private async Task RunThumbnailCacheCleanupAsync()
    {
        if (!await ShowStyledConfirmAsync("Obnovit Windows thumbnail cache?",
            "Smaže thumbcache databáze a restartuje Explorer. Náhledy obrázků a videí se znovu vytvoří.",
            "Obnovit", "Zrušit", "UI / Explorer")) return;
        try
        {
            await GTweakMaintenanceService.ClearThumbnailCacheAsync(_loadCts?.Token ?? _windowLifetimeCts.Token);
            await ShowMessageAsync("Thumbnail cache", "Thumbnail cache byla vyčištěna a Explorer restartován.");
        }
        catch (Exception ex)
        {
            LogService.Error("ui", "Thumbnail cache cleanup failed", ex);
            await ShowMessageAsync("Thumbnail cache", "Obnova selhala: " + ex.Message);
        }
    }
    private async Task RunSafeCacheCleanupAsync()
    {
        long bytes = await StorageMaintenanceService.GetSafeCleanupSizeAsync();
        if (!await ShowStyledConfirmAsync("Vyčistit dočasné cache?", $"Bezpečné Temp/shader cache mají přibližně {bytes / 1048576d:0.0} MB.", "Vyčistit", "Zrušit", "Úložiště / NVMe")) return;
        var result = await StorageMaintenanceService.ClearSafeCachesAsync();
        await ShowMessageAsync("Cleanup", $"Uvolněno {result.FreedBytes / 1048576d:0.0} MB. Selhané soubory: {result.FailedFiles}.");
    }

    private async Task RunEventLogCleanupAsync()
    {
        if (!await ShowStyledConfirmAsync("Vyčistit Event Viewer logy?",
            "Smaže záznamy ze všech dostupných Windows Event Viewer logů. Tato operace odstraní historické záznamy a nelze ji vrátit.",
            "Vyčistit", "Zrušit", "Úložiště / NVMe")) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var result = await GTweakMaintenanceService.ClearEventLogsAsync(cts.Token);
            await ShowMessageAsync("Event Viewer", $"Zpracováno {result.Cleared} z {result.Discovered} logů. Selhalo: {result.Failed}.");
        }
        catch (Exception ex)
        {
            LogService.Error("storage", "Event Viewer cleanup failed", ex);
            await ShowMessageAsync("Event Viewer", "Čištění selhalo: " + ex.Message);
        }
    }
}
