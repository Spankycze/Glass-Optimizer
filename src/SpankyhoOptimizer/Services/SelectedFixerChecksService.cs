using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using Windows.Management.Deployment;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Opt-in targeted checks for the four selected Fixer additions.
/// Read-only: does not start/disable Windows services, reinstall Store packages,
/// modify drivers, restart indexing or write any system/registry setting.
/// </summary>
internal static class SelectedFixerChecksService
{
    internal sealed record Result(string Id, string Title, string State,
        bool Attention, string Detail, string Guidance,
        string? ActionUri = null, string? ActionLabel = null);

    // F03/F15: official Windows Repair/Store entry points. The click only
    // opens a trusted OS page; Windows and the user own the subsequent action.
    internal sealed record RepairRoute(string Uri, string Label);
    internal static RepairRoute ResolveRepairRoute(Result item)
    {
        if (!string.IsNullOrEmpty(item.ActionUri) && !string.IsNullOrEmpty(item.ActionLabel))
            return new RepairRoute(item.ActionUri, item.ActionLabel);
        return item.Id switch
        {
            Bluetooth when item.Attention => new("devmgmt.msc", "Otevřít Správce zařízení"),
            Bluetooth => new("ms-settings:bluetooth", "Otevřít nastavení Bluetooth"),
            Search => new("ms-settings:search", "Otevřít nastavení vyhledávání"),
            Xbox or Nvidia => new("ms-settings:appsfeatures", "Otevřít nainstalované aplikace"),
            _ => throw new ArgumentOutOfRangeException(nameof(item))
        };
    }

    internal static bool IsAllowedRepairUri(string value) => value is
        "devmgmt.msc" or "services.msc" or "indexing-options" or
        "ms-settings:bluetooth" or "ms-settings:search" or
        "ms-settings:appsfeatures" or
        "ms-settings:appsfeatures-app?Microsoft.GamingServices_8wekyb3d8bbwe" or
        "ms-settings:appsfeatures-app?NVIDIACorp.NVIDIAControlPanel_56jybvy8sckqj" or
        "ms-windows-store://pdp/?productid=9MWPM2CQNLHN" or
        "ms-windows-store://pdp/?productid=9NF8H0H7WMLT";

    private const string Xbox = "xbox";
    private const string Bluetooth = "bluetooth";
    private const string Nvidia = "nvidia-control-panel";
    private const string Search = "windows-search";

    internal static Task<IReadOnlyList<Result>> ScanAsync(CancellationToken token) =>
        Task.Run<IReadOnlyList<Result>>(() =>
        {
            token.ThrowIfCancellationRequested();
            var result = new List<Result>(4)
            {
                CheckXbox(),
                CheckBluetooth(),
                CheckNvidiaControlPanel(),
                CheckWindowsSearch()
            };
            token.ThrowIfCancellationRequested();
            return result;
        }, token);

    private static Result CheckXbox()
    {
        bool? installed = PackageInstalled("Microsoft.GamingServices_8wekyb3d8bbwe");
        // Xbox/Game Pass is optional. Absent Gaming Services is not a Windows
        // performance problem and must NOT trigger forced installation.
        if (installed == false)
            return new(Xbox, "Xbox / Game Pass", "Volitelné", false,
                "Gaming Services není nainstalovaná.", "Instaluj pouze pokud používáš Xbox aplikaci nebo Game Pass.",
                "ms-windows-store://pdp/?productid=9MWPM2CQNLHN", "Otevřít Gaming Services ve Store");
        string service = ServiceState("GamingServices");
        string serviceNet = ServiceState("GamingServicesNet");
        if ((service == "Disabled" || serviceNet == "Disabled") && installed == true)
            return new(Xbox, "Xbox / Game Pass", "Prověřit", true,
                "Gaming Services je nainstalovaná, ale služba je zakázaná: GamingServices=" + service +
                    ", GamingServicesNet=" + serviceNet + ".",
                "V Aplikacích Windows otevři Gaming Services → Rozšířené možnosti → Opravit. Nepoužívej reset dat bez zálohy.",
                "ms-settings:appsfeatures-app?Microsoft.GamingServices_8wekyb3d8bbwe", "Otevřít opravu Gaming Services");
        if (installed == null)
            return new(Xbox, "Xbox / Game Pass", "Nezjištěno", false,
                "Stav Gaming Services nelze přečíst.", "Ověř stav v Aplikace → Nainstalované aplikace.");
        return new(Xbox, "Xbox / Game Pass", "Přítomno", false,
            "Gaming Services je nainstalovaná. Služby: GamingServices=" + service +
                ", GamingServicesNet=" + serviceNet + ".",
            "Není nutný žádný zásah, pokud hry z Game Pass fungují.",
            "ms-settings:appsfeatures-app?Microsoft.GamingServices_8wekyb3d8bbwe", "Otevřít možnosti Gaming Services");
    }

