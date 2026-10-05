# PERFORMANCE_BASELINE

Aktualizováno: 2026-09-27 23:54 CEST  
Zdroj: `artifacts/perf/latest.json` • 10 opakování • median/P95 • trace + counters + WPR + soak.

## Aktuální runtime baseline

| Metrika | Median | P95 | Poznámka |
|---|---:|---:|---|
| startup activation | 193 ms | 200 ms | `--startup-hotpath-smoke`; není vydáváno za true OS cold start |
| Network cold / warm | 22.5 / 2 ms | 29 / 3 ms | page navigation/UI path |
| Storage cold / warm | 17 / 2 ms | 19 / 4 ms | page navigation/UI path |
| GPU cold / warm | 13 / 2 ms | 15 / 4 ms | page navigation/UI path; detekce běží deferred |
| Startup cold / warm | 12 / 0 ms | 13 / 1 ms | page navigation/UI path |
| Services cold / warm | 11 / 0 ms | 14 / 1 ms | vendor snapshot deferred mimo foreground |
| Defender cold / warm | 10 / 6.5 ms | 11 / 7 ms | page navigation/UI path |
| Debloater cold / warm | 8 / 0 ms | 9 / 0 ms | page navigation/UI path |
| Power Plan cold / warm | 4 / 7 ms | 5 / 9 ms | page navigation/UI path |

## Backend / network baseline

| Operace | Fresh / cold | Warm / cache | Poznámka |
|---|---:|---:|---|
| adapter enumeration (.NET) | 4.275 ms median | 5.367 ms P95 | 30 vzorků |
| NIC capabilities | 56 ms | — | bounded/cancellable backend smoke |
| DNS readback guard | 1 ms | — | bez změny DNS |
| DNS benchmark smoke | 2473 ms | — | 24 resolverů, 16 alive, best avg 5.1 ms |
| Storage probe | 125 ms | 28 ms refresh | mimo UI thread |
| GPU detection | 179 ms | 20 ms | mimo UI thread / deferred |
| Task Scheduler inventory | 296 ms | 1 ms persistent cache | 249 tasks |

## Memory / leak

- Cold-pass high-water telemetry: private delta median **+68.6 MB**, WS delta median **+78.2 MB**, handles median **+244**. To je naplnění persistentních WinUI/page cache a není to leak verdict.
- Autoritativní soak: **0 MB WS / -1 MB private / 0 MB managed / 0 handles**.
- Leak gate se proto vyhodnocuje z opakovaného soak workloadu, ne z prvního cold passu.

## Subprocess audit

- Audit workload: **36 subprocessů**, z toho **0× powershell.exe/pwsh.exe**.
- Nejčastější runtime subprocess: `netsh.exe` 11×, median 59 ms.
- Profiler subprocessy (`dotnet-trace`, `dotnet-counters`, `wpr`) jsou audit tooling a nejsou runtime dependency aplikace.

## Build baseline

- `build-fast` v auditním běhu: **2049.8 ms**.
- Profiled incremental MSBuild wall: **6253.5 ms**.
- `XamlPreCompile`: **3029 ms** — aktuální hlavní build bottleneck.
- `MarkupCompilePass1`: **522 ms**.
- `MarkupCompilePass2`: **443 ms**.
- `ResolveProjectReferences`: **156 ms**.
- `CoreCompile`: **16 ms** (up-to-date incremental path).
- Binlog: `artifacts/perf/20260927_235324/build-profile/incremental.binlog`.

## Performance tooling

- `perf-audit.ps1`: build → source index → analyzers → repeated cold/warm measurements → network/backend timings → process telemetry → dotnet-trace → dotnet-counters → WPR → soak → report → regression gate.
- `perf-regression-gate.ps1`: startup trend, cold/warm page budgets, soak private/handle growth, PowerShell subprocess regression.
- `source-index.ps1`: local C#/XAML/PS1 index pro process/sync-wait/WMI/registry/Task.Run/Dispatcher/PowerShell/network/lock patterns.
- `analyze-performance.ps1`: oddělený Roslyn/.NET analyzer audit; nezpomaluje běžný build-fast.
- Trace artefakty: `performance.nettrace`, `runtime-counters.json`, `startup.etl`.
- WPA GUI není nainstalované; WPR ETL se přesto korektně sbírá.

## Ověřená změna tohoto auditu

- Services cold navigation: **53 ms → 31 ms** v prvním targeted rerunu; stabilizovaný 10× median následně **11 ms / P95 14 ms**.
- Příčina: synchronní `WindowsModernizerService.ReadSnapshot()` ve vendor panelu.
- Oprava: vendor snapshot přesunut mimo foreground UI path; UI nejdřív vytvoří shell a stav doplní deferred.
- Změna byla ponechána pouze po měřeném zlepšení a build/smoke validaci.
