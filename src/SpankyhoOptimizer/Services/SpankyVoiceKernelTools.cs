using System;
using System.ComponentModel;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SemanticKernel;

namespace SpankyhoOptimizer.Services;

public sealed class SpankyVoiceKernelTools
{
    [KernelFunction("get_time")]
    [Description("Vrátí aktuální lokální čas počítače.")]
    public string GetTime() => DateTime.Now.ToString("HH:mm");

    [KernelFunction("get_date")]
    [Description("Vrátí aktuální lokální datum počítače.")]
    public string GetDate() => DateTime.Now.ToString("dddd d. MMMM yyyy");

    [KernelFunction("get_ram_usage")]
    [Description("Vrátí aktuální živé využití fyzické RAM. Hodnota není cachovaná.")]
    public string GetRamUsage() => SystemInfoService.GetLiveRamSummary();

    [KernelFunction("get_disk_usage")]
    [Description("Vrátí aktuální využití systémového disku a volné místo.")]
    public string GetDiskUsage() => SystemInfoService.GetLiveDiskSummary();

    [KernelFunction("get_optimizer_version")]
    [Description("Vrátí aktuální verzi Spankyho Optimizeru.")]
    public string GetOptimizerVersion() =>
        typeof(SpankyVoiceKernelTools).Assembly.GetName().Version?.ToString(3) ?? "neznámá";