    private static Result CheckBluetooth()
    {
        string support = ServiceState("bthserv");
        var hardware = BluetoothAdapterHealthService.Read();
        if (hardware.Available)
        {
            var affected = hardware.Adapters
                .Where(x => BluetoothAdapterHealthService.IsActionableDriverError(x.ErrorCode))
                .ToArray();
            if (affected.Length > 0)
            {
                string examples = string.Join(", ", affected.Take(2)
                    .Select(x => x.Name + " (Code " + x.ErrorCode + ")"));
                return new(Bluetooth, "Bluetooth", "Ovladač – problém", true,
                    "Windows hlásí chybu fyzického Bluetooth adaptéru: " + examples + ". " +
                    "Bluetooth Support Service: " + support + ".",
                    "Otevři Správce zařízení, ověř kód chyby a stav ovladače. Nic automaticky neresetuji.");
            }
            if (hardware.Adapters.Count == 0)
                return new(Bluetooth, "Bluetooth", "Volitelné", false,
                    "Fyzický USB/PCI Bluetooth adaptér nebyl nalezen. Služba: " + support + ".",
                    "Pokud počítač Bluetooth nemá, není potřeba nic zapínat.");
        }
        // Disabled is often intentional on a computer without Bluetooth hardware.
        return support switch
        {
            "Disabled" => new(Bluetooth, "Bluetooth", "Volitelné", false,
                "Bluetooth Support Service je vypnutá.", "Pokud Bluetooth používáš, otevři Nastavení → Bluetooth a zařízení a prověř ovladač."),
            "Missing" => new(Bluetooth, "Bluetooth", "Nedostupné", false,
                "Systém nemá dostupnou službu Bluetooth Support.", "Ověř, zda má počítač Bluetooth adaptér."),
            "Unknown" => new(Bluetooth, "Bluetooth", "Nezjištěno", false,
                "Službu Bluetooth nelze přečíst.", "Ověř Bluetooth v Nastavení Windows."),
            _ => new(Bluetooth, "Bluetooth", "Připraveno", false,
                "Bluetooth Support Service: " + support,
                "Pokud zařízení nefunguje, ověř párování a stav ovladače v Nastavení Windows.")
        };
    }

    private static Result CheckNvidiaControlPanel()
    {
        bool? present = PackageInstalled("NVIDIACorp.NVIDIAControlPanel_56jybvy8sckqj");
        string classic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NVIDIA Corporation", "Control Panel Client", "nvcplui.exe");
        bool legacy = File.Exists(classic);
        string service = ServiceState("NVDisplay.ContainerLocalSystem");
        if ((present == true || legacy) && service == "Disabled")
            return new(Nvidia, "NVIDIA Control Panel", "Prověřit službu", true,
                "NVIDIA Control Panel je přítomný, ale NVIDIA Display Container je zakázaný.",
                "Pokud se Control Panel neotevírá, prověř stav NVIDIA Display Container v Službách Windows. Automaticky ji nezapínám.",
                present == true ? "ms-settings:appsfeatures-app?NVIDIACorp.NVIDIAControlPanel_56jybvy8sckqj" : "ms-settings:appsfeatures",
                "Otevřít nastavení NVIDIA Control Panel");
        if (present == true || legacy)
            return new(Nvidia, "NVIDIA Control Panel", "Přítomno", false,
                (present == true ? "Aplikace NVIDIA Control Panel je nainstalovaná." :
                 "Klasická NVIDIA Control Panel je přítomná.") +
                " NVIDIA Display Container: " + service,
                "Pokud nejde otevřít, ověř aplikaci v Nainstalované aplikace. Není potřeba měnit GPU výkonové režimy.",
                present == true ? "ms-settings:appsfeatures-app?NVIDIACorp.NVIDIAControlPanel_56jybvy8sckqj" : "ms-settings:appsfeatures",
                "Otevřít možnosti NVIDIA Control Panel");
        if (present == false)
            return new(Nvidia, "NVIDIA Control Panel", "Nezjištěno", false,
                "Ovládací panel NVIDIA nebyl nalezen. Na systému bez NVIDIA GPU není potřeba.",
                "Pokud používáš NVIDIA GPU, zkontroluj instalaci aplikace v Microsoft Store a stav NVIDIA Display Container.",
                "ms-windows-store://pdp/?productid=9NF8H0H7WMLT", "Otevřít NVIDIA Control Panel ve Store");
        return new(Nvidia, "NVIDIA Control Panel", "Nezjištěno", false,
            "Windows nedovolil ověřit dostupnost ovládacího panelu NVIDIA.",
            "Zkontroluj jej v Nainstalovaných aplikacích.");
    }

    private static Result CheckWindowsSearch() =>
        CheckWindowsSearchState(ServiceState("WSearch"));

