using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    private const string ServiceStartReceiptVersion = "service-start-original-v1";
    private static readonly SemaphoreSlim ServiceStartTransactionGate = new(1, 1);

    private static string ServiceReceiptPath(string service) =>
        Path.Combine(LogService.Root, "service-start-originals-v1", service + ".txt");

    private static void ValidateServiceStartName(string service)
    {
        if (string.IsNullOrWhiteSpace(service) || service.Length > 80 ||
            !service.All(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-'))
            throw new ArgumentException("Neplatné jméno Windows služby.", nameof(service));
    }

    internal static string EncodeServiceStartReceipt(string service, int startType)
    {
        ValidateServiceStartName(service);
        ValidateStartType(startType);
        return ServiceStartReceiptVersion + "|" + service.ToUpperInvariant() + "|" + startType;
    }

    internal static int ParseServiceStartReceipt(string content, string service)
    {
        ValidateServiceStartName(service);
        string[] fields = content.TrimEnd('\r', '\n').Split('|');
        if (fields.Length != 3 ||
            fields[0] != ServiceStartReceiptVersion ||
            !string.Equals(fields[1], service, StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(fields[2], out int startType) ||
            startType is < 2 or > 4)
            throw new InvalidDataException(
                "Původní typ spuštění služby nemá platný a důvěryhodný záznam.");
        return startType;
    }

    private static void ValidateStartType(int startType)
    {
        // Start=0/1 is for boot/system drivers, not a discretionary Windows
        // service toggle. Protect these critical boot-time configurations.
        if (startType is < 2 or > 4)
            throw new InvalidOperationException(
                "Tento typ spuštění služby není bezpečné automaticky změnit.");
    }

    private static async Task<int> CaptureOrReadServiceOriginalAsync(
        string service, int observed, CancellationToken ct)
    {
        ValidateServiceStartName(service);
        ValidateStartType(observed);
        string path = ServiceReceiptPath(service);
        if (File.Exists(path))
            return ParseServiceStartReceipt(
                await File.ReadAllTextAsync(path, ct).ConfigureAwait(false), service);
        await AtomicFileService.WriteTextAsync(
            path, EncodeServiceStartReceipt(service, observed), ct).ConfigureAwait(false);
        return observed;
    }

    private static async Task<int> LoadServiceOriginalAsync(
        string service, CancellationToken ct)
    {
        ValidateServiceStartName(service);
        string path = ServiceReceiptPath(service);
        if (!File.Exists(path))
            throw new InvalidOperationException(
                "Původní typ spuštění služby není uložen; ruční stav nebude přepsán.");
        return ParseServiceStartReceipt(
            await File.ReadAllTextAsync(path, ct).ConfigureAwait(false), service);
    }

    // Executes the actual state machine against injectable service-control and
    // durable-backup operations, so tests never touch live Windows services.
    internal static async Task ExecuteServiceStartMutationAsync(
        bool disable,
        string service,
        Func<CancellationToken, Task<int?>> readStart,
        Func<int, CancellationToken, Task<int>> captureOrLoadOriginal,
        Func<CancellationToken, Task<int>> loadOriginal,
        Func<int, CancellationToken, Task> changeStart,
        Func<CancellationToken, Task> ensureMayDisable,
        CancellationToken ct)
    {
        ValidateServiceStartName(service);
        ct.ThrowIfCancellationRequested();

        static async Task<int> GetStartAsync(
            Func<CancellationToken, Task<int?>> reader, CancellationToken token)
        {
            int actual = await reader(token).ConfigureAwait(false) ??
                throw new InvalidOperationException("Služba již neexistuje.");
            ValidateStartType(actual);
            return actual;
        }

        if (!disable)
        {
            int original = await loadOriginal(ct).ConfigureAwait(false);
            ValidateStartType(original);
            int actual = await GetStartAsync(readStart, ct).ConfigureAwait(false);
            if (actual == original) return;
            if (actual != 4)
                throw new InvalidOperationException(
                    "Služba byla mezitím změněna mimo Optimizer; její stav nebude přepsán.");
            ct.ThrowIfCancellationRequested();
            await changeStart(original, ct).ConfigureAwait(false);
            int verified = await GetStartAsync(readStart, CancellationToken.None)
                .ConfigureAwait(false);
            if (verified != original)
                throw new IOException("SCM nepotvrdil obnovu původního Start=" + original);
            return;
        }

        await ensureMayDisable(ct).ConfigureAwait(false);
        int before = await GetStartAsync(readStart, ct).ConfigureAwait(false);
        int originalStart = await captureOrLoadOriginal(before, ct).ConfigureAwait(false);
        ValidateStartType(originalStart);
        if (before != originalStart && before != 4)
            throw new InvalidOperationException(
                "Start služby se oproti původní záloze změnil mimo Optimizer.");
        ct.ThrowIfCancellationRequested();
        if (before == 4) return;

        try
        {
            await changeStart(4, ct).ConfigureAwait(false);
            int actual = await GetStartAsync(readStart, ct).ConfigureAwait(false);
            if (actual != 4)
                throw new IOException("SCM nepotvrdil vypnutí služby.");
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception mutationError)
        {
            try
            {
                int current = await GetStartAsync(readStart, CancellationToken.None)
                    .ConfigureAwait(false);
                if (current != originalStart)
                {
                    if (current != 4)
                        throw new InvalidOperationException(
                            "Služba má cizí mezistav; automatický rollback je zablokován.");
                    await changeStart(originalStart, CancellationToken.None).ConfigureAwait(false);
                    int verified = await GetStartAsync(readStart, CancellationToken.None)
                        .ConfigureAwait(false);
                    if (verified != originalStart)
                        throw new IOException("SCM nepotvrdil rollback původního typu spuštění.");
                }
            }
            catch (Exception restoreError)
            {
                throw new AggregateException(
                    "Změna služby i návrat původního typu spuštění selhaly.",
                    mutationError, restoreError);
            }
            throw;
        }
    }

    private static async Task SetServiceDisabledAsync(
        string service, bool disabled, CancellationToken ct)
    {
        await ServiceStartTransactionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ExecuteServiceStartMutationAsync(
                disabled, service,
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    return Task.FromResult(ServiceSafetyService.ReadStartType(service));
                },
                (observed, token) =>
                    CaptureOrReadServiceOriginalAsync(service, observed, token),
                token => LoadServiceOriginalAsync(service, token),
                (desired, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    if (!NativeCore.ChangeServiceStartType(service, (uint)desired, out int error))
                        throw new IOException(
                            "SCM odmítl změnu Start služby " + service + ", error=" + error);
                    ServiceCatalogService.InvalidateCache();
                    return Task.CompletedTask;
                },
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    if (!ServiceSafetyService.CanDisable(service))
                        throw new InvalidOperationException(
                            ServiceSafetyService.ExplainBlock(service));
                    return Task.CompletedTask;
                }, ct).ConfigureAwait(false);
        }
        finally
        {
            ServiceStartTransactionGate.Release();
        }
    }

    private static TweakDefinition ServiceDisableTweak(
        string service, string label, TweakRisk risk)
    {
        return TweakFactory.Command(
            "gt.svc." + service, "components", "Služby", label + " — vypnout",
            "Volitelné vypnutí služby s ověřením SCM a obnovením původního typu spuštění.",
            ct => Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                return ServiceSafetyService.ReadStartType(service) == 4;
            }, ct),
            (on, ct) => SetServiceDisabledAsync(service, on, ct),
            false, risk, true, risk == TweakRisk.Advanced ? "Střední" : "Nízký",
            risk == TweakRisk.Advanced
                ? "Může omezit systémovou funkci. Vypnutí nastavení obnoví původní Start služby."
                : null,
            19041, "Any", true, true,
            revert: token => SetServiceDisabledAsync(service, false, token));
    }
}
