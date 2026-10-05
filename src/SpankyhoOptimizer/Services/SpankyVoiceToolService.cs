using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public static class SpankyVoiceToolService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(6)
    };

    private static readonly object WeatherContextSync = new();
    private static bool _weatherContextSet;
    private static string? _lastWeatherLocation;
    private static DateTime _lastWeatherContextUtc = DateTime.MinValue;

    private static readonly Dictionary<string, string> WeatherLocationAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["praze"] = "Praha",
            ["praha"] = "Praha",
            ["brne"] = "Brno",
            ["brno"] = "Brno",
            ["ostrave"] = "Ostrava",
            ["ostrava"] = "Ostrava",
            ["plzni"] = "Plzeň",
            ["plzen"] = "Plzeň",
            ["liberci"] = "Liberec",
            ["liberec"] = "Liberec",
            ["olomouci"] = "Olomouc",
            ["olomouc"] = "Olomouc",
            ["zline"] = "Zlín",
            ["zlin"] = "Zlín",
            ["jihlave"] = "Jihlava",
            ["jihlava"] = "Jihlava",
            ["pardubicich"] = "Pardubice",
            ["pardubice"] = "Pardubice",
            ["hradci kralove"] = "Hradec Králové",
            ["hradec kralove"] = "Hradec Králové",
            ["ceskych budejovicich"] = "České Budějovice",
            ["ceske budejovice"] = "České Budějovice",
            ["karlovych varech"] = "Karlovy Vary",
            ["karlovy vary"] = "Karlovy Vary",
            ["bratislave"] = "Bratislava",
            ["bratislava"] = "Bratislava",
            ["vidni"] = "Vienna",
            ["viden"] = "Vienna",
            ["berline"] = "Berlin",
            ["berlin"] = "Berlin",
            ["londyne"] = "London",
            ["london"] = "London",
            ["parizi"] = "Paris",
            ["paris"] = "Paris",
            ["new yorku"] = "New York",
            ["new york"] = "New York",
            ["tokiu"] = "Tokyo",
            ["tokyo"] = "Tokyo"
        };

    private static readonly string[] TimePhrases =
    {
        "kolik je hodin", "kolik hodin", "jaky je cas", "jaky mame cas", "rekni cas",
        "cas je", "aktualni cas", "hodiny"
    };

    private static readonly string[] DatePhrases =
    {
        "jake je datum", "jaky je datum", "kolikateho je", "dnesni datum", "jaky je dnes den",
        "co je dnes za den", "datum dnes", "dnes je kolikateho"
    };

    private static readonly string[] CpuTelemetryPhrases =
    {
        "teplota cpu", "cpu teplota", "teplota procesoru", "teplota procesor",
        "kolik ma cpu stupnu", "kolik stupnu ma cpu", "kolik stupnu ma procesor",
        "jakou mam teplotu cpu", "jakou ma cpu teplotu", "jakou ma procesor teplotu",
        "jak je horky procesor", "jak je horky cpu", "kolik ma procesor", "kolik ma cpu",
        "vytizeni cpu", "cpu vytizeni", "cpu load", "zatizeni cpu", "zatizeni procesoru",
        "takt cpu", "takt procesoru", "frekvence cpu", "frekvence procesoru",
        "prikon cpu", "spotreba cpu", "cpu watty", "procesor watty"
    };

    private static readonly string[] GpuTelemetryPhrases =
    {
        "vytizeni gpu", "gpu vytizeni", "gpu load", "grafika vytizeni", "vytizeni grafiky",
        "teplota gpu", "gpu teplota", "teplota grafiky", "kolik ma gpu stupnu", "kolik stupnu ma gpu",
        "kolik jede gpu", "kolik jede grafika", "zatizeni gpu", "zatizeni grafiky",
        "jak jede gpu", "jak jede grafika", "a gpu", "a grafika", "co gpu", "jak gpu"
    };

    private static readonly string[] RamStatusPhrases =
    {
        "stav ram", "kolik ram", "kolik mam ram", "kolik je ram", "vyuziti ram",
        "vytizeni ram", "pamet ram", "volna ram", "kolik je volne ram", "kolik ram je volne",
        "kolik mam volne pameti", "stav pameti", "a ram", "a pamet", "co ram", "co pamet"
    };

    private static readonly string[] RamTopPhrases =
    {
        "co zere ram", "co mi zere ram", "nejvic ram", "nejvic pameti", "zere pamet",
        "co bere ram", "co pouziva nejvic ram", "ktery proces zere ram", "ktery proces bere ram"
    };

    private static readonly string[] CpuTopPhrases =
    {
        "co zere cpu", "nejvic cpu", "ktery proces zere cpu", "co bere cpu",
        "co pouziva cpu", "ktery proces pouziva nejvic cpu", "co vytizuje procesor"
    };

    private static readonly string[] NetworkPhrases =
    {
        "ping", "latence", "odezva site", "odezva internetu", "jaky mam ping",
        "kolik mam ping", "jakou mam latenci", "jak rychla je odezva", "odezva pripojeni"
    };

    private static readonly string[] WeatherCurrentPhrases =
    {
        "pocasi", "jake je pocasi", "jaky je pocasi", "jak je venku", "co je venku",
        "teplota venku", "kolik je venku stupnu", "kolik je venku", "je venku zima",
        "je venku teplo", "prsi venku", "snezi venku", "bude ted prset"
    };

    private static readonly string[] WeatherTodayPhrases =
    {
        "predpoved pocasi", "predpoved na dnes", "pocasi dnes", "jak bude dnes",
        "bude dnes prset", "bude dnes snezit", "dnesni pocasi", "jak bude venku dnes", "a dnes", "co dnes"
    };

    private static readonly string[] WeatherTomorrowPhrases =
    {
        "jak bude zitra", "pocasi zitra", "jake bude zitra pocasi", "bude zitra prset",
        "bude zitra snezit", "teplota zitra", "predpoved na zitra", "jak bude venku zitra", "a zitra", "co zitra"
    };

    private static readonly string[] SystemPhrases =
    {
        "stav systemu", "prehled systemu", "jak je na tom pc", "jak je na tom pocitac",
        "jaky mam windows", "jaky je power plan", "stav pocitace", "prehled pc"
    };

    private static readonly string[] DiskPhrases =
    {
        "kolik mam mista", "kolik je volneho mista", "kolik je volno na disku", "stav disku",
        "vyuziti disku", "volne misto", "kolik mam volneho mista", "disk usage"
    };

    private static readonly string[] DnsPhrases =
    {
        "jake mam dns", "jaky mam dns", "dns servery", "stav dns", "dns status",
        "jake dns pouzivam", "ktere dns pouzivam"
    };

    private static readonly string[] PowerPlanPhrases =
    {
        "jaky mam power plan", "aktivni power plan", "jaky je power plan", "napajeci profil",
        "aktivni profil napajeni", "jaky mam profil napajeni"
    };

    private static readonly string[] OptimizerVersionPhrases =
    {
        "jaka je verze optimizeru", "verze optimizeru", "jaky je build", "jaka je verze aplikace",
        "verze spankyho optimizeru", "jaky je build optimizeru"
    };

    private static readonly string[] WindowsVersionPhrases =
    {
        "jaky mam windows", "verze windows", "jaka je verze windows", "jakou mam verzi windows",
        "jaky windows mam"
    };

    private static readonly string[] FanStatusPhrases =
    {
        "stav vetraku", "otacky vetraku", "kolik jedou vetraky", "jak jedou vetraky",
        "kolik maji vetraky otacek", "jaky je profil vetraku", "jaky je fan profil",
        "fan status", "vetraky status"
    };

    private static readonly string[] VoiceHealthPhrases =
    {
        "proc nemluvis", "proc me neslysis", "voice nefunguje", "hlas nefunguje",
        "zkontroluj voice", "zkontroluj hlas", "diagnostika hlasu", "stav hlasoveho asistenta",
        "latence voice", "latence hlasu", "jak rychle reagujes", "jak rychly je voice", "stav stt", "stav tts"
    };

    private static readonly string[] FirefoxTroubleshootPhrases =
    {
        "proc mi laguje firefox", "proc firefox laguje", "firefox se seka", "firefox je pomaly",
        "diagnostikuj firefox", "zkontroluj firefox", "co brzdi firefox"
    };

    private static readonly string[] OverloadPhrases =
    {
        "je neco pretizeny", "je neco pretizene", "co je pretizene", "co je pretizeny",
        "zkontroluj jestli neni neco spatne", "zkontroluj pc", "co ted brzdi pc", "proc je pc pomaly"
    };

    private static readonly (string[] Phrases, string Section, string Reply)[] SectionRoutes =
    {
        (new[] { "otevri firefox optimizer", "ukaz firefox sekci", "firefox sekci", "otevri nastaveni firefoxu" }, "browsers", "Otevírám Firefox sekci."),
        (new[] { "otevri sit", "otevri network", "otevri internet", "sitovou sekci" }, "network", "Otevírám síť."),
        (new[] { "otevri grafiku", "otevri gpu", "gpu sekci" }, "gpu", "Otevírám GPU."),
        (new[] { "otevri power plan", "otevri napajeni", "power plan sekci" }, "power", "Otevírám Power Plan."),
        (new[] { "otevri uloziste", "otevri nvme", "storage sekci" }, "storage", "Otevírám úložiště."),
        (new[] { "otevri system tweaky", "otevri system", "systemovou sekci" }, "system", "Otevírám systémové tweaky."),
        (new[] { "otevri windows update", "otevri aktualizace" }, "update", "Otevírám Windows Update."),
        (new[] { "otevri defender", "defender sekci" }, "defender", "Otevírám Defender."),
        (new[] { "otevri debloater", "debloater sekci" }, "debloat", "Otevírám Debloater."),
        (new[] { "otevri fixer", "fixer sekci" }, "fixer", "Otevírám Fixer.")
    };

    public static async Task<string?> TryHandleAsync(string utterance, CancellationToken token = default)
    {
        string text = NormalizeRoutingText(utterance);
        if (text.Length == 0) return null;

        if (Has(text, TimePhrases))
            return $"Je {DateTime.Now:HH:mm}.";

        if (Has(text, DatePhrases))
            return $"Dnes je {DateTime.Now:dddd d. MMMM yyyy}.";

        string? fanPreset = VoiceFanPresetIntentPolicy.TryResolve(text);
        if (!string.IsNullOrWhiteSpace(fanPreset))
        {
            return FanProfileService.ApplyVoicePreset(fanPreset)
                ? BuildFanPresetReply(fanPreset)
                : "Profil ventilátorů se nepodařilo změnit.";
        }

        if (Has(text, FanStatusPhrases))
            return BuildFanStatusReply();

        if (Has(text, "otevri firefox", "spust firefox", "zapni firefox"))
        {
            string? firefox = FirefoxUpdateService.FindFirefoxExe();
            return !string.IsNullOrWhiteSpace(firefox) && ProcessRunner.StartDetached(firefox)
                ? "Otevírám Firefox."
                : "Firefox se nepodařilo spustit.";
        }

        foreach ((string[] phrases, string section, string reply) in SectionRoutes)
        {
            if (!Has(text, phrases))
                continue;

            return FanProfileService.OpenSectionFromVoice(section)
                ? reply
                : "Sekci se nepodařilo otevřít.";
        }

        if (Has(text, "spust dns benchmark", "spusti dns benchmark", "spusť dns benchmark", "dns benchmark spust", "otestuj dns"))
            return await RunDnsBenchmarkVoiceAsync(token).ConfigureAwait(false);

        if (Has(text, VoiceHealthPhrases))
            return SpankyVoiceListenerService.GetHealthSummary();

        if (Has(text, FirefoxTroubleshootPhrases))
            return await BuildFirefoxDiagnosticAsync(token).ConfigureAwait(false);

        if (Has(text, OverloadPhrases))
            return await BuildQuickHealthDiagnosticAsync(token).ConfigureAwait(false);

        if (Has(text, RamTopPhrases))
            return BuildTopMemoryReply();

        if (Has(text, RamStatusPhrases))
            return "RAM: " + SystemInfoService.GetLiveRamSummary() + ".";

        if (Has(text, DiskPhrases))
            return "Disk: " + SystemInfoService.GetLiveDiskSummary() + ".";

        if (Has(text, DnsPhrases))
            return BuildDnsStatusReply();

        if (Has(text, PowerPlanPhrases))
        {
            OverviewSnapshot? snapshot = SystemInfoService.TryGetFreshSnapshot();
            return snapshot == null
                ? "Aktivní power plan se ještě načítá."
                : "Aktivní power plan je " + snapshot.PowerPlan + ".";
        }

        if (Has(text, WindowsVersionPhrases))
        {
            OverviewSnapshot? snapshot = SystemInfoService.TryGetFreshSnapshot();
            return snapshot == null
                ? "Windows se ještě načítá."
                : $"Máš {snapshot.Windows}.";
        }

        if (Has(text, OptimizerVersionPhrases))
        {
            string version = typeof(SpankyVoiceToolService).Assembly.GetName().Version?.ToString(3) ?? "neznámá";
            return $"Spankyho Optimizer je verze {version}.";
        }

        if (Has(text, SystemPhrases))
        {
            OverviewSnapshot? snapshot = SystemInfoService.TryGetFreshSnapshot();
            if (snapshot == null) return "Přehled systému se ještě načítá.";
            return $"Běží {snapshot.Windows}. Procesor {snapshot.Cpu}. Grafika {snapshot.Gpu}. RAM {snapshot.Ram}. Napájení {snapshot.PowerPlan}.";
        }

        if (Has(text, GpuTelemetryPhrases))
            return BuildGpuTelemetryReply();

        if (Has(text, CpuTelemetryPhrases) || text.Contains("teplot cpu") || text.Contains("teplot procesor"))
            return BuildCpuTelemetryReply();

        if (Has(text, CpuTopPhrases))
            return await BuildTopCpuReplyAsync(token).ConfigureAwait(false);

        if (Has(text, NetworkPhrases))
            return await BuildLatencyReplyAsync(token).ConfigureAwait(false);

        if (Has(text, "bezi diablo", "mam spustene diablo", "je zapnute diablo"))
            return IsAnyProcessRunning("DiabloImmortal", "Diablo Immortal") ? "Jo, Diablo Immortal běží." : "Diablo Immortal teď neběží.";

        if (Has(text, "bezi firefox", "mam spusteny firefox", "je zapnuty firefox"))
            return IsAnyProcessRunning("firefox") ? "Jo, Firefox běží." : "Firefox teď neběží.";

        string? explicitWeatherLocation = ExtractWeatherLocation(text);

        if (Has(text, WeatherTomorrowPhrases))
        {
            bool followUp = IsWeatherFollowUp(text);
            string? location = explicitWeatherLocation ?? (followUp ? GetRememberedWeatherLocation() : null);
            RememberWeatherLocation(location);
            return await GetWeatherForecastAsync(location, 1, token).ConfigureAwait(false);
        }

        if (Has(text, WeatherTodayPhrases))
        {
            bool followUp = IsWeatherFollowUp(text);
            string? location = explicitWeatherLocation ?? (followUp ? GetRememberedWeatherLocation() : null);
            RememberWeatherLocation(location);
            return await GetWeatherForecastAsync(location, 0, token).ConfigureAwait(false);
        }

        bool weatherWithLocation =
            explicitWeatherLocation != null &&
            (text.Contains("pocas", StringComparison.Ordinal) ||
             text.Contains("teplot", StringComparison.Ordinal) ||
             text.StartsWith("jak je v ", StringComparison.Ordinal) ||
             text.StartsWith("jak je ve ", StringComparison.Ordinal));

        if (Has(text, WeatherCurrentPhrases) || text.Contains("pocas") ||
            (text.Contains("venku") && text.Contains("jak")) || weatherWithLocation)
        {
            RememberWeatherLocation(explicitWeatherLocation);
            return await GetWeatherAsync(explicitWeatherLocation, token).ConfigureAwait(false);
        }

        return null;
    }

    private static string BuildFanPresetReply(string preset) =>
        preset switch
        {
            "night" => "Zapínám noční režim ventilátorů, 700 RPM.",
            "quiet" => "Zapínám tichý režim ventilátorů, 1100 RPM.",
            "auto" => "Zapínám automatický režim ventilátorů s cílem 65 stupňů.",
            "balanced" => "Zapínám vyvážený režim ventilátorů, 1620 RPM.",
            "performance" => "Zapínám výkonný režim ventilátorů, 2100 RPM.",
            _ => "Profil ventilátorů je změněný."
        };

    private static string BuildFanStatusReply()
    {
        FanProfile profile = FanProfileService.Load();
        FanAgentHealth? health = FanProfileService.ReadHealthStatus();

        string profileName = profile.CpuTempTargetEnabled
            ? $"automatický s cílem {profile.CpuTempTargetC} stupňů"
            : profile.TargetRpm switch
            {
                <= 700 => "noční",
                <= 1100 => "tichý",
                <= 1700 => "vyvážený",
                >= 1900 => "výkonný",
                _ => $"{profile.TargetRpm} RPM"
            };

        if (health != null &&
            DateTime.UtcNow - health.UpdatedUtc <= TimeSpan.FromSeconds(12))
        {
            int[] validRpms = health.Rpms.Where(x => x > 0).ToArray();
            if (validRpms.Length > 0)
            {
                int average = (int)Math.Round(validRpms.Average());
                return $"Větráky jedou přibližně {average} RPM. Profil je {profileName}.";
            }
        }

        return profile.CpuTempTargetEnabled
            ? $"Větráky jedou v profilu {profileName}."
            : $"Větráky jsou nastavené na {profile.TargetRpm} RPM. Profil je {profileName}.";
    }

    public static string BuildCpuTelemetryReply()
    {
        FanAgentHealth? agent = FanProfileService.ReadHealthStatus();
        if (agent != null &&
            DateTime.UtcNow - agent.UpdatedUtc <= TimeSpan.FromSeconds(12) &&
            (agent.CpuTempC.HasValue || agent.LoadPercent >= 0))
        {
            string temp = agent.CpuTempC.HasValue ? $"{agent.CpuTempC:0.#} °C" : "teplota nedostupná";
            string load = $"{agent.LoadPercent:0.#} %";
            string clock = agent.EffectiveClockMHz.HasValue ? $"{agent.EffectiveClockMHz:0} MHz" : "takt nedostupný";
            string power = agent.PackagePowerW.HasValue ? $"{agent.PackagePowerW:0.#} W" : "příkon nedostupný";
            return $"CPU: {temp}, vytížení {load}, efektivní takt {clock}, příkon {power}.";
        }

        CpuTemperatureService.CpuTelemetrySample sample = HardwareTelemetryBridgeService.ReadBestTelemetry();
        string fallbackTemp = sample.Celsius.HasValue ? $"{sample.Celsius:0.#} °C" : "teplota nedostupná";
        string fallbackLoad = sample.LoadPercent.HasValue ? $"{sample.LoadPercent:0.#} %" : "load nedostupný";
        string fallbackClock = sample.EffectiveClockMHz.HasValue ? $"{sample.EffectiveClockMHz:0} MHz" : "takt nedostupný";
        string fallbackPower = sample.PackagePowerW.HasValue ? $"{sample.PackagePowerW:0.#} W" : "příkon nedostupný";
        return $"CPU {sample.CpuName}: {fallbackTemp}, vytížení {fallbackLoad}, efektivní takt {fallbackClock}, příkon {fallbackPower}.";
    }

    public static string BuildGpuTelemetryReply()
    {
        CpuTemperatureService.GpuTelemetrySample sensor;
        try
        {
            sensor = CpuTemperatureService.ReadGpuTelemetry();
        }
        catch
        {
            sensor = new CpuTemperatureService.GpuTelemetrySample(
                null,
                "GPU senzor nedostupný",
                "GPU",
                null);
        }

        double? load = GpuPdhUsageService.TryRead() ?? sensor.LoadPercent;
        string temp = sensor.Celsius.HasValue ? $"{sensor.Celsius:0.#} °C" : "teplota nedostupná";
        string utilization = load.HasValue ? $"{load.Value:0.#} %" : "vytížení nedostupné";
        return $"{sensor.GpuName}: {temp}, vytížení {utilization}.";
    }

    public static async Task<string> RunDnsBenchmarkVoiceAsync(CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));

        try
        {
            IReadOnlyList<DnsBenchmarkResult> results =
                await DnsBenchmarkService.RunSmokeAsync(timeout.Token).ConfigureAwait(false);

            DnsBenchmarkResult? best = results
                .Where(x => x.IsAlive && !x.RedirectsNxDomain && string.IsNullOrWhiteSpace(x.Error))
                .OrderByDescending(x => x.OverallScore)
                .ThenBy(x => x.AverageMs)
                .FirstOrDefault();

            return best == null
                ? "DNS benchmark doběhl, ale nenašel spolehlivý resolver."
                : $"Nejlépe vyšel {best.Name}, {best.Address}, průměr {best.AverageMs:0.#} ms, jitter {best.JitterMs:0.#} ms.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return "DNS benchmark se nestihl dokončit do dvanácti sekund.";
        }
        catch (Exception ex)
        {
            LogService.Error("voice", "DNS benchmark voice action failed", ex);
            return "DNS benchmark se nepodařilo spustit.";
        }
    }

    public static string BuildDnsStatusReply()
    {
        try
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
        catch (Exception ex)
        {
            LogService.Error("voice", "DNS status tool failed", ex);
            return "DNS servery se teď nepodařilo zjistit.";
        }
    }

    public static async Task<string> BuildLatencyReplyAsync(CancellationToken token = default)
    {
        try
        {
            using var ping = new Ping();
            token.ThrowIfCancellationRequested();
            PingReply reply = await ping.SendPingAsync("1.1.1.1", 2000).WaitAsync(token).ConfigureAwait(false);
            return reply.Status == IPStatus.Success
                ? $"Aktuální síťová odezva na 1.1.1.1 je {reply.RoundtripTime} milisekund."
                : $"Ping selhal se stavem {reply.Status}.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Error("voice", "Latency tool failed", ex);
            return "Síťovou odezvu se teď nepodařilo změřit.";
        }
    }

    public static async Task<string> BuildFirefoxDiagnosticAsync(CancellationToken token = default)
    {
        Process[] firefox = Process.GetProcessesByName("firefox");
        if (firefox.Length == 0)
            return "Firefox teď neběží.";

        try
        {
            var samples = firefox.Select(p =>
            {
                try { return (Process: p, Cpu: p.TotalProcessorTime, Ram: p.WorkingSet64); }
                catch { return (Process: p, Cpu: TimeSpan.Zero, Ram: 0L); }
            }).ToArray();

            var sw = Stopwatch.StartNew();
            Task<PingReply> pingTask;
            using var ping = new Ping();
            try { pingTask = ping.SendPingAsync("1.1.1.1", 1200); }
            catch { pingTask = Task.FromResult<PingReply>(null!); }

            await Task.Delay(320, token).ConfigureAwait(false);
            double elapsedMs = Math.Max(1, sw.Elapsed.TotalMilliseconds);
            int cpuCount = Math.Max(1, Environment.ProcessorCount);

            double cpu = 0;
            long ram = 0;
            foreach (var sample in samples)
            {
                try
                {
                    TimeSpan delta = sample.Process.TotalProcessorTime - sample.Cpu;
                    cpu += Math.Max(0, delta.TotalMilliseconds / elapsedMs * 100d / cpuCount);
                    ram += sample.Process.WorkingSet64;
                }
                catch { ram += sample.Ram; }
            }

            long? pingMs = null;
            try
            {
                PingReply reply = await pingTask.WaitAsync(token).ConfigureAwait(false);
                if (reply != null && reply.Status == IPStatus.Success)
                    pingMs = reply.RoundtripTime;
            }
            catch { }

            double ramGb = ram / 1073741824d;
            string ramSystem = SystemInfoService.GetLiveRamSummary();
            string network = pingMs.HasValue ? $"{pingMs.Value} ms" : "nedostupný";

            string finding =
                cpu >= 70 ? "Firefox je teď výrazně vytížený na CPU." :
                ramGb >= 4.0 ? "Firefox teď používá hodně RAM." :
                pingMs >= 100 ? "Síťová odezva je vysoká." :
                "CPU, RAM Firefoxu ani základní ping teď nevypadají jako hlavní problém.";

            return $"Firefox: {firefox.Length} procesů, CPU {cpu:0.#} %, RAM {ramGb:0.0} GB, ping {network}. Systémová RAM {ramSystem}. {finding}";
        }
        finally
        {
            foreach (Process process in firefox)
                process.Dispose();
        }
    }

    public static async Task<string> BuildQuickHealthDiagnosticAsync(CancellationToken token = default)
    {
        FanAgentHealth? agent = FanProfileService.ReadHealthStatus();
        bool freshAgent = agent != null && DateTime.UtcNow - agent.UpdatedUtc <= TimeSpan.FromSeconds(12);

        double? cpuTemp = freshAgent ? agent!.CpuTempC : null;
        double? cpuLoad = freshAgent ? agent!.LoadPercent : null;
        if (!freshAgent)
        {
            CpuTemperatureService.CpuTelemetrySample fallbackCpu = HardwareTelemetryBridgeService.ReadBestTelemetry();
            cpuTemp = fallbackCpu.Celsius;
            cpuLoad = fallbackCpu.LoadPercent;
        }

        CpuTemperatureService.GpuTelemetrySample gpu = CpuTemperatureService.ReadGpuTelemetry();
        double? gpuLoad = GpuPdhUsageService.TryRead() ?? gpu.LoadPercent;
        string ram = SystemInfoService.GetLiveRamSummary();

        long? pingMs = null;
        try
        {
            using var ping = new Ping();
            PingReply reply = await ping.SendPingAsync("1.1.1.1", 1200).WaitAsync(token).ConfigureAwait(false);
            if (reply.Status == IPStatus.Success)
                pingMs = reply.RoundtripTime;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { }

        bool cpuHigh = cpuLoad >= 85;
        bool gpuHigh = gpuLoad >= 95;
        bool hotCpu = cpuTemp >= 90;
        bool hotGpu = gpu.Celsius >= 88;
        bool networkHigh = pingMs >= 100;

        string assessment = cpuHigh || gpuHigh || hotCpu || hotGpu || networkHigh
            ? "Vidím aspoň jednu hodnotu, která stojí za pozornost."
            : "Na CPU, GPU, teplotách ani základním pingu teď nevidím zjevné přetížení.";

        return $"CPU {cpuLoad?.ToString("0.#") ?? "?"} %, {cpuTemp?.ToString("0.#") ?? "?"} °C. " +
               $"GPU {gpuLoad?.ToString("0.#") ?? "?"} %, {gpu.Celsius?.ToString("0.#") ?? "?"} °C. " +
               $"RAM {ram}. Ping {(pingMs.HasValue ? pingMs.Value + " ms" : "nedostupný")}. {assessment}";
    }

    public static async Task<string> BuildTopCpuReplyAsync(CancellationToken token = default)
    {
        var first = Process.GetProcesses()
            .Select(p =>
            {
                try { return (Process: p, Cpu: p.TotalProcessorTime); }
                catch { p.Dispose(); return (Process: (Process?)null, Cpu: TimeSpan.Zero); }
            })
            .Where(x => x.Process != null)
            .ToArray();

        try
        {
            var sampleClock = Stopwatch.StartNew();
            await Task.Delay(320, token).ConfigureAwait(false);
            double elapsedMs = Math.Max(1, sampleClock.Elapsed.TotalMilliseconds);
            int cpuCount = Math.Max(1, Environment.ProcessorCount);
            var top = first.Select(x =>
            {
                try
                {
                    TimeSpan delta = x.Process!.TotalProcessorTime - x.Cpu;
                    double percent = Math.Max(0, delta.TotalMilliseconds / elapsedMs * 100d / cpuCount);
                    return (Name: x.Process.ProcessName, Percent: percent);
                }
                catch { return (Name: string.Empty, Percent: 0d); }
            })
            .Where(x => x.Percent > 0.1 && x.Name.Length > 0)
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Percent: g.Sum(x => x.Percent)))
            .OrderByDescending(x => x.Percent)
            .Take(3)
            .ToArray();

            return top.Length == 0
                ? "Teď nevidím žádný proces s výrazným využitím CPU."
                : "Nejvíc CPU teď používá " + string.Join(", ", top.Select(x => $"{x.Name} {x.Percent:0.#} %")) + ".";
        }
        finally
        {
            foreach (var item in first)
                item.Process?.Dispose();
        }
    }

    public static bool IsAnyProcessRunning(params string[] names)
    {
        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                string name = process.ProcessName;
                if (names.Any(x => name.Contains(x, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            catch { }
            finally { process.Dispose(); }
        }
        return false;
    }

    private static string BuildTopMemoryReply()
    {
        try
        {
            var top = Process.GetProcesses()
                .Select(p =>
                {
                    try { return (Name: p.ProcessName, Bytes: p.WorkingSet64); }
                    catch { return (Name: string.Empty, Bytes: 0L); }
                    finally { p.Dispose(); }
                })
                .Where(x => x.Bytes > 0 && x.Name.Length > 0)
                .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Name: g.Key, Bytes: g.Sum(x => x.Bytes)))
                .OrderByDescending(x => x.Bytes)
                .Take(3)
                .ToArray();

            if (top.Length == 0) return "Nenašel jsem procesy s využitím RAM.";

            string items = string.Join(", ", top.Select(x =>
                $"{x.Name} {x.Bytes / 1073741824d:0.0} GB"));
            return "Nejvíc RAM teď používá " + items + ".";
        }
        catch
        {
            return "Teď se mi nepodařilo načíst využití RAM procesů.";
        }
    }

    internal static string? ExtractWeatherLocation(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        int marker = text.LastIndexOf(" ve ", StringComparison.Ordinal);
        int markerLength = 4;
        int alternative = text.LastIndexOf(" v ", StringComparison.Ordinal);
        if (alternative > marker)
        {
            marker = alternative;
            markerLength = 3;
        }

        int start;
        if (marker >= 0)
            start = marker + markerLength;
        else if (text.StartsWith("ve ", StringComparison.Ordinal))
            start = 3;
        else if (text.StartsWith("v ", StringComparison.Ordinal))
            start = 2;
        else
            return null;

        string candidate = text[start..].Trim();
        if (candidate.Length < 2)
            return null;

        string[] words = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var placeWords = new List<string>();
        foreach (string word in words)
        {
            if (word is "dnes" or "dneska" or "zitra" or "ted" or "vecer" or "rano" or
                "odpoledne" or "noci" or "bude" or "je" or "prsi" or "snezi" or "pocasi" or
                "predpoved" or "teplota" or "stupnu" or "jak" or "jake" or "jaky")
                break;
            if (placeWords.Count == 0 && word.All(char.IsDigit))
                return null;
            placeWords.Add(word);
        }

        if (placeWords.Count == 0)
            return null;

        string raw = string.Join(' ', placeWords);
        return WeatherLocationAliases.TryGetValue(raw, out string? canonical)
            ? canonical
            : raw;
    }

    private static bool IsWeatherFollowUp(string text) =>
        text is "a zitra" or "co zitra" or "a dnes" or "co dnes" ||
        text.StartsWith("a zitra ", StringComparison.Ordinal) ||
        text.StartsWith("a dnes ", StringComparison.Ordinal);

    private static void RememberWeatherLocation(string? location)
    {
        lock (WeatherContextSync)
        {
            _weatherContextSet = true;
            _lastWeatherLocation = string.IsNullOrWhiteSpace(location) ? null : location.Trim();
            _lastWeatherContextUtc = DateTime.UtcNow;
        }
    }

    private static string? GetRememberedWeatherLocation()
    {
        lock (WeatherContextSync)
        {
            if (!_weatherContextSet || DateTime.UtcNow - _lastWeatherContextUtc > TimeSpan.FromMinutes(10))
                return null;
            return _lastWeatherLocation;
        }
    }

    public static Task<string> GetWeatherAsync(CancellationToken token = default) =>
        GetWeatherAsync(null, token);

    public static async Task<string> GetWeatherAsync(string? location, CancellationToken token = default)
    {
        try
        {
            double latitude;
            double longitude;
            string city;

            if (string.IsNullOrWhiteSpace(location))
            {
                using HttpResponseMessage geoResponse = await Http.GetAsync("https://ipwho.is/", token).ConfigureAwait(false);
                string geoBody = await geoResponse.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                using JsonDocument geo = JsonDocument.Parse(geoBody);
                JsonElement root = geo.RootElement;

                if (!geoResponse.IsSuccessStatusCode ||
                    (root.TryGetProperty("success", out JsonElement success) && !success.GetBoolean()))
                    return "Počasí teď nemůžu načíst.";

                latitude = root.GetProperty("latitude").GetDouble();
                longitude = root.GetProperty("longitude").GetDouble();
                city = root.TryGetProperty("city", out JsonElement cityElement)
                    ? cityElement.GetString() ?? "tvojí oblasti"
                    : "tvojí oblasti";
            }
            else
            {
                string query = Uri.EscapeDataString(location.Trim());
                using HttpResponseMessage geoResponse = await Http.GetAsync(
                    $"https://geocoding-api.open-meteo.com/v1/search?name={query}&count=1&language=cs&format=json", token).ConfigureAwait(false);
                string geoBody = await geoResponse.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                if (!geoResponse.IsSuccessStatusCode)
                    return $"Místo {location} se mi nepodařilo najít.";

                using JsonDocument geo = JsonDocument.Parse(geoBody);
                if (!geo.RootElement.TryGetProperty("results", out JsonElement results) || results.GetArrayLength() == 0)
                    return $"Místo {location} se mi nepodařilo najít.";

                JsonElement result = results[0];
                latitude = result.GetProperty("latitude").GetDouble();
                longitude = result.GetProperty("longitude").GetDouble();
                city = result.TryGetProperty("name", out JsonElement name)
                    ? name.GetString() ?? location.Trim()
                    : location.Trim();
            }

            string url = FormattableString.Invariant(
                $"https://api.open-meteo.com/v1/forecast?latitude={latitude}&longitude={longitude}&current=temperature_2m,apparent_temperature,precipitation,weather_code,wind_speed_10m&timezone=auto");

            using HttpResponseMessage weatherResponse = await Http.GetAsync(url, token).ConfigureAwait(false);
            string weatherBody = await weatherResponse.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!weatherResponse.IsSuccessStatusCode)
                return "Počasí teď nemůžu načíst.";
            using JsonDocument weather = JsonDocument.Parse(weatherBody);
            JsonElement current = weather.RootElement.GetProperty("current");
            double temperature = current.GetProperty("temperature_2m").GetDouble();
            double feelsLike = current.GetProperty("apparent_temperature").GetDouble();
            double precipitation = current.GetProperty("precipitation").GetDouble();
            double wind = current.GetProperty("wind_speed_10m").GetDouble();
            int code = current.GetProperty("weather_code").GetInt32();

            string condition = WeatherCodeToCzech(code);
            string rain = precipitation > 0.05 ? $", srážky {precipitation:0.#} milimetru" : string.Empty;
            return $"V oblasti {city} je {temperature:0.#} stupně, pocitově {feelsLike:0.#}, {condition}{rain}. Vítr {wind:0.#} kilometrů za hodinu.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Error("voice", "Weather tool failed", ex);
            return "Počasí teď nemůžu načíst.";
        }
    }

    public static async Task<string> GetWeatherForecastAsync(string? location, int dayOffset = 0, CancellationToken token = default)
    {
        try
        {
            dayOffset = Math.Clamp(dayOffset, 0, 6);
            double latitude;
            double longitude;
            string place;

            if (string.IsNullOrWhiteSpace(location))
            {
                using HttpResponseMessage geoResponse = await Http.GetAsync("https://ipwho.is/", token).ConfigureAwait(false);
                string geoBody = await geoResponse.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                using JsonDocument geo = JsonDocument.Parse(geoBody);
                JsonElement root = geo.RootElement;
                if (!geoResponse.IsSuccessStatusCode ||
                    (root.TryGetProperty("success", out JsonElement success) && !success.GetBoolean()))
                    return "Předpověď počasí teď nemůžu načíst.";

                latitude = root.GetProperty("latitude").GetDouble();
                longitude = root.GetProperty("longitude").GetDouble();
                place = root.TryGetProperty("city", out JsonElement cityElement)
                    ? cityElement.GetString() ?? "tvojí oblasti"
                    : "tvojí oblasti";
            }
            else
            {
                string query = Uri.EscapeDataString(location.Trim());
                using HttpResponseMessage geoResponse = await Http.GetAsync(
                    $"https://geocoding-api.open-meteo.com/v1/search?name={query}&count=1&language=cs&format=json", token).ConfigureAwait(false);
                string geoBody = await geoResponse.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                if (!geoResponse.IsSuccessStatusCode)
                    return $"Místo {location} se mi nepodařilo najít.";

                using JsonDocument geo = JsonDocument.Parse(geoBody);
                if (!geo.RootElement.TryGetProperty("results", out JsonElement results) || results.GetArrayLength() == 0)
                    return $"Místo {location} se mi nepodařilo najít.";

                JsonElement result = results[0];
                latitude = result.GetProperty("latitude").GetDouble();
                longitude = result.GetProperty("longitude").GetDouble();
                place = result.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? location.Trim() : location.Trim();
            }

            string url = FormattableString.Invariant(
                $"https://api.open-meteo.com/v1/forecast?latitude={latitude}&longitude={longitude}&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,wind_speed_10m_max&forecast_days=7&timezone=auto");

            using HttpResponseMessage weatherResponse = await Http.GetAsync(url, token).ConfigureAwait(false);
            string weatherBody = await weatherResponse.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!weatherResponse.IsSuccessStatusCode)
                return "Předpověď počasí teď nemůžu načíst.";

            using JsonDocument weather = JsonDocument.Parse(weatherBody);
            JsonElement daily = weather.RootElement.GetProperty("daily");
            JsonElement dates = daily.GetProperty("time");
            if (dates.GetArrayLength() <= dayOffset)
                return "Předpověď pro ten den není dostupná.";

            string date = dates[dayOffset].GetString() ?? string.Empty;
            double max = daily.GetProperty("temperature_2m_max")[dayOffset].GetDouble();
            double min = daily.GetProperty("temperature_2m_min")[dayOffset].GetDouble();
            double wind = daily.GetProperty("wind_speed_10m_max")[dayOffset].GetDouble();
            int rain = daily.GetProperty("precipitation_probability_max")[dayOffset].GetInt32();
            int code = daily.GetProperty("weather_code")[dayOffset].GetInt32();
            string when = dayOffset == 0 ? "Dnes" : dayOffset == 1 ? "Zítra" : DateTime.TryParse(date, out DateTime parsed) ? parsed.ToString("dddd") : date;
            return $"{when} v oblasti {place}: {WeatherCodeToCzech(code)}, {min:0.#} až {max:0.#} stupně, pravděpodobnost srážek {rain} procent, vítr až {wind:0.#} kilometrů za hodinu.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Error("voice", "Weather forecast tool failed", ex);
            return "Předpověď počasí teď nemůžu načíst.";
        }
    }

    private static string WeatherCodeToCzech(int code) => code switch
    {
        0 => "jasno",
        1 or 2 => "polojasno",
        3 => "zataženo",
        45 or 48 => "mlha",
        51 or 53 or 55 or 56 or 57 => "mrholení",
        61 or 63 or 65 or 66 or 67 => "déšť",
        71 or 73 or 75 or 77 => "sněžení",
        80 or 81 or 82 => "přeháňky",
        85 or 86 => "sněhové přeháňky",
        95 or 96 or 99 => "bouřka",
        _ => "proměnlivé počasí"
    };

    private static bool Has(string text, params string[] values) => MatchesIntent(text, values);

    internal static bool MatchesIntent(string text, params string[] values)
    {
        foreach (string value in values)
        {
            if (text.Contains(value, StringComparison.Ordinal))
                return true;
            if (FuzzyPhraseMatch(text, value))
                return true;
        }
        return false;
    }

    private static bool FuzzyPhraseMatch(string text, string phrase)
    {
        string[] source = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] target = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (target.Length == 0 || source.Length == 0)
            return false;

        if (target.Length == 1)
        {
            string wanted = target[0];
            if (wanted.Length < 5)
                return false;
            int maxDistance = wanted.Length >= 8 ? 2 : 1;
            return source.Any(word => Math.Abs(word.Length - wanted.Length) <= maxDistance &&
                                      EditDistance(word, wanted) <= maxDistance);
        }

        string[] important = target.Where(x => x.Length >= 3).ToArray();
        if (important.Length < 2)
            return false;

        int matched = 0;
        foreach (string wanted in important)
        {
            int maxDistance = wanted.Length >= 8 ? 2 : 1;
            if (source.Any(word => Math.Abs(word.Length - wanted.Length) <= maxDistance &&
                                   EditDistance(word, wanted) <= maxDistance))
                matched++;
        }

        int required = Math.Max(2, (int)Math.Ceiling(important.Length * 0.72));
        return matched >= required;
    }

    private static int EditDistance(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal))
            return 0;

        int[] previousPrevious = new int[b.Length + 1];
        int[] previous = new int[b.Length + 1];
        int[] current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
            previous[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                int value = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);

                if (i > 1 && j > 1 &&
                    a[i - 1] == b[j - 2] &&
                    a[i - 2] == b[j - 1])
                {
                    value = Math.Min(value, previousPrevious[j - 2] + 1);
                }

                current[j] = value;
            }

            (previousPrevious, previous, current) = (previous, current, previousPrevious);
        }

        return previous[b.Length];
    }

    internal static string NormalizeRoutingText(string value)
    {
        string formD = (value ?? string.Empty).ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(formD.Length);
        foreach (char c in formD)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }

        string normalized = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        normalized = normalized
            .Replace("nucni", "nocni", StringComparison.Ordinal)
            .Replace("chat gpt", "chatgpt", StringComparison.Ordinal)
            .Replace("ge force", "geforce", StringComparison.Ordinal)
            .Replace("fire fox", "firefox", StringComparison.Ordinal)
            .Replace("diablo imortal", "diablo immortal", StringComparison.Ordinal)
            .Replace("rajzen", "ryzen", StringComparison.Ordinal)
            .Replace("task sheduler", "task scheduler", StringComparison.Ordinal)
            .Replace("spanky ho optimizer", "spankyho optimizer", StringComparison.Ordinal)
            .Replace("po cas si", "pocasi", StringComparison.Ordinal)
            .Replace("po casy", "pocasi", StringComparison.Ordinal)
            .Replace("pocas si", "pocasi", StringComparison.Ordinal)
            .Replace("poas", "pocasi", StringComparison.Ordinal)
            .Replace("po as", "pocasi", StringComparison.Ordinal)
            .Replace("po c asi", "pocasi", StringComparison.Ordinal)
            .Replace("po cas", "pocasi", StringComparison.Ordinal)
            .Replace("poc as i", "pocasi", StringComparison.Ordinal);
        return normalized;
    }
}