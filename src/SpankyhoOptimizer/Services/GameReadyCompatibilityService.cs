using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// G09 GameReadyX: user-requested, on-demand compatibility preflight.
/// Checks optional legacy game dependencies without downloading, installing,
/// changing GPU/Windows settings, measuring FPS or scanning game libraries.
/// </summary>
internal static class GameReadyCompatibilityService
{
    internal sealed record Finding(string Name, string State, string Detail,
        bool NeedsAttention);
    internal sealed record Report(IReadOnlyList<Finding> Findings);
    internal sealed record ManualRepair(string Title, string Url);
    private const string MicrosoftVcGuide = "https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist/";
    private const string MicrosoftDirectXGuide = "https://www.microsoft.com/en-us/download/details.aspx?id=8109";

    // Return actionable official instructions only when there is positive
    // evidence of a missing prerequisite. Unknown and default settings must
    // never trigger a download suggestion.
    internal static IReadOnlyList<ManualRepair> ManualRepairOptions(Report report)
    {
        var resources = new List<ManualRepair>(2);
        foreach (var finding in report.Findings)
        {
            if (finding.Name == "Microsoft Visual C++" && finding.NeedsAttention)
                resources.Add(new("Microsoft Visual C++ x86/x64", MicrosoftVcGuide));
            if (finding.Name == "Starší DirectX knihovny" &&
                finding.State == "Volitelné / neúplné")
                resources.Add(new("Starší DirectX knihovny (volitelné)", MicrosoftDirectXGuide));
        }
        return resources;
    }

    internal static bool IsApprovedManualRepairUrl(string url) =>
        url is MicrosoftVcGuide or MicrosoftDirectXGuide;

