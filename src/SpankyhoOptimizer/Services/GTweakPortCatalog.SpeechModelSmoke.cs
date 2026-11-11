using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    // Calls production state machine using in-memory delegates only; never
    // opens HKLM and never changes the user's Windows speech settings.
    internal static async Task RunSpeechModelMutationSmokeAsync()
    {
        static void Check(bool ok, string scenario)
        {
            if (!ok) throw new InvalidOperationException(
                "SPEECH_MODEL_SMOKE_FAIL: " + scenario);
        }

        // 1. Public UI and registry semantics preserved.
        TweakDefinition tweak = SpeechModelTweak();
        Check(tweak.Id == SpeechModelId && tweak.Section == "privacy" &&
            tweak.Group == "Speech / Input" && !tweak.Recommended &&
            tweak.Risk == TweakRisk.Optional && !tweak.RestartRequired &&
            tweak.RequiresAdmin && tweak.RegistryHive == RegistryHive.LocalMachine &&
            tweak.RegistryPath == SpeechModelKey &&
            tweak.RegistryValueName == SpeechModelValueName &&
            tweak.RegistryEnabledValue == 0 && tweak.RevertAsync is not null &&
            tweak.RevertMode == "OriginalRegistryValue", "catalog metadata");

        // 2-5. Exact original key/value existence and original DWORD.
        SpeechModelOriginal missing = DecodeSpeechModelOriginal(
            EncodeSpeechModelOriginal(new SpeechModelOriginal(false, false, 0)));
        Check(!missing.KeyExisted && !missing.ValueExisted, "originally absent key");
        SpeechModelOriginal empty = DecodeSpeechModelOriginal(
            EncodeSpeechModelOriginal(new SpeechModelOriginal(true, false, 0)));
        Check(empty.KeyExisted && !empty.ValueExisted, "originally absent value");
        SpeechModelOriginal one = DecodeSpeechModelOriginal(
            EncodeSpeechModelOriginal(new SpeechModelOriginal(true, true, 1)));
        Check(one.KeyExisted && one.ValueExisted && one.Value == 1, "original DWORD 1");
        SpeechModelOriginal custom = DecodeSpeechModelOriginal(
            EncodeSpeechModelOriginal(new SpeechModelOriginal(true, true, 5)));
        Check(custom.Value == 5, "original DWORD 5");

        // 6-8. Never accept other feature, impossible or corrupted receipts.
        bool invalid = false;
        try { _ = DecodeSpeechModelOriginal("speech-model-original-v1|other|1|1|1"); }
        catch (InvalidDataException) { invalid = true; }
        Check(invalid, "foreign ownership receipt");
        invalid = false;
        try
        {
            _ = DecodeSpeechModelOriginal(
                "speech-model-original-v1|gt.privacy.speechmodel|0|1|1");
        }
        catch (InvalidDataException) { invalid = true; }
        Check(invalid, "invalid original key existence");
        invalid = false;
        try
        {
            _ = DecodeSpeechModelOriginal(
                "speech-model-original-v1|gt.privacy.speechmodel|1|1|bad");
        }
        catch (InvalidDataException) { invalid = true; }
        Check(invalid, "corrupted DWORD snapshot");

        // 9. Compare both existence fields as well as the value.
        Check(SpeechModelMatches(one, new SpeechModelOriginal(true, true, 1)) &&
            !SpeechModelMatches(one, new SpeechModelOriginal(true, true, 0)) &&
            !SpeechModelMatches(missing, empty), "exact original comparison");

        // 10. Strict protection against deleting foreign key data.
        Check(CanRemoveSpeechModelOwnedKey(false, false, 0, false, false) &&
            CanRemoveSpeechModelOwnedKey(false, true, 1, false, true) &&
            !CanRemoveSpeechModelOwnedKey(true, true, 1, false, true) &&
            !CanRemoveSpeechModelOwnedKey(false, true, 0, false, false) &&
            !CanRemoveSpeechModelOwnedKey(false, true, 2, false, true) &&
            !CanRemoveSpeechModelOwnedKey(false, true, 1, true, true) &&
            !CanRemoveSpeechModelOwnedKey(false, true, 1, false, false),
            "strict owned leaf cleanup");

        int? current = 1, original = null;
        bool recorded = false;
        int saves = 0, applies = 0, restores = 0;
        Func<CancellationToken, Task> capture = ct =>
        {
            Check(!ct.IsCancellationRequested, "cancelled receipt storage");
            saves++;
            if (!recorded) { original = current; recorded = true; }
            return Task.CompletedTask;
        };
        Func<CancellationToken, Task> apply = ct =>
        {
            Check(!ct.IsCancellationRequested, "cancelled policy write");
            applies++;
            current = 0;
            return Task.CompletedTask;
        };
        Func<CancellationToken, Task> restore = ct =>
        {
            Check(!ct.IsCancellationRequested, "cancelled restore");
            restores++;
            if (!recorded)
                throw new InvalidOperationException("No original state");
            current = original;
            return Task.CompletedTask;
        };

        // 11-12. ON 1 -> 0, OFF restores 1 without a synthetic default.
        await ExecuteSpeechModelMutationAsync(true, capture, apply, restore,
            CancellationToken.None);
        Check(current == 0 && original == 1 && saves == 1 && applies == 1 &&
            restores == 0, "original capture before ON");
        await ExecuteSpeechModelMutationAsync(false, capture, apply, restore,
            CancellationToken.None);
        Check(current == 1 && saves == 1 && applies == 1 && restores == 1,
            "exact original restoration");

        // 13. A missing original DWORD stays missing.
        current = null; original = null; recorded = false;
        await ExecuteSpeechModelMutationAsync(true, capture, apply, restore,
            CancellationToken.None);
        await ExecuteSpeechModelMutationAsync(false, capture, apply, restore,
            CancellationToken.None);
        Check(current is null, "missing original preserved");

        // 14. Nondefault original DWORD is not rewritten to a guessed 1.
        current = 5; original = null; recorded = false;
        await ExecuteSpeechModelMutationAsync(true, capture, apply, restore,
            CancellationToken.None);
        await ExecuteSpeechModelMutationAsync(false, capture, apply, restore,
            CancellationToken.None);
        Check(current == 5, "custom original preserved");

        // 15. Repeated ON does not replace the first original.
        current = 1; original = null; recorded = false;
        await ExecuteSpeechModelMutationAsync(true, capture, apply, restore,
            CancellationToken.None);
        await ExecuteSpeechModelMutationAsync(true, capture, apply, restore,
            CancellationToken.None);
        Check(original == 1 && current == 0, "repeat ON first original");
        await ExecuteSpeechModelMutationAsync(false, capture, apply, restore,
            CancellationToken.None);
        Check(current == 1, "repeat ON then restore");

        // 16. Failed first-activation receipt storage blocks any policy write.
        int oldApplies = applies;
        bool fault = false;
        try
        {
            await ExecuteSpeechModelMutationAsync(true,
                _ => throw new IOException("atomic receipt failure"),
                apply, restore, CancellationToken.None);
        }
        catch (IOException) { fault = true; }
        Check(fault && applies == oldApplies, "durable receipt failure");

        // 17. OFF with missing original refuses to invent a setting.
        fault = false;
        try
        {
            await ExecuteSpeechModelMutationAsync(false, capture, apply,
                _ => throw new InvalidOperationException("no receipt"),
                CancellationToken.None);
        }
        catch (InvalidOperationException) { fault = true; }
        Check(fault && applies == oldApplies, "no original fails closed");

        // 18. A partially completed write attempts rollback.
        current = 1; recorded = false; original = null;
        fault = false;
        try
        {
            await ExecuteSpeechModelMutationAsync(true, capture,
                _ =>
                {
                    current = 0;
                    throw new IOException("write failed after mutation");
                }, restore, CancellationToken.None);
        }
        catch (IOException) { fault = true; }
        Check(fault && current == 1, "partial registry write rollback");

        // 19. Readback kind/value mismatch attempts the same rollback.
        current = 1; recorded = false; original = null;
        fault = false;
        try
        {
            await ExecuteSpeechModelMutationAsync(true, capture,
                _ =>
                {
                    current = 0;
                    throw new InvalidDataException("wrong DWORD kind");
                }, restore, CancellationToken.None);
        }
        catch (InvalidDataException) { fault = true; }
        Check(fault && current == 1, "readback rollback");

        // 20. Both primary and recovery errors are preserved.
        bool both = false;
        try
        {
            await ExecuteSpeechModelMutationAsync(true, capture,
                _ => throw new IOException("mutation error"),
                _ => throw new UnauthorizedAccessException("restore denied"),
                CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            both = ex.InnerExceptions.Count == 2 &&
                ex.InnerExceptions[0] is IOException &&
                ex.InnerExceptions[1] is UnauthorizedAccessException;
        }
        Check(both, "compound mutation recovery errors");

        // 21. OFF refuses changes made by another process.
        current = 3; fault = false;
        try
        {
            await ExecuteSpeechModelMutationAsync(false, capture, apply,
                _ => throw new InvalidOperationException("external policy edit"),
                CancellationToken.None);
        }
        catch (InvalidOperationException) { fault = true; }
        Check(fault && current == 3, "foreign change preservation");

        // 22. Cancellation after an applied write restores with fresh token.
        current = 1; recorded = false; original = null;
        using var afterWrite = new CancellationTokenSource();
        bool cancelled = false;
        try
        {
            await ExecuteSpeechModelMutationAsync(true, capture, _ =>
            {
                current = 0;
                afterWrite.Cancel();
                return Task.CompletedTask;
            }, restore, afterWrite.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && current == 1, "post-write cancellation rollback");

        // 23-24. Already-cancelled ON/OFF have no side effects.
        using var precancel = new CancellationTokenSource();
        precancel.Cancel();
        int oldSaves = saves, oldRestores = restores;
        bool preOn = false, preOff = false;
        try
        {
            await ExecuteSpeechModelMutationAsync(true, capture, apply, restore, precancel.Token);
        }
        catch (OperationCanceledException) { preOn = true; }
        try
        {
            await ExecuteSpeechModelMutationAsync(false, capture, apply, restore, precancel.Token);
        }
        catch (OperationCanceledException) { preOff = true; }
        Check(preOn && preOff && saves == oldSaves && applies == oldApplies &&
            restores == oldRestores, "pre-cancelled ON OFF");

        // 25. Cancel after durable capture but before policy mutation.
        using var afterCapture = new CancellationTokenSource();
        cancelled = false;
        try
        {
            await ExecuteSpeechModelMutationAsync(true, _ =>
            {
                afterCapture.Cancel();
                return Task.CompletedTask;
            }, apply, restore, afterCapture.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && applies == oldApplies, "capture cancellation");
    }
}
