using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

public static partial class NativeSystemProbeService
{
    private static Dictionary<uint, double?> ReadStorageTemperaturesFresh(int generation)
    {
        Interlocked.Increment(ref _storageTemperatureFreshLoadCount);
        var map = new Dictionary<uint, double?>();
        ManagementScope? scope = null;
        try
        {
            lock (StorageScopeLock)
            {
                scope = _storageReliabilityScope;
                if (scope == null || !scope.IsConnected)
                {
                    scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
                    scope.Connect();
                    _storageReliabilityScope = scope;
                }
            }

            using var q = new ManagementObjectSearcher(
                scope,
                new ObjectQuery("SELECT DeviceId,Temperature FROM MSFT_StorageReliabilityCounter"),
                QueryOptions());

            var results = q.Get();
            WmiEnumerationScope.ForEach<ManagementObject>(results, results,
                CancellationToken.None, counter =>
            {
                if (counter["DeviceId"] is null) return;
                if (!uint.TryParse(counter["DeviceId"]?.ToString(), out uint deviceId)) return;
                double? temp = counter["Temperature"] is null
                    ? null
                    : Convert.ToDouble(counter["Temperature"]);
                map[deviceId] = temp;
            });

            if (generation == Volatile.Read(ref _storageTemperatureGeneration))
            {
                var samples = new DiskTemperatureSample[map.Count];
                int sampleIndex = 0;
                foreach (var item in map)
                    samples[sampleIndex++] = new DiskTemperatureSample(item.Key, item.Value);
                PersistentHardwareCacheService.WriteMachineStable(
                    PersistentDiskTemperatureKey,
                    samples);
            }
        }
        catch (Exception ex)
        {
            lock (StorageScopeLock)
            {
                if (ReferenceEquals(_storageReliabilityScope, scope))
                    _storageReliabilityScope = null;
            }
            LogService.Error("storage", "Storage temperature probe failed", ex);
        }
        return map;
    }

    internal static async Task RunStorageTemperatureStaleCacheSmokeAsync(CancellationToken token)
    {
        InvalidateStorageCache(preserveStableDriverMetadata: true);
        _ = await Task.Run(() => ReadStorageTemperaturesFresh(Volatile.Read(ref _storageTemperatureGeneration)), token).ConfigureAwait(false);
        var fresh = await Task.Run(ReadStorage, token).ConfigureAwait(false);
        if (fresh.Count == 0)
            throw new InvalidOperationException("Storage temperature stale-cache smoke could not establish a fresh disk snapshot.");

        int freshLoadsBeforePreserve = Volatile.Read(ref _storageTemperatureFreshLoadCount);
        InvalidateStorageCache(preserveStableDriverMetadata: true, invalidateTemperatures: false);
        var preserveSw = System.Diagnostics.Stopwatch.StartNew();
        var preserved = await Task.Run(ReadStorage, token).ConfigureAwait(false);
        long preserveReadMs = preserveSw.ElapsedMilliseconds;
        int freshLoadsAfterPreserve = Volatile.Read(ref _storageTemperatureFreshLoadCount);
        if (preserved.Count != fresh.Count || freshLoadsAfterPreserve != freshLoadsBeforePreserve)
            throw new InvalidOperationException(
                $"Storage temperature preserve invalidation failed. disks={preserved.Count}/{fresh.Count} freshLoads={freshLoadsBeforePreserve}->{freshLoadsAfterPreserve}.");

        if (!PersistentHardwareCacheService.BackdateForSmoke(PersistentDiskTemperatureKey, TimeSpan.FromMinutes(5)))
            throw new InvalidOperationException("Storage temperature stale-cache smoke could not backdate the persistent temperature cache.");

        lock (ProbeCacheLock)
        {
            _storageProbeCache = null;
            _storageProbeAt = default;
        }

        int staleBefore = Volatile.Read(ref _storageTemperatureStaleHitCount);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var staleRead = await Task.Run(ReadStorage, token).ConfigureAwait(false);
        long staleReadMs = sw.ElapsedMilliseconds;
        int staleAfter = Volatile.Read(ref _storageTemperatureStaleHitCount);
        if (staleRead.Count == 0 || staleAfter != staleBefore + 1)
            throw new InvalidOperationException($"Storage temperature stale-cache path was not used. before={staleBefore} after={staleAfter} count={staleRead.Count}.");

        Task? refresh;
        lock (StorageTemperatureRefreshLock) refresh = _storageTemperatureRefreshTask;
        if (refresh != null)
            await refresh.WaitAsync(token).ConfigureAwait(false);

        lock (ProbeCacheLock)
        {
            _storageProbeCache = null;
            _storageProbeAt = default;
        }

        int staleBeforeFreshRead = Volatile.Read(ref _storageTemperatureStaleHitCount);
        var refreshed = await Task.Run(ReadStorage, token).ConfigureAwait(false);
        int staleAfterFreshRead = Volatile.Read(ref _storageTemperatureStaleHitCount);
        if (refreshed.Count == 0 || staleAfterFreshRead != staleBeforeFreshRead)
            throw new InvalidOperationException("Storage temperature background refresh did not restore a fresh persistent cache.");

        LogService.Info("performance",
            $"STORAGE_TEMPERATURE_STALE_CACHE_SMOKE_OK disks={fresh.Count} preserveReadMs={preserveReadMs} staleReadMs={staleReadMs} staleHits={staleAfter - staleBefore}");
    }