    internal static Task<Report> ScanAsync(CancellationToken token) =>
        Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var list = new List<Finding>(4)
            {
                CheckVcruntimes(),
                CheckLegacyDirectX(),
                CheckPagefile(),
                CheckMpoOverride()
            };
            token.ThrowIfCancellationRequested();
            return new Report(list);
        }, token);

    private static Finding CheckVcruntimes()
    {
        (bool? x64, string v64) = GetVcruntime(RegistryView.Registry64, "x64");
        (bool? x86, string v86) = GetVcruntime(RegistryView.Registry32, "x86");
        bool anyMissing = x64 == false || x86 == false;
        bool both = x64 == true && x86 == true;
        string state = both ? "Nalezeno" : anyMissing ? "Prověřit při chybě hry" : "Nezjištěno";
        string detail = "VC++ v14 x64: " + v64 +
                        " • x86: " + v86 + ". " +
                        "Chybějící architektura vadí jen aplikacím, které ji vyžadují. " +
                        "Oficiální aktuální balíčky: https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist/";
        return new Finding("Microsoft Visual C++", state,
            detail, anyMissing);
    }

    private static (bool? Installed, string Version) GetVcruntime(RegistryView view, string arch)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\" + arch);
            bool installed = Convert.ToInt32(key?.GetValue("Installed") ?? 0) == 1;
            string version = key?.GetValue("Version")?.ToString() ?? "verze nezjištěna";
            return (installed, installed ? version : "nenalezeno");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException
            or IOException or ArgumentException or InvalidCastException)
        { return (null, "nezjištěno"); }
    }

    private static Finding CheckLegacyDirectX()
    {
        // A Windows 11 DirectX 12 installation does NOT imply the optional
        // June 2010 D3DX9/XInput 1.3 side-by-side libraries are present.
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string[] files = ["d3dx9_43.dll", "xinput1_3.dll"];
        string[] paths = ["System32", "SysWOW64"];
        int found = 0;
        foreach (string folder in paths)
            foreach (string file in files)
                if (File.Exists(Path.Combine(win, folder, file))) found++;
        return new Finding("Starší DirectX knihovny",
            found == 4 ? "Nalezeno" : "Volitelné / neúplné",
            "D3DX9/XInput 1.3: " + found + "/4 souborů (64/32 bit). " +
            "Moderním hrám často nejsou potřeba. Některé starší hry potřebují DirectX End-User Runtimes (June 2010): " +
            "https://www.microsoft.com/en-us/download/details.aspx?id=8109",
            NeedsAttention: false);
    }

    private static Finding CheckPagefile()
    {
        // Unlike PagingFiles registry entries, these WMI classes separately
        // expose the active pagefile and whether Windows manages its size.
        bool? auto = null;
        int entries = 0;
        uint totalSize = 0, totalUsed = 0;
        string drive = "";
        try
        {
            var scope = new System.Management.ManagementScope(@"\\.\root\CIMV2");
            scope.Options.Timeout = TimeSpan.FromSeconds(3);
            scope.Connect();
            using (var search = new System.Management.ManagementObjectSearcher(scope,
                new System.Management.ObjectQuery("SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem"),
                new System.Management.EnumerationOptions
                {
                    Timeout = TimeSpan.FromSeconds(3),
                    ReturnImmediately = false, Rewindable = false
                }))
            using (var rows = search.Get())
            {
                foreach (System.Management.ManagementObject item in rows)
                {
                    using (item) { auto = item["AutomaticManagedPagefile"] as bool?; }
                    break;
                }
            }
            using (var search = new System.Management.ManagementObjectSearcher(scope,
                new System.Management.ObjectQuery("SELECT Name,AllocatedBaseSize,CurrentUsage FROM Win32_PageFileUsage"),
                new System.Management.EnumerationOptions
                {
                    Timeout = TimeSpan.FromSeconds(3),
                    ReturnImmediately = false, Rewindable = false
                }))
            using (var rows = search.Get())
                foreach (System.Management.ManagementObject item in rows)
                {
                    using (item)
                    {
                        entries++;
                        totalSize += Convert.ToUInt32(item["AllocatedBaseSize"] ?? 0);
                        totalUsed += Convert.ToUInt32(item["CurrentUsage"] ?? 0);
                        string name = Convert.ToString(item["Name"]) ?? "";
                        if (entries == 1 && name.Length >= 2 && name[1] == ':')
                            drive = name[..2].ToUpperInvariant();
                    }
                }
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or
            System.Runtime.InteropServices.COMException or UnauthorizedAccessException or
            System.Security.SecurityException or InvalidOperationException)
        {
            return new Finding("Stránkovací soubor", "Nezjištěno",
                "Nepodařilo se přečíst aktivní stránkovací soubor; není důvod automaticky měnit Windows.", false);
        }
        string mode = auto switch
        {
            true => "Windows spravuje velikost",
            false => "Vlastní správa velikosti",
            _ => "Nezjištěno"
        };
        string state = entries > 0 ? "Aktivní" : "Nevykázaný";
        string use = entries > 0
            ? "Aktivní soubor: " + entries + (drive.Length > 0 ? " • " + drive : "") +
              " • alokováno " + totalSize + " MiB • použito " + totalUsed + " MiB."
            : "WMI nevrátilo aktivní stránkovací soubor; neznamená to samo o sobě systémovou chybu.";
        return new Finding("Stránkovací soubor", state,
            mode + ". " + use + " Neměň velikost automaticky; vypnutí může snížit dostupný commit limit.",
            NeedsAttention: false);
    }

    private static Finding CheckMpoOverride()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\Dwm");
            object? value = key?.GetValue("OverlayTestMode");
            return value switch
            {
                null => new("MPO / Windows grafika", "Windows Default",
                    "Žádné explicitní OverlayTestMode přepsání v systémovém registru. To neprokazuje, že MPO skutečně běží.", false),
                int n => new("MPO / Windows grafika", "Vlastní nastavení",
                    "OverlayTestMode=" + n + ". Neprovádím změnu; vypnutí MPO bývá pouze cílený workaround pro doložený problém.", false),
                _ => new("MPO / Windows grafika", "Nezjištěno",
                    "Typ hodnoty OverlayTestMode není očekávaný; automatická oprava není bezpečná.", false)
            };
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or
            System.Security.SecurityException or IOException)
        { return new("MPO / Windows grafika", "Nezjištěno", "Nelze přečíst nastavení MPO.", false); }
    }

    internal static string Format(Report report)
    {
        return "Kontrola připravenosti pro kompatibilitu her — bez měření FPS a bez systémových změn.\n\n" +
            string.Join("\n\n", report.Findings.Select(f =>
                f.Name + " — " + f.State + "\n" + f.Detail)) +
            "\n\nChybějící volitelné komponenty nejsou automaticky chyba Windows. Instaluj je jen pokud konkrétní hra vyžaduje daný runtime.";
    }

    internal static string RunPolicySmoke()
    {
        var report = new Report([
            new Finding("Microsoft Visual C++", "Prověřit při chybě hry", "x86 chybí", true),
            new Finding("Starší DirectX knihovny", "Volitelné / neúplné", "2/4", false),
            new Finding("Stránkovací soubor", "Nezjištěno", "Unknown", false),
            new Finding("MPO / Windows grafika", "Windows Default", "No registry override", false)
        ]);
        string text = Format(report);
        if (report.Findings.Count != 4 || report.Findings.Count(x => x.NeedsAttention) != 1 ||
            !text.Contains("bez měření FPS", StringComparison.Ordinal) ||
            !text.Contains("nejsou automaticky chyba", StringComparison.Ordinal) ||
            !text.Contains("Windows Default", StringComparison.Ordinal))
            throw new InvalidOperationException("GameReadyX runtime-check classification regressed.");
        var repairs = ManualRepairOptions(report);
        var healthy = new Report([
            new Finding("Microsoft Visual C++", "Nalezeno", "x86 a x64 nainstalované", false),
            new Finding("Starší DirectX knihovny", "Nalezeno", "4/4", false),
            new Finding("Stránkovací soubor", "Aktivní", "Automaticky spravován", false),
            new Finding("MPO / Windows grafika", "Windows Default", "Bez přepsání", false)
        ]);
        var unknown = new Report([
            new Finding("Microsoft Visual C++", "Nezjištěno", "Registr nelze přečíst", false),
            new Finding("Starší DirectX knihovny", "Nezjištěno", "Bez přístupu", false)
        ]);
        if (repairs.Count != 2 ||
            repairs[0].Url != MicrosoftVcGuide || repairs[1].Url != MicrosoftDirectXGuide ||
            !IsApprovedManualRepairUrl(repairs[0].Url) ||
            !IsApprovedManualRepairUrl(repairs[1].Url) ||
            IsApprovedManualRepairUrl("file:///C:/setup.exe") ||
            IsApprovedManualRepairUrl("https://example.invalid/download.exe") ||
            ManualRepairOptions(healthy).Count != 0 ||
            ManualRepairOptions(unknown).Count != 0)
            throw new InvalidOperationException("GameReadyX official manual repair routing regressed.");
        return "GAME_READY_COMPAT_POLICY_SMOKE_OK runtime=vc,legacydx,pagefile,mpo noAutoInstall=true noFpsOrGameLauncher=true noSystemWrites=true";
    }
}