    // Pure mapping for service-state-specific user-initiated repair paths.
    internal static Result CheckWindowsSearchState(string service)
    {
        if (service == "Stopped")
            return new(Search, "Windows Search", "Zastaveno", false,
                "Indexovací služba WSearch je zastavená; může jít o úsporné nebo ruční nastavení.",
                "Pokud vyhledávání nefunguje, zkontroluj službu WSearch v Službách Windows. Službu automaticky nespouštím.",
                "services.msc", "Otevřít službu Windows Search");
        return service switch
        {
            "Disabled" => new(Search, "Windows Search", "Prověřit", true,
                "Indexovací služba WSearch je zakázaná.",
                "Používáš-li hledání souborů, otevři Služby Windows a prověř WSearch; automaticky službu nezapínám.",
                "services.msc", "Otevřít službu Windows Search"),
            "Missing" => new(Search, "Windows Search", "Nedostupné", false,
                "Službu WSearch Windows nehlásí.", "Ověř volitelné součásti a službu ve Windows."),
            "Unknown" => new(Search, "Windows Search", "Nezjištěno", false,
                "Stav WSearch nelze přečíst.", "Ověř Vyhledávání ve Windows."),
            _ => new(Search, "Windows Search", "Připraveno", false,
                "WSearch: " + service,
                "Pokud vyhledávání nefunguje, otevři Možnosti indexování. Obnovení indexu spouští pouze uživatel a může dočasně zvýšit CPU/I/O.",
                "indexing-options", "Otevřít možnosti indexování")
        };
    }

    private static bool? PackageInstalled(string familyName)
    {
        try
        {
            var packages = new PackageManager().FindPackagesForUser(string.Empty, familyName);
            return packages.Any();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or
            System.Runtime.InteropServices.COMException or InvalidOperationException or
            System.Security.SecurityException)
        { return null; }
    }

    private static string ServiceState(string name)
    {
        try
        {
            using var svc = new ServiceController(name);
            return svc.StartType == ServiceStartMode.Disabled
                ? "Disabled" : svc.Status.ToString();
        }
        catch (InvalidOperationException) { return "Missing"; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or System.Security.SecurityException)
        { return "Unknown"; }
    }

    internal static string RunPolicySmoke()
    {
        string[] ids = [Xbox, Bluetooth, Nvidia, Search];
        if (ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 4 ||
            !ids.Contains(Xbox) || !ids.Contains(Search))
            throw new InvalidOperationException("Selected fixer checks IDs are not unique.");
        // Tests must not touch the real local service manager/package deployment
        // or start a repair; no claims about actual PC service state here.
        var examples = new[]
        {
            new Result(Xbox, "Xbox", "Volitelné", false, "Missing optional", "Install when needed"),
            new Result(Bluetooth, "Bluetooth", "Volitelné", false, "Disabled intentionally", ""),
            new Result(Nvidia, "NVCP", "Nezjištěno", false, "Hardware not confirmed", ""),
            new Result(Search, "Search", "Prověřit", true, "Disabled service", "")
        };
        if (examples.Count(x => x.Attention) != 1 || examples.Count(x => !x.Attention) != 3)
            throw new InvalidOperationException("Optional checks misclassified as mandatory repairs.");
        var xboxStore = ResolveRepairRoute(examples[0] with
        {
            ActionUri = "ms-windows-store://pdp/?productid=9MWPM2CQNLHN",
            ActionLabel = "Otevřít Gaming Services ve Store"
        });
        var nvidiaRepair = ResolveRepairRoute(examples[2] with
        {
            ActionUri = "ms-settings:appsfeatures-app?NVIDIACorp.NVIDIAControlPanel_56jybvy8sckqj",
            ActionLabel = "Otevřít opravu NVIDIA Control Panel"
        });
        var bluetoothDriver = ResolveRepairRoute(examples[1] with { Attention = true });
        var search = ResolveRepairRoute(examples[3]);
        var searchDisabled = ResolveRepairRoute(CheckWindowsSearchState("Disabled"));
        var searchStopped = ResolveRepairRoute(CheckWindowsSearchState("Stopped"));
        var searchRunning = ResolveRepairRoute(CheckWindowsSearchState("Running"));
        var searchUnknown = ResolveRepairRoute(CheckWindowsSearchState("Unknown"));
        if (!IsAllowedRepairUri(xboxStore.Uri) || !IsAllowedRepairUri(nvidiaRepair.Uri) ||
            bluetoothDriver.Uri != "devmgmt.msc" || search.Uri != "ms-settings:search" ||
            searchDisabled.Uri != "services.msc" || searchStopped.Uri != "services.msc" ||
            searchRunning.Uri != "indexing-options" || searchUnknown.Uri != "ms-settings:search" ||
            !IsAllowedRepairUri(searchDisabled.Uri) || !IsAllowedRepairUri(searchRunning.Uri) ||
            IsAllowedRepairUri("https://example.invalid/") || IsAllowedRepairUri("powershell.exe"))
            throw new InvalidOperationException("Official repair routing policy regressed.");
        return "SELECTED_FIXER_CHECKS_POLICY_SMOKE_OK checks=4 noAutofix=true optionalPackagesNotFault=true officialRepairLinks=true noServiceWrites=true";
    }
}
