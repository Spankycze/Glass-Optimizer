using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public sealed partial class AdaptivePowerRuntimeService
{
    private static double? ReadWorkingSetMiB(Process process)
    {
        // Exactly the five already-identifiable allowlisted processes in the
        // existing CPU snapshot. Read-only, no EmptyWorkingSet, handle scans,
        // process restarts, extra enumeration or claim of permanent RAM savings.
        try
        {
            long bytes = process.WorkingSet64;
            return bytes >= 0 ? Math.Round(bytes / 1048576d, 2) : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or
            System.ComponentModel.Win32Exception or NotSupportedException or
            ArgumentException or UnauthorizedAccessException)
        {
            return null; // Missing/denied is unknown, not zero RAM.
        }
    }

    private double? ReadCachedWorkingSetMiB(
        Process process, bool refresh, bool sameProcessCpuHistory,
        ref int nativeReads, ref int cacheHits)
    {
        int pid = process.Id;
        if (refresh)
        {
            double? value = ReadWorkingSetMiB(process);
            nativeReads++;
            if (_workingSetByPid.Count < 8)
                _workingSetByPid[pid] = value;
            return value;
        }
        if (sameProcessCpuHistory &&
            _workingSetByPid.TryGetValue(pid, out double? cached))
        {
            cacheHits++;
            return cached;
        }
        // A new PID or reset CPU counter must not inherit stale cached RAM.
        return null;
    }

    private ProcessLoadSample SampleProcessLoad(CancellationToken token, int foregroundPid)
    {
        token.ThrowIfCancellationRequested();
        DateTime now = DateTime.UtcNow;
        // TickAsync is serialized by _tickGate and has already validated
        // snapshot age, foreground PID, and HWND. Never reuse again here:
        // a changed HWND with the same PID would otherwise be invisible.
        bool metricDue = now - _lastProcessSampleMetricUtc >= TimeSpan.FromMinutes(1);
        using Process? ownMetrics = metricDue ? Process.GetCurrentProcess() : null;
        long cpuBeforeTicks = metricDue
            ? ownMetrics!.TotalProcessorTime.Ticks : 0;
        long sampleStart = Stopwatch.GetTimestamp();
        long allocatedAtStart = GC.GetAllocatedBytesForCurrentThread();
        bool refreshWorkingSet = AiWorkingSetSamplingPolicy.ShouldRefresh(
            _lastWorkingSetReadUtc, now);
        if (refreshWorkingSet)
            _workingSetByPid.Clear();
        int nativeWorkingSetReads = 0, cachedWorkingSetReads = 0;
        // Match the active window against the Process[] already needed for game
        // detection and CPU deltas. Avoid an extra OpenProcess on normal ticks.
        string foregroundFromSnapshot = string.Empty;
        bool foregroundFound = false;
        bool foregroundCpuComparable = false;
        // CPU deltas use monotonic Stopwatch time; NTP/manual Windows clock changes
        // must not inflate process percentages or affect AI workload decisions.
        bool comparableCpuWindow = AiAdaptiveProcessScanPolicy.CpuWindowComparable(
            _previousProcessSampleTimestamp, sampleStart, Stopwatch.Frequency);
        double elapsed = AiAdaptiveProcessScanPolicy.CpuWindowSeconds(
            _previousProcessSampleTimestamp, sampleStart, Stopwatch.Frequency);
        int logical = Math.Max(1, Environment.ProcessorCount);
        _processCpuHistory.BeginCycle();
        _cpuDeniedCache.BeginCycle(now);
        int nativeCpuReadAttempts = 0;
        int comparableCpuCounters = 0;
        int invalidCpuCounterDeltas = 0;
        // PID/name cache is refreshed in full every fourth scan. Newly seen
        // PIDs and CPU-counter resets always fetch their name immediately.
        bool refreshProcessNames = (++_processNameRefreshCycle & 3) == 0;
        int reusedProcessNames = 0;
        int nativeProcessNames = 0;
        double totalCpuSeconds = 0;
        double topCpuSeconds = 0;
        double oneDriveCpuSeconds = 0;
        double dropboxCpuSeconds = 0;
        double epicCpuSeconds = 0;
        double qbtCpuSeconds = 0;
        double searchIndexerCpuSeconds = 0;
        double searchProtocolCpuSeconds = 0;
        double searchFilterCpuSeconds = 0;
        // Unknown CPU counter evidence must not masquerade as measured 0%.
        bool oneDriveCpuComparable = false, dropboxCpuComparable = false;
        bool epicCpuComparable = false;
        bool qbtCpuComparable = false;
        bool searchIndexerCpuComparable = false, searchProtocolCpuComparable = false;
        bool searchFilterCpuComparable = false;
        int oneDriveInstanceCount = 0;
        int dropboxInstanceCount = 0;
        int epicInstanceCount = 0;
        int epicPid = 0;
        int qbtInstanceCount = 0;
        int qbtPid = 0;
        int searchIndexerInstanceCount = 0;
        int searchProtocolInstanceCount = 0;
        int searchFilterInstanceCount = 0;
        int oneDrivePid = 0, dropboxPid = 0;
        int searchIndexerPid = 0, searchProtocolPid = 0, searchFilterPid = 0;
        // Capture existing Process wrappers without extra enumeration. Read
        // WorkingSet64 only AFTER unique-instance counts are known, BEFORE
        // their existing snapshot-wide DisposeAll() cleanup.
        Process? oneDriveMemoryTarget = null, dropboxMemoryTarget = null;
        Process? epicMemoryTarget = null;
        Process? qbtMemoryTarget = null;
        Process? searchIndexerMemoryTarget = null;
        Process? searchProtocolMemoryTarget = null, searchFilterMemoryTarget = null;
        bool oneDriveMemoryReusable = false, dropboxMemoryReusable = false;
        bool epicMemoryReusable = false;
        bool qbtMemoryReusable = false;
        bool searchIndexerMemoryReusable = false;
        bool searchProtocolMemoryReusable = false, searchFilterMemoryReusable = false;
        int duplicateWorkingSetCandidates = 0;
        double? oneDriveWorkingSetMiB = null, dropboxWorkingSetMiB = null;
        double? epicWorkingSetMiB = null;
        double? qbtWorkingSetMiB = null;
        double? searchIndexerWorkingSetMiB = null;
        double? searchProtocolWorkingSetMiB = null, searchFilterWorkingSetMiB = null;
        string topProcess = string.Empty;
        bool gameActive = false;

        // Process.GetProcesses returns native process wrappers for the entire
        // snapshot. Dispose ALL of them even if a read or future cancellation
        // interrupts iteration before the last entry is visited.
        Process[] processes = Process.GetProcesses();
        try
        {
            foreach (Process process in processes)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    int processId = process.Id;
                    // A long sleep or invalid monotonic window needs a fresh
                    // baseline, not a fabricated process CPU delta. Avoid even
                    // looking up old PID/name state on this recovery scan.
                    AdaptiveProcessCpuSample previous = default;
                    bool previouslySeen = comparableCpuWindow &&
                        _processCpuHistory.TryGetPrevious(processId, out previous);
                    // Native CPU access is denied for many protected Windows
                    // processes. Resolve names FIRST so denied processes can
                    // still be classified as games and foreground immediately.
                    // For previously measured PIDs, reuse the name except
                    // on foreground/periodic refresh. CPU resets below force
                    // an immediate fresh name resolution (PID recycle).
                    bool reuseName = processId != foregroundPid && previouslySeen &&
                        !refreshProcessNames &&
                        !string.IsNullOrEmpty(previous.Name);
                    string name = reuseName ? previous.Name : process.ProcessName;
                    TimeSpan cpu = default;
                    bool cpuKnown = false;
                    if (!_cpuDeniedCache.ShouldSkip(processId, name, foregroundPid))
                    {
                        nativeCpuReadAttempts++;
                        try
                        {
                            cpu = process.TotalProcessorTime;
                            cpuKnown = true;
                        }
                        catch (System.ComponentModel.Win32Exception ex)
                        {
                            // Only access-denied(5) may be negatively cached.
                            // Other Win32 failures remain UNKNOWN, not zero.
                            if (ex.NativeErrorCode == 5)
                                _cpuDeniedCache.RecordDenied(processId, name, foregroundPid);
                        }
                        catch (InvalidOperationException) { /* exited process: unknown CPU */ }
                        catch (NotSupportedException) { /* unavailable CPU property */ }
                    }
                    // Preserve strict PID/name identity even when the native
                    // CPU read fails. ProcessName is much cheaper than denied
                    // CPU queries and remains readable for most system PIDs.
                    if (!cpuKnown && reuseName)
                    {
                        name = process.ProcessName;
                        reuseName = false;
                    }
                    // Preserve the old strict PID/CPU-counter identity logic:
                    // a decreasing CPU counter makes cached name unsafe.
                    if (cpuKnown && reuseName && cpu < previous.Cpu)
                    {
                        name = process.ProcessName;
                        reuseName = false;
                    }
                    bool canReuseMemory = AiProcessCpuEvidencePolicy.HasComparableCounter(
                        cpuKnown, previouslySeen, previous.Cpu, cpu, elapsed, logical);
                    if (canReuseMemory)
                        comparableCpuCounters++;
                    else if (cpuKnown && previouslySeen)
                        invalidCpuCounterDeltas++;
                    if (reuseName) reusedProcessNames++;
                    else nativeProcessNames++;
                    if (foregroundPid != 0 && processId == foregroundPid)
                    {
                        foregroundFromSnapshot = name;
                        foregroundFound = true;
                        foregroundCpuComparable = canReuseMemory;
                    }
                    if (AiKnownGameProcessPolicy.IsKnownGame(name))
                    {
                        gameActive = true;
                    }
                    if (cpuKnown)
                        _processCpuHistory.Remember(processId, cpu, name);
                    if (name.Equals("OneDrive", StringComparison.OrdinalIgnoreCase))
                    {
                        oneDriveInstanceCount++;
                        oneDrivePid = processId;
                        oneDriveCpuComparable = canReuseMemory;
                        oneDriveMemoryTarget = oneDriveInstanceCount == 1 ? process : null;
                        oneDriveMemoryReusable = canReuseMemory;
                    }
                    else if (name.Equals("Dropbox", StringComparison.OrdinalIgnoreCase))
                    {
                        dropboxInstanceCount++;
                        dropboxPid = processId;
                        dropboxCpuComparable = canReuseMemory;
                        dropboxMemoryTarget = dropboxInstanceCount == 1 ? process : null;
                        dropboxMemoryReusable = canReuseMemory;
                    }
                    else if (name.Equals("EpicGamesLauncher", StringComparison.OrdinalIgnoreCase))
                    {
                        epicInstanceCount++;
                        epicPid = processId;
                        epicCpuComparable = canReuseMemory;
                        epicMemoryTarget = epicInstanceCount == 1 ? process : null;
                        epicMemoryReusable = canReuseMemory;
                    }
                    else if (name.Equals("qbittorrent", StringComparison.OrdinalIgnoreCase))
                    {
                        qbtInstanceCount++;
                        qbtPid = processId;
                        qbtCpuComparable = canReuseMemory;
                        qbtMemoryTarget = qbtInstanceCount == 1 ? process : null;
                        qbtMemoryReusable = canReuseMemory;
                    }
                    else if (name.Equals("SearchIndexer", StringComparison.OrdinalIgnoreCase))
                    {
                        searchIndexerInstanceCount++;
                        searchIndexerPid = processId;
                        searchIndexerCpuComparable = canReuseMemory;
                        searchIndexerMemoryTarget = searchIndexerInstanceCount == 1 ? process : null;
                        searchIndexerMemoryReusable = canReuseMemory;
                    }
                    else if (name.Equals("SearchProtocolHost", StringComparison.OrdinalIgnoreCase))
                    {
                        searchProtocolInstanceCount++;
                        searchProtocolPid = processId;
                        searchProtocolCpuComparable = canReuseMemory;
                        searchProtocolMemoryTarget = searchProtocolInstanceCount == 1 ? process : null;
                        searchProtocolMemoryReusable = canReuseMemory;
                    }
                    else if (name.Equals("SearchFilterHost", StringComparison.OrdinalIgnoreCase))
                    {
                        searchFilterInstanceCount++;
                        searchFilterPid = processId;
                        searchFilterCpuComparable = canReuseMemory;
                        searchFilterMemoryTarget = searchFilterInstanceCount == 1 ? process : null;
                        searchFilterMemoryReusable = canReuseMemory;
                    }

                    if (canReuseMemory)
                    {
                        double delta = (cpu - previous.Cpu).TotalSeconds;
                        if (delta > 0)
                        {
                            totalCpuSeconds += delta;
                            // Reuse this very same native CPU-delta snapshot.
                            // The target may cease to be the top CPU process
                            // *because* its scheduling priority was lowered.
                            if (name.Equals("OneDrive", StringComparison.OrdinalIgnoreCase))
                                oneDriveCpuSeconds += delta;
                            else if (name.Equals("Dropbox", StringComparison.OrdinalIgnoreCase))
                                dropboxCpuSeconds += delta;
                            else if (name.Equals("EpicGamesLauncher", StringComparison.OrdinalIgnoreCase))
                                epicCpuSeconds += delta;
                            else if (name.Equals("qbittorrent", StringComparison.OrdinalIgnoreCase))
                                qbtCpuSeconds += delta;
                            else if (name.Equals("SearchIndexer", StringComparison.OrdinalIgnoreCase))
                                searchIndexerCpuSeconds += delta;
                            else if (name.Equals("SearchProtocolHost", StringComparison.OrdinalIgnoreCase))
                                searchProtocolCpuSeconds += delta;
                            else if (name.Equals("SearchFilterHost", StringComparison.OrdinalIgnoreCase))
                                searchFilterCpuSeconds += delta;
                            if (delta > topCpuSeconds)
                            {
                                topCpuSeconds = delta;
                                topProcess = name;
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or
                    System.ComponentModel.Win32Exception or NotSupportedException or ArgumentException)
                {
                    // Processes can exit or deny access during enumeration.
                }
            }
            // No working-set query (not even cache access) for any name with
            // two or more instances: those samples are NEVER AI-eligible.
            duplicateWorkingSetCandidates =
                (oneDriveInstanceCount > 1 ? oneDriveInstanceCount : 0) +
                (dropboxInstanceCount > 1 ? dropboxInstanceCount : 0) +
                (epicInstanceCount > 1 ? epicInstanceCount : 0) +
                (qbtInstanceCount > 1 ? qbtInstanceCount : 0) +
                (searchIndexerInstanceCount > 1 ? searchIndexerInstanceCount : 0) +
                (searchProtocolInstanceCount > 1 ? searchProtocolInstanceCount : 0) +
                (searchFilterInstanceCount > 1 ? searchFilterInstanceCount : 0);
            if (AiWorkingSetSamplingPolicy.MayReadUniqueInstance(oneDriveInstanceCount) &&
                oneDriveMemoryTarget is not null)
                oneDriveWorkingSetMiB = ReadCachedWorkingSetMiB(oneDriveMemoryTarget,
                    refreshWorkingSet, oneDriveMemoryReusable,
                    ref nativeWorkingSetReads, ref cachedWorkingSetReads);
            if (AiWorkingSetSamplingPolicy.MayReadUniqueInstance(dropboxInstanceCount) &&
                dropboxMemoryTarget is not null)
                dropboxWorkingSetMiB = ReadCachedWorkingSetMiB(dropboxMemoryTarget,
                    refreshWorkingSet, dropboxMemoryReusable,
                    ref nativeWorkingSetReads, ref cachedWorkingSetReads);
            if (AiWorkingSetSamplingPolicy.MayReadUniqueInstance(epicInstanceCount) &&
                epicMemoryTarget is not null)
                epicWorkingSetMiB = ReadCachedWorkingSetMiB(epicMemoryTarget,
                    refreshWorkingSet, epicMemoryReusable,
                    ref nativeWorkingSetReads, ref cachedWorkingSetReads);
            if (AiWorkingSetSamplingPolicy.MayReadUniqueInstance(qbtInstanceCount) &&
                qbtMemoryTarget is not null)
                qbtWorkingSetMiB = ReadCachedWorkingSetMiB(qbtMemoryTarget,
                    refreshWorkingSet, qbtMemoryReusable,
                    ref nativeWorkingSetReads, ref cachedWorkingSetReads);
            if (AiWorkingSetSamplingPolicy.MayReadUniqueInstance(searchIndexerInstanceCount) &&
                searchIndexerMemoryTarget is not null)
                searchIndexerWorkingSetMiB = ReadCachedWorkingSetMiB(searchIndexerMemoryTarget,
                    refreshWorkingSet, searchIndexerMemoryReusable,
                    ref nativeWorkingSetReads, ref cachedWorkingSetReads);
            if (AiWorkingSetSamplingPolicy.MayReadUniqueInstance(searchProtocolInstanceCount) &&
                searchProtocolMemoryTarget is not null)
                searchProtocolWorkingSetMiB = ReadCachedWorkingSetMiB(searchProtocolMemoryTarget,
                    refreshWorkingSet, searchProtocolMemoryReusable,
                    ref nativeWorkingSetReads, ref cachedWorkingSetReads);
            if (AiWorkingSetSamplingPolicy.MayReadUniqueInstance(searchFilterInstanceCount) &&
                searchFilterMemoryTarget is not null)
                searchFilterWorkingSetMiB = ReadCachedWorkingSetMiB(searchFilterMemoryTarget,
                    refreshWorkingSet, searchFilterMemoryReusable,
                    ref nativeWorkingSetReads, ref cachedWorkingSetReads);
        }
        finally
        {
            ProcessSnapshotCleanup.DisposeAll(processes);
        }

        _processCpuHistory.EndCycle();
        if (refreshWorkingSet)
            _lastWorkingSetReadUtc = now;
        _previousProcessSampleTimestamp = sampleStart;

        double totalPercent = Math.Clamp(
            totalCpuSeconds / elapsed * 100d / logical,
            0d,
            100d);

        double topPercent = Math.Clamp(
            topCpuSeconds / elapsed * 100d / logical,
            0d,
            100d);

        string foregroundProcess = foregroundFound
            ? foregroundFromSnapshot
            : ForegroundProcessProbe.GetName();

        _cachedProcessSample = new ProcessLoadSample(
            totalPercent,
            topProcess,
            topPercent,
            foregroundProcess,
            gameActive,
            AiProcessCpuEvidencePolicy.Percent(
                oneDriveInstanceCount, oneDriveCpuComparable, oneDriveCpuSeconds, elapsed, logical),
            AiProcessCpuEvidencePolicy.Percent(
                dropboxInstanceCount, dropboxCpuComparable, dropboxCpuSeconds, elapsed, logical),
            oneDriveInstanceCount,
            dropboxInstanceCount,
            AiProcessCpuEvidencePolicy.Percent(
                searchIndexerInstanceCount, searchIndexerCpuComparable, searchIndexerCpuSeconds, elapsed, logical),
            AiProcessCpuEvidencePolicy.Percent(
                searchProtocolInstanceCount, searchProtocolCpuComparable, searchProtocolCpuSeconds, elapsed, logical),
            AiProcessCpuEvidencePolicy.Percent(
                searchFilterInstanceCount, searchFilterCpuComparable, searchFilterCpuSeconds, elapsed, logical),
            searchIndexerInstanceCount,
            searchProtocolInstanceCount,
            searchFilterInstanceCount,
            foregroundPid,
            oneDriveInstanceCount == 1 ? oneDrivePid : 0,
            dropboxInstanceCount == 1 ? dropboxPid : 0,
            searchIndexerInstanceCount == 1 ? searchIndexerPid : 0,
            searchProtocolInstanceCount == 1 ? searchProtocolPid : 0,
            searchFilterInstanceCount == 1 ? searchFilterPid : 0,
            oneDriveInstanceCount == 1 ? oneDriveWorkingSetMiB : null,
            dropboxInstanceCount == 1 ? dropboxWorkingSetMiB : null,
            searchIndexerInstanceCount == 1 ? searchIndexerWorkingSetMiB : null,
            searchProtocolInstanceCount == 1 ? searchProtocolWorkingSetMiB : null,
            searchFilterInstanceCount == 1 ? searchFilterWorkingSetMiB : null,
            _lastWorkingSetReadUtc,
            AiProcessCpuEvidencePolicy.HasRepresentativeForegroundCoverage(
                comparableCpuCounters, processes.Length, foregroundCpuComparable),
            AiProcessCpuEvidencePolicy.Percent(
                epicInstanceCount, epicCpuComparable, epicCpuSeconds, elapsed, logical),
            epicInstanceCount,
            epicInstanceCount == 1 ? epicPid : 0,
            epicInstanceCount == 1 ? epicWorkingSetMiB : null,
            AiProcessCpuEvidencePolicy.Percent(
                qbtInstanceCount, qbtCpuComparable, qbtCpuSeconds, elapsed, logical),
            qbtInstanceCount,
            qbtInstanceCount == 1 ? qbtPid : 0,
            qbtInstanceCount == 1 ? qbtWorkingSetMiB : null);
        // A baseline-only scan still updates process identities, but must not
        // authorize A/B/A trials, priority mutations, or repeated workload votes.
        _lastProcessSampleUtc = comparableCpuWindow && comparableCpuCounters > 0
            ? now : default;
        // Every successful native snapshot contributes once. Report one
        // bounded, allocation-free aggregate per minute; never add a timer.
        // Unlike a single arbitrary sample, maxMs reveals periodic stalls.
        double thisSampleMs = Stopwatch.GetElapsedTime(sampleStart).TotalMilliseconds;
        long thisSampleAllocatedB =
            GC.GetAllocatedBytesForCurrentThread() - allocatedAtStart;
        _sampleMetrics.Add(thisSampleMs, thisSampleAllocatedB, processes.Length,
            nativeProcessNames, reusedProcessNames,
            nativeWorkingSetReads, cachedWorkingSetReads, refreshWorkingSet,
            duplicateWorkingSetCandidates,
            nativeCpuReadAttempts, _cpuDeniedCache.AccessDeniedThisCycle,
            _cpuDeniedCache.SuppressedThisCycle);
        if (metricDue)
        {
            _lastProcessSampleMetricUtc = now;
            AdaptiveProcessSampleMetricWindow.Snapshot minute =
                _sampleMetrics.TakeAndReset();
            int reusedSnapshots = _processSnapshotCacheReuses;
            _processSnapshotCacheReuses = 0;
            ownMetrics!.Refresh();
            long currentCpuTicks = ownMetrics.TotalProcessorTime.Ticks;
            double cpuDuringThisSampleMs = TimeSpan.FromTicks(
                Math.Max(0, currentCpuTicks - cpuBeforeTicks)).TotalMilliseconds;
            double cpuSinceLastMetricMs = _lastMetricProcessCpuTicks < 0
                ? -1
                : TimeSpan.FromTicks(
                    Math.Max(0, currentCpuTicks - _lastMetricProcessCpuTicks))
                    .TotalMilliseconds;
            _lastMetricProcessCpuTicks = currentCpuTicks;
            long processRamMiB = ownMetrics.WorkingSet64 / 1048576;
            int processHandles = ownMetrics.HandleCount;
            int processThreads = ownMetrics.Threads.Count;
            // Same already cached status, no extra service/file writes or
            // WinUI polling. Visible read-only metrics use the LAST minute
            // aggregate, never a fake single "average" sample.
            _lastSamplerSummary =
                $"Sběr procesů {minute.Scans}× (cache {reusedSnapshots}×): průměr {minute.MeanMs:0.##} ms, " +
                $"max {minute.MaxMs:0.##} ms; alokace průměr " +
                $"{minute.MeanAllocatedB / 1024:0.#} KiB, max " +
                $"{minute.MaxAllocatedB / 1024d:0.#} KiB; " +
                $"CPU procesu za interval " +
                $"{(cpuSinceLastMetricMs < 0 ? "—" : cpuSinceLastMetricMs.ToString("0.#") + " ms")}; " +
                $"RAM {processRamMiB} MiB, handly {processHandles}, vlákna {processThreads}; " +
                $"CPU okno {(comparableCpuWindow ? "ověřené" : "nový základ")}, " +
                $"pokrytí {( _cachedProcessSample.HasRepresentativeCpuCoverage ? "dostatečné" : "neúplné")}; " +
                $"CPU čtení {minute.NativeCpuReadAttempts}, " +
                $"odmítnuto {minute.CpuAccessDenied}, " +
                $"ušetřeno {minute.SuppressedDeniedCpuReads}";
            LogService.Info("performance",
                $"ADAPTIVE_PROCESS_SAMPLE scans={minute.Scans} reusedSnapshots={reusedSnapshots} " +
                $"avgMs={minute.MeanMs:0.###} minMs={minute.MinMs:0.###} " +
                $"maxMs={minute.MaxMs:0.###} ms={thisSampleMs:0.###} " +
                $"avgAllocatedB={minute.MeanAllocatedB:0.#} peakAllocatedB={minute.MaxAllocatedB} " +
                $"allocatedB={thisSampleAllocatedB} " +
                $"avgProcesses={minute.MeanProcesses:0.#} peakProcesses={minute.MaxProcesses} " +
                $"nameReuse={minute.ReusedNames} nativeNames={minute.NativeNameReads} " +
                $"workingSetNativeReads={minute.NativeWorkingSetReads} " +
                $"workingSetCacheHits={minute.WorkingSetCacheHits} " +
                $"workingSetRefreshes={minute.WorkingSetRefreshes} " +
                $"duplicateCandidatesSkipped={minute.DuplicateCandidatesSkipped} " +
                $"comparableCpuCounters={comparableCpuCounters} totalProcesses={processes.Length} " +
                $"foregroundCpuComparable={foregroundCpuComparable} " +
                $"representativeNativeCpu={_cachedProcessSample.HasRepresentativeCpuCoverage} " +
                $"invalidCpuDeltas={invalidCpuCounterDeltas} " +
                $"nativeCpuReads={minute.NativeCpuReadAttempts} " +
                $"nativeCpuAccessDenied={minute.CpuAccessDenied} " +
                $"nativeCpuDeniedSkipped={minute.SuppressedDeniedCpuReads} " +
                $"selfCpuDeltaMs={cpuDuringThisSampleMs:0.##} " +
                $"selfCpuIntervalMs={cpuSinceLastMetricMs:0.##} " +
                $"selfWorkingSetMiB={processRamMiB} " +
                $"selfHandles={processHandles} selfThreads={processThreads} " +
                $"foregroundSnapshot={foregroundFound} cpuWindowComparable={comparableCpuWindow} " +
                $"cpuWindowSeconds={elapsed:0.###} cpuPercent={totalPercent:0.0}");
        }
        return _cachedProcessSample;
    }
}
