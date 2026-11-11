using System;
using System.Runtime.InteropServices;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Read-only spooler queue probe. Installed queues alone never mean a printer
/// is used; zero queued jobs never proves a queue will not be needed later.
/// Does not start Spooler, enumerate remote connections, or write configuration.
/// </summary>
internal readonly record struct AiPrinterQueueEvidence(
    bool Available, int LocalQueues, int PendingJobs);

internal static class AiOptionalPrinterUsageProbe
{
    private const uint PrinterEnumLocal = 0x00000002;
    private const uint PrinterInfoLevel = 2;
    private const int ErrorInsufficientBuffer = 122;
    private const int MaximumPrinterBufferBytes = 4 * 1024 * 1024;

    [StructLayout(LayoutKind.Sequential)]
    private struct PrinterInfo2
    {
        public IntPtr ServerName, PrinterName, ShareName, PortName, DriverName,
            Comment, Location, DevMode, SepFile, PrintProcessor, Datatype,
            Parameters, SecurityDescriptor;
        public uint Attributes, Priority, DefaultPriority, StartTime, UntilTime,
            Status, Jobs, AveragePagesPerMinute;
    }

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("winspool.drv", EntryPoint = "EnumPrintersW", SetLastError = true,
        CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumPrinters(uint flags, IntPtr name,
        uint level, IntPtr buffer, uint size, out uint needed, out uint returned);

    internal static AiOptionalFeatureUsage Classify(AiPrinterQueueEvidence evidence)
    {
        if (!evidence.Available || evidence.LocalQueues < 0 ||
            evidence.PendingJobs < 0 || (evidence.LocalQueues == 0 && evidence.PendingJobs > 0))
            return AiOptionalFeatureUsage.Unknown;
        return evidence.PendingJobs > 0
            ? AiOptionalFeatureUsage.InUse
            : AiOptionalFeatureUsage.CandidateForObservation;
    }

    internal static AiPrinterQueueEvidence Read(bool spoolerRunning)
    {
        if (!spoolerRunning)
            return default;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            bool first = EnumPrinters(PrinterEnumLocal, IntPtr.Zero, PrinterInfoLevel,
                IntPtr.Zero, 0, out uint needed, out uint emptyCount);
            if (first && needed == 0)
                return new(true, 0, 0);
            if (!first && Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer)
                return default;
            if (needed == 0 || needed > MaximumPrinterBufferBytes)
                return default;
            buffer = Marshal.AllocHGlobal((int)needed);
            if (!EnumPrinters(PrinterEnumLocal, IntPtr.Zero, PrinterInfoLevel,
                buffer, needed, out _, out uint count))
                return default;
            int stride = Marshal.SizeOf<PrinterInfo2>();
            if (count > needed / (uint)stride || count > int.MaxValue)
                return default;
            long jobs = 0;
            for (int i = 0; i < (int)count; i++)
            {
                var queue = Marshal.PtrToStructure<PrinterInfo2>(IntPtr.Add(buffer, i * stride));
                jobs += queue.Jobs;
                if (jobs > int.MaxValue)
                    return default;
            }
            return new(true, (int)count, (int)jobs);
        }
        catch (Exception)
        {
            return default;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(buffer);
        }
    }

    internal static string Describe(AiPrinterQueueEvidence evidence)
    {
        if (!evidence.Available)
            return "stav tiskových front nelze ověřit; nic neměnit";
        if (evidence.PendingJobs > 0)
            return $"tiskové úlohy čekají ({evidence.PendingJobs}); tisk zachovat";
        return evidence.LocalQueues > 0
            ? $"nakonfigurované tiskárny ({evidence.LocalQueues}), fronty jsou nyní prázdné; další využití neověřeno"
            : "žádná místní tisková fronta; budoucí využití neověřeno";
    }
}