    private static Dictionary<string, (string Provider, string Version)> ReadDiskDrivers()
    {
        lock (ProbeCacheLock)
            if (_diskDriverCache != null && DateTime.UtcNow - _diskDriverCacheAt < TimeSpan.FromMinutes(10))
                return _diskDriverCache;

        if (PersistentHardwareCacheService.TryReadMachineStable<DiskDriverMetadata[]>(
                PersistentDiskDriverKey, PersistentDiskDriverTtl, out var persisted) &&
            persisted is { Length: > 0 })
        {
            var persistedMap = persisted
                .Where(x => !string.IsNullOrWhiteSpace(x.DeviceId))
                .GroupBy(x => x.DeviceId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => (g.First().Provider ?? string.Empty, g.First().Version ?? string.Empty),
                    StringComparer.OrdinalIgnoreCase);

            if (persistedMap.Count > 0)
            {
                lock (ProbeCacheLock)
                {
                    _diskDriverCache = persistedMap;
                    _diskDriverCacheAt = DateTime.UtcNow;
                }
                LogService.Info("performance", $"STORAGE_DRIVER_PERSISTENT_HIT count={persistedMap.Count}");
                return persistedMap;
            }
        }

        var map = new Dictionary<string, (string Provider, string Version)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var q = new ManagementObjectSearcher(
                GetStorageDriverScope(),
                new ObjectQuery("SELECT DeviceID,DriverProviderName,DriverVersion FROM Win32_PnPSignedDriver WHERE DeviceClass='DISKDRIVE'"),
                QueryOptions());
            var results = q.Get();
            WmiEnumerationScope.ForEach<ManagementObject>(results, results,
                CancellationToken.None, x =>
            {
                string deviceId = x["DeviceID"]?.ToString() ?? string.Empty;
                if (deviceId.Length > 0)
                    map[deviceId] = (
                        x["DriverProviderName"]?.ToString() ?? string.Empty,
                        x["DriverVersion"]?.ToString() ?? string.Empty);
            });
        }
        catch (Exception ex)
        {
            LogService.Error("storage", "Disk driver metadata probe failed", ex);
        }

        if (map.Count > 0)
        {
            PersistentHardwareCacheService.WriteMachineStable(
                PersistentDiskDriverKey,
                map.Select(x => new DiskDriverMetadata(x.Key, x.Value.Provider, x.Value.Version)).ToArray());
        }