    [KernelFunction("get_windows_version")]
    [Description("Vrátí Windows verzi z existující systémové telemetrie.")]
    public async Task<string> GetWindowsVersionAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        OverviewSnapshot snapshot = SystemInfoService.TryGetFreshSnapshot()
            ?? await SystemInfoService.GetSnapshotAsync().ConfigureAwait(false);
        return snapshot.Windows;
    }

    [KernelFunction("get_active_power_plan")]
    [Description("Vrátí aktuální aktivní Windows power plan.")]
    public async Task<string> GetActivePowerPlanAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        OverviewSnapshot snapshot = SystemInfoService.TryGetFreshSnapshot()
            ?? await SystemInfoService.GetSnapshotAsync().ConfigureAwait(false);
        return snapshot.PowerPlan;
    }

    [KernelFunction("get_firefox_status")]
    [Description("Zjistí, zda Firefox právě běží.")]
    public string GetFirefoxStatus() =>
        SpankyVoiceToolService.IsAnyProcessRunning("firefox")
            ? "Firefox běží."
            : "Firefox neběží.";

    [KernelFunction("get_dns_status")]
    [Description("Vrátí DNS servery aktivních síťových rozhraní.")]
    public string GetDnsStatus()
    {
        string[] dns = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n =>
            {
                try { return n.GetIPProperties().DnsAddresses.AsEnumerable(); }
                catch { return Enumerable.Empty<System.Net.IPAddress>(); }
            })
            .Select(x => x.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray();

        return dns.Length == 0
            ? "DNS servery se nepodařilo zjistit."
            : "Aktivní DNS: " + string.Join(", ", dns) + ".";
    }

    [KernelFunction("get_system_overview")]
    [Description("Vrátí Windows, CPU, GPU, RAM, disk, síť a aktivní power plan.")]
    public async Task<string> GetSystemOverviewAsync(CancellationToken token = default)
    {        token.ThrowIfCancellationRequested();
        OverviewSnapshot snapshot = SystemInfoService.TryGetFreshSnapshot()
            ?? await SystemInfoService.GetSnapshotAsync().ConfigureAwait(false);
        return $"Windows={snapshot.Windows}; CPU={snapshot.Cpu}; GPU={snapshot.Gpu}; RAM={snapshot.Ram}; Disk={snapshot.Disk}; Network={snapshot.Network}; Power={snapshot.PowerPlan}";
    }

    [KernelFunction("get_memory_hogs")]
    [Description("Vrátí procesy, které aktuálně používají nejvíc RAM.")]
    public async Task<string> GetMemoryHogsAsync(CancellationToken token = default)
    {
        string? result = await SpankyVoiceToolService.TryHandleAsync("co mi zere ram", token).ConfigureAwait(false);
        return result ?? "Využití RAM se nepodařilo načíst.";
    }

    [KernelFunction("get_weather")]
    [Description("Vrátí aktuální počasí pro přibližnou lokaci zařízení.")]
    public async Task<string> GetWeatherAsync(CancellationToken token = default)
    {
        string? result = await SpankyVoiceToolService.TryHandleAsync("pocasi", token).ConfigureAwait(false);
        return result ?? "Počasí se nepodařilo načíst.";
    }
    [KernelFunction("get_weather_forecast")]
    [Description("Vrátí předpověď počasí až 6 dní dopředu. location může být prázdné pro přibližnou polohu zařízení; dayOffset 0=dnes, 1=zítra.")]
    public async Task<string> GetWeatherForecastAsync(
        [Description("Město nebo místo, například Praha; prázdné = přibližná poloha zařízení")] string? location = null,
        [Description("0=dnes, 1=zítra, až 6")] int dayOffset = 0,
        CancellationToken token = default)
    {
        return await SpankyVoiceToolService.GetWeatherForecastAsync(location, dayOffset, token).ConfigureAwait(false);
    }

    [KernelFunction("get_cpu_telemetry")]
    [Description("Vrátí aktuální teplotu CPU, vytížení, efektivní takt a příkon.")]
    public string GetCpuTelemetry() => SpankyVoiceToolService.BuildCpuTelemetryReply();

    [KernelFunction("get_gpu_telemetry")]
    [Description("Vrátí aktuální vytížení GPU.")]
    public string GetGpuTelemetry() => SpankyVoiceToolService.BuildGpuTelemetryReply();

    [KernelFunction("get_top_cpu_processes")]
    [Description("Vrátí procesy, které aktuálně používají nejvíc CPU.")]
    public Task<string> GetTopCpuProcessesAsync(CancellationToken token = default) =>
        SpankyVoiceToolService.BuildTopCpuReplyAsync(token);

    [KernelFunction("get_network_latency")]
    [Description("Změří aktuální síťovou odezvu pomocí krátkého ping testu.")]
    public Task<string> GetNetworkLatencyAsync(CancellationToken token = default) =>
        SpankyVoiceToolService.BuildLatencyReplyAsync(token);

    [KernelFunction("diagnose_firefox")]
    [Description("Provede rychlou diagnostiku Firefoxu podle skutečného CPU, RAM procesů a síťové odezvy.")]
    public Task<string> DiagnoseFirefoxAsync(CancellationToken token = default) =>
        SpankyVoiceToolService.BuildFirefoxDiagnosticAsync(token);

    [KernelFunction("get_system_health")]
    [Description("Zkontroluje živé CPU, GPU, teploty, RAM a ping a vrátí krátké hodnocení.")]
    public Task<string> GetSystemHealthAsync(CancellationToken token = default) =>
        SpankyVoiceToolService.BuildQuickHealthDiagnosticAsync(token);

    [KernelFunction("is_application_running")]
    [Description("Zjistí, zda běží aplikace nebo hra podle názvu procesu, například firefox nebo DiabloImmortal.")]
    public string IsApplicationRunning([Description("Název procesu nebo aplikace")] string name) =>
        SpankyVoiceToolService.IsAnyProcessRunning(name)
            ? $"{name} běží."
            : $"{name} neběží.";

    [KernelFunction("get_voice_health")]
    [Description("Vrátí stav mikrofonu, voice pipeline, AI backendu a TTS pro diagnostiku hlasového asistenta.")]
    public string GetVoiceHealth() => SpankyVoiceListenerService.GetHealthSummary();

    [KernelFunction("get_fan_status")]
    [Description("Vrátí aktuální profil ventilátorů a cílové otáčky.")]
    public string GetFanStatus()
    {
        FanProfile profile = FanProfileService.Load();
        string mode = profile.CpuTempTargetEnabled ? "auto" :
            profile.TargetRpm <= 800 ? "night" :
            profile.TargetRpm <= 1200 ? "quiet" :
            profile.TargetRpm >= 2000 ? "performance" : "balanced";
        return $"mode={mode}; target_rpm={profile.TargetRpm}";
    }

    [KernelFunction("apply_gaming_power_profile")]
    [Description("Zapne Spankyho Performance gaming power profil ve Windows. Použij jen když o to uživatel požádá.")]
    public async Task<string> ApplyGamingPowerProfileAsync(CancellationToken token = default)
    {
        return await PerformanceProfilesService.ApplyPerformancePowerProfileAsync(token).ConfigureAwait(false);
    }

    [KernelFunction("set_fan_profile")]
    [Description("Nastaví profil ventilátorů. Povolené hodnoty: night, quiet, auto, balanced, performance.")]
    public string SetFanProfile(
        [Description("night, quiet, auto, balanced nebo performance")] string preset)
    {
        string normalized = (preset ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized is not ("night" or "quiet" or "auto" or "balanced" or "performance"))
            return "Neplatný profil.";
        return FanProfileService.ApplyVoicePreset(normalized)
            ? $"Profil {normalized} byl nastaven."
            : $"Profil {normalized} se nepodařilo nastavit.";
    }
}