using System;

namespace SpankyhoOptimizer.Services;

internal static class UpstreamUpdatePolicy
{
    internal static bool ShouldProbe(bool force, bool enabled, bool hasSnapshot, DateTime? checkedUtc, DateTime now)
    {
        if (force) return true;
        if (!enabled) return false;
        return !hasSnapshot || !checkedUtc.HasValue || now < checkedUtc.Value || now - checkedUtc.Value >= TimeSpan.FromHours(1);
    }

    internal static string IntegrationMode(string id) => id switch
    {
        "winutil" => "native-policy",
        "winhance" => "native-port",
        "dnshop" => "live-catalog",
        "removewindowsai" => "runtime-script",
        "spicetify-marketplace" => "external-installer",
        _ => "reference"
    };

    internal static string IntegrationDetail(string id) => id switch
    {
        "winutil" => "Doporučené služby čtou ověřená data z WinUtilu (4 podporované služby). Nativní oprava chybějících služeb je převzatá v tomto buildu. Další upstream skripty se automaticky nespouštějí.",
        "winhance" => "Funkce jsou portované do C# (WinhancePortCatalog). Nový archiv nemění tuto logiku; její aktualizace vyžaduje nový build Optimizeru.",
        "dnshop" => "DNS seznam skutečně používá DnsResolverCatalogService; aktualizuje se samostatně v DNS benchmarku. Archiv zdrojů není aktivní DNS katalog a nemění nastavení adaptéru.",
        "removewindowsai" => "Používaný skript aktualizuje ZoicwareToolsService před spuštěním zvolené akce (10minutová cache). Tento archiv není potvrzení použité verze skriptu.",
        "spicetify-marketplace" => "Marketplace se skutečně instaluje/aktualizuje přes sekci Spotify / Spicetify. Samotné stažení zdrojů nemění instalaci Spotify.",
        "sophia" or "win11debloat" => "Referenční zdroj; přímé spouštění této kopie není zapojené. Převzaté funkce Optimizeru se mění novým buildem, nikoli stažením archivu.",
        _ => "Bez ověřeného runtime adaptéru; stažení není instalace."
    };

    internal static string ActiveRevision(string id)
    {
        if (id != "winutil") return "Samostatná integrační cesta / nativní build";
        try { return "Data: " + WinUtilServicePolicy.ActiveCommit + " | nativni kod overen k: " + WinUtilServicePolicy.ReviewedBehaviorCommit; }
        catch (Exception ex) { return "Aktivní data neověřena: " + ex.Message; }
    }
}