        lock (ProbeCacheLock)
        {
            _diskDriverCache = map;
            _diskDriverCacheAt = DateTime.UtcNow;
        }
        return map;
    }
    internal static bool ReadTrimStateCached() => ReadTrimState();

    private static bool ReadTrimState()
    {
        lock (ProbeCacheLock) if (_trimCacheAt != default && DateTime.UtcNow - _trimCacheAt < TimeSpan.FromMinutes(5)) return _trimCache;

        if (PersistentHardwareCacheService.TryReadMachineStable<TrimPersistentState>(
                PersistentTrimKey, PersistentTrimTtl, out var persisted) &&
            persisted != null)
        {
            lock (ProbeCacheLock) { _trimCache = persisted.Enabled; _trimCacheAt = DateTime.UtcNow; }
            LogService.Info("performance", $"STORAGE_TRIM_PERSISTENT_HIT enabled={persisted.Enabled}");
            return persisted.Enabled;
        }

        try
        {
            using (RegistryKey? fileSystem = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\FileSystem"))
            {
                object? raw = fileSystem?.GetValue("NtfsDisableDeleteNotify");
                if (raw != null)
                {
                    bool registryValue = Convert.ToInt32(raw) == 0;
                    lock (ProbeCacheLock) { _trimCache = registryValue; _trimCacheAt = DateTime.UtcNow; }
                    PersistentHardwareCacheService.WriteMachineStable(PersistentTrimKey, new TrimPersistentState(registryValue));
                    return registryValue;
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Info("storage", "TRIM registry fast-path unavailable: " + ex.Message);
        }

        try
        {
            using var p = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo("fsutil.exe", "behavior query DisableDeleteNotify")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            var outputBuffer = new StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (outputBuffer) outputBuffer.AppendLine(e.Data); };
            // Drain stderr even when not used, to avoid blocking the child process.
            p.ErrorDataReceived += (_, _) => { };
            if (!p.Start())
            {
                LogService.Error("storage", "TRIM state probe failed to start fsutil.exe");
                return false;
            }
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (!p.WaitForExit(3000))
            {
                try { p.Kill(true); } catch { }
                LogService.Error("storage", "TRIM state probe timed out", new System.TimeoutException("fsutil.exe behavior query DisableDeleteNotify exceeded 3 seconds."));
                return false;
            }
            p.WaitForExit(); // flush pending async output events after exit
            string output;
            lock (outputBuffer) output = outputBuffer.ToString();
            bool value = p.ExitCode == 0 && output.Contains("= 0", StringComparison.OrdinalIgnoreCase);
            lock (ProbeCacheLock) { _trimCache = value; _trimCacheAt = DateTime.UtcNow; }
            PersistentHardwareCacheService.WriteMachineStable(PersistentTrimKey, new TrimPersistentState(value));
            return value;
        }
        catch (Exception ex)
        {
            LogService.Error("storage", "TRIM state probe failed", ex);
            return false;
        }
    }

    public static (string Status, string Signer) ReadSignature(string path)
    {
        if (!File.Exists(path)) return ("NotFound", string.Empty);
        try
        {
            bool valid = VerifyAuthenticode(path);
#pragma warning disable SYSLIB0057
            // Release the intermediate native certificate handle as well.
            using var signedFileCertificate = X509Certificate.CreateFromSignedFile(path);
            using var cert = new X509Certificate2(signedFileCertificate);
#pragma warning restore SYSLIB0057
            return (valid ? "Valid" : "Invalid", cert.Subject ?? string.Empty);
        }
        catch { return ("NotSigned", string.Empty); }
    }

    public static WindowsUpdateCenterStatus ReadWindowsUpdateStatus()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        DateTime? pauseUntil = ReadPauseUntil();
        bool paused = pauseUntil.HasValue && pauseUntil.Value > DateTime.Now;
        long pauseMs = sw.ElapsedMilliseconds;

        bool reboot;
        using (var cbs = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
        using (var wu = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
            reboot = cbs != null || wu != null;
        long rebootMs = sw.ElapsedMilliseconds - pauseMs;

        string services = string.Join("; ", new[] { "wuauserv", "bits", "cryptsvc", "usosvc" }.Select(ServiceState));
        long servicesMs = sw.ElapsedMilliseconds - pauseMs - rebootMs;

        var errors = ReadWindowsUpdateErrors();
        long errorsMs = sw.ElapsedMilliseconds - pauseMs - rebootMs - servicesMs;

        LogService.Info("performance",
            $"WINDOWS_UPDATE_STATUS_PHASE pauseMs={pauseMs} rebootMs={rebootMs} servicesMs={servicesMs} errorsMs={errorsMs} totalMs={sw.ElapsedMilliseconds}");
        return new WindowsUpdateCenterStatus(reboot, paused, pauseUntil, services, errors.Count, errors.LastError);
    }
    private static DateTime? ReadPauseUntil()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings");
            string? raw = key?.GetValue("PauseUpdatesExpiryTime")?.ToString();
            return DateTime.TryParse(raw, out var dt) ? dt.ToLocalTime() : null;
        }
        catch { return null; }
    }

    private static string ServiceState(string name)
    {
        try { using var s = new ServiceController(name); return name + "=" + s.Status; }
        catch { return name + "=Unknown"; }
    }

    private static (int Count, string LastError) ReadWindowsUpdateErrors()
    {
        int count = 0; string last = string.Empty;
        try
        {
            var query = new EventLogQuery("Microsoft-Windows-WindowsUpdateClient/Operational", PathType.LogName,
                "*[System[(Level=2)]]")
            { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            for (int i = 0; i < 80; i++)
            {
                using var ev = reader.ReadEvent(); if (ev == null) break;
                count++; if (last.Length == 0) last = ev.FormatDescription() ?? string.Empty;
            }
        }
        catch { }
        return (count, last);
    }

    public static IReadOnlyList<AvailableDriverUpdate> ReadAvailableDriverUpdates()
    {
        var list = new List<AvailableDriverUpdate>();
        Type? t = Type.GetTypeFromProgID("Microsoft.Update.Session");
        if (t == null) return list;
        dynamic? session = null;
        try
        {
            session = Activator.CreateInstance(t);
            dynamic searcher = session.CreateUpdateSearcher();
            dynamic result = searcher.Search("IsInstalled=0 and Type='Driver'");
            for (int i = 0; i < result.Updates.Count; i++)
            {
                dynamic u = result.Updates.Item(i);
                list.Add(new AvailableDriverUpdate((string)u.Title, SafeDynamic(u, "DriverClass"), SafeDynamic(u, "DriverManufacturer"), SafeDynamic(u, "DriverVerDate")));
            }
        }
        catch (Exception ex) { LogService.Error("Native driver update query failed", ex); }
        finally { if (session != null && System.Runtime.InteropServices.Marshal.IsComObject(session)) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(session); }
        return list;
    }
    private static string SafeDynamic(dynamic value, string member)
    {
        try
        {
            return member switch
            {
                "DriverClass" => Convert.ToString(value.DriverClass) ?? string.Empty,
                "DriverManufacturer" => Convert.ToString(value.DriverManufacturer) ?? string.Empty,
                "DriverVerDate" => Convert.ToString(value.DriverVerDate) ?? string.Empty,
                _ => string.Empty
            };
        }
        catch { return string.Empty; }
    }

    private static bool VerifyAuthenticode(string path)
    {
        IntPtr filePtr = IntPtr.Zero;
        try
        {
            var file = new WinTrustFileInfo
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                pcwszFilePath = path,
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero
            };
            filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(file, filePtr, false);
            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = 2,
                fdwRevocationChecks = 0,
                dwUnionChoice = 1,
                pFile = filePtr,
                dwStateAction = 0,
                dwProvFlags = 0x00000020,
                dwUIContext = 0
            };
            Guid action = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
        }
        finally
        {
            if (filePtr != IntPtr.Zero) Marshal.FreeHGlobal(filePtr);
        }
    }

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WinTrustData pWVTData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
    }

    private static ManagementScope GetStorageDiskScope()
    {
        lock (StorageDiskScopeLock)
        {
            if (_storageDiskScope == null || !_storageDiskScope.IsConnected)
            {
                _storageDiskScope = new ManagementScope(@"\\.\root\CIMV2");
                _storageDiskScope.Connect();
            }
            return _storageDiskScope;
        }
    }

    private static ManagementScope GetStorageDriverScope()
    {
        lock (StorageDriverScopeLock)
        {
            if (_storageDriverScope == null || !_storageDriverScope.IsConnected)
            {
                _storageDriverScope = new ManagementScope(@"\\.\root\CIMV2");
                _storageDriverScope.Connect();
            }
            return _storageDriverScope;
        }
    }

    private static System.Management.EnumerationOptions QueryOptions() => new()
    {
        Timeout = TimeSpan.FromSeconds(2),
        ReturnImmediately = false,
        Rewindable = false
    };

    private static string Wql(string s) => "'" + s.Replace("'", "''") + "'";
    private static double ToDouble(object? x) { try { return Convert.ToDouble(x ?? 0); } catch { return 0; } }
}
