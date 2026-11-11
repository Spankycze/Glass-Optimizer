using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public static partial class TweakFactory
{
    // One transaction boundary for all remaining generic DWORD toggles.
    // Callbacks permit pure fault-injection tests without touching HKLM/HKCU.
    internal static async Task ExecuteDwordMutationAsync(
        bool enable,
        Func<CancellationToken, Task> captureOriginal,
        Func<CancellationToken, Task> applyAndVerify,
        Func<CancellationToken, Task<bool>> restoreOriginal,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!enable)
        {
            // In particular: NEVER call captureOriginal on OFF. A snapshot
            // captured after the tweak is already ON is not an original state.
            if (!await restoreOriginal(CancellationToken.None).ConfigureAwait(false))
                throw new InvalidOperationException(
                    "Původní hodnota tohoto registry tweaku není uložená; " +
                    "není bezpečné použít náhradní hodnotu.");
            return;
        }

        await captureOriginal(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        try
        {
            await applyAndVerify(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception mutationFailure)
        {
            try
            {
                // Cancellation must not suppress best-effort rollback of a
                // potentially partially written registry value.
                if (!await restoreOriginal(CancellationToken.None).ConfigureAwait(false))
                    throw new InvalidOperationException(
                        "Nelze bezpečně vrátit původní hodnotu po neúspěšném zápisu.");
            }
            catch (Exception rollbackFailure)
            {
                throw new AggregateException(
                    "Zápis DWORD i obnova původního nastavení selhaly.",
                    mutationFailure, rollbackFailure);
            }
            throw;
        }
    }
}
