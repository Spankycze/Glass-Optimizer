using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    private const string GalleryId = "gt.ui.gallery";
    private const string GalleryClsid = "{e88865ea-0e1c-4e20-9aa6-edcd0212c87c}";
    private const string GalleryReceiptVersion = "gallery-namespace-v1";
    private static readonly string[] GalleryPaths =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\" + GalleryClsid,
        @"SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\" + GalleryClsid
    ];
    private static readonly SemaphoreSlim GalleryGate = new(1, 1);
    private static string GalleryReceiptPath =>
        Path.Combine(LogService.Root, "gallery-namespace-original-v1.txt");

    internal static string EncodeGalleryReceipt(bool nativeExisted, bool wowExisted) =>
        GalleryReceiptVersion + "|" + GalleryId + "|" +
        (nativeExisted ? "1" : "0") + "|" + (wowExisted ? "1" : "0");

    internal static (bool Native, bool Wow) DecodeGalleryReceipt(string text)
    {
        string[] fields = text.TrimEnd('\r', '\n').Split('|');
        if (fields.Length != 4 ||
            fields[0] != GalleryReceiptVersion || fields[1] != GalleryId ||
            (fields[2] != "0" && fields[2] != "1") ||
            (fields[3] != "0" && fields[3] != "1"))
            throw new InvalidDataException(
                "Původní záznam stavu Gallery v Exploreru je neplatný.");
        return (fields[2] == "1", fields[3] == "1");
    }

    internal static bool CanDeleteGalleryNamespace(
        bool originallyExisted, bool currentlyExisted,
        bool hasValues, bool hasSubkeys) =>
        !currentlyExisted || (originallyExisted && !hasValues && !hasSubkeys);

    private static bool IsEmptyGalleryKey(RegistryKey key) =>
        key.GetValueNames().Length == 0 && key.GetSubKeyNames().Length == 0;

    private static async Task<(bool Native, bool Wow)> ReadGalleryReceiptAsync(
        CancellationToken ct)
    {
        if (!File.Exists(GalleryReceiptPath))
            throw new InvalidOperationException(
                "Chybí původní záznam Gallery; nelze bezpečně rekonstruovat cizí nastavení.");
        return DecodeGalleryReceipt(
            await File.ReadAllTextAsync(GalleryReceiptPath, ct).ConfigureAwait(false));
    }

    private static async Task CaptureGalleryOriginalAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (File.Exists(GalleryReceiptPath))
        {
            _ = await ReadGalleryReceiptAsync(ct).ConfigureAwait(false);
            return;
        }

        bool[] existed = new bool[2];
        using (RegistryKey root = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine, RegistryView.Registry64))
        {
            for (int i = 0; i < GalleryPaths.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                using RegistryKey? key = root.OpenSubKey(GalleryPaths[i], writable: false);
                existed[i] = key is not null;
                // No subtree serializer is available. Do not claim to back up
                // a populated namespace key or erase its unrelated content.
                if (key is not null && !IsEmptyGalleryKey(key))
                    throw new InvalidOperationException(
                        "Gallery obsahuje další hodnoty/podklíče; skrytí by mohlo ztratit data.");
            }
        }

        ct.ThrowIfCancellationRequested();
        await AtomicFileService.WriteTextAsync(
            GalleryReceiptPath, EncodeGalleryReceipt(existed[0], existed[1]), ct)
            .ConfigureAwait(false);
    }

    private static Task<bool> DetectGalleryHiddenAsync(CancellationToken ct) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using RegistryKey root = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine, RegistryView.Registry64);
            foreach (string path in GalleryPaths)
            {
                ct.ThrowIfCancellationRequested();
                using RegistryKey? key = root.OpenSubKey(path, writable: false);
                if (key is not null) return false;
            }
            return true;
        }, ct);

    private static Task RemoveGalleryKeyAsync(
        string path, bool originallyExisted, CancellationToken ct) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using RegistryKey root = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine, RegistryView.Registry64);
            int slash = path.LastIndexOf('\\');
            string parentPath = path[..slash];
            string childName = path[(slash + 1)..];
            using RegistryKey? parent = root.OpenSubKey(parentPath, writable: true);
            if (parent is null) return;
            using (RegistryKey? child = parent.OpenSubKey(childName, writable: false))
            {
                if (child is null) return;
                if (!CanDeleteGalleryNamespace(
                        originallyExisted, true,
                        child.GetValueNames().Length > 0, child.GetSubKeyNames().Length > 0))
                    throw new InvalidOperationException(
                        "Gallery obsahuje cizí nebo nově vzniklá data; mazání je zablokováno.");
            }

            ct.ThrowIfCancellationRequested();
            // Nonrecursive deletion refuses unexpected children even if a
            // second process modified the registry after inspection.
            parent.DeleteSubKey(childName, throwOnMissingSubKey: false);
            using RegistryKey? after = parent.OpenSubKey(childName, writable: false);
            if (after is not null)
                throw new IOException("Windows nepotvrdil odebrání Gallery.");
        }, ct);

    private static async Task ApplyGalleryHiddenAsync(CancellationToken ct)
    {
        var original = await ReadGalleryReceiptAsync(ct).ConfigureAwait(false);
        bool[] originallyExisted = [original.Native, original.Wow];
        for (int i = 0; i < GalleryPaths.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            await RemoveGalleryKeyAsync(GalleryPaths[i], originallyExisted[i], ct)
                .ConfigureAwait(false);
        }
    }

    private static Task RestoreGalleryEntryAsync(
        string path, bool originallyExisted, CancellationToken ct) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using RegistryKey root = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? existing = root.OpenSubKey(path, writable: false);
            if (originallyExisted)
            {
                if (existing is not null)
                {
                    // Someone else may have recreated or edited the key.
                    // Never truncate it or replace its values.
                    return;
                }
                using RegistryKey key = root.CreateSubKey(path, writable: true)
                    ?? throw new IOException("Nelze obnovit původní Gallery.");
                using RegistryKey? check = root.OpenSubKey(path, writable: false);
                if (check is null)
                    throw new IOException("Windows nepotvrdil obnovení Gallery.");
            }
            else if (existing is not null)
            {
                // The app never creates originally-absent keys; this is an
                // externally added namespace entry and must be preserved.
                throw new InvalidOperationException(
                    "Gallery před úpravou neexistovala, ale nyní ji vytvořil jiný software.");
            }
        }, ct);

    private static async Task RestoreGalleryOriginalAsync(CancellationToken ct)
    {
        var original = await ReadGalleryReceiptAsync(ct).ConfigureAwait(false);
        bool[] existed = [original.Native, original.Wow];
        await RestoreGalleryViewsAsync(
            (index, token) => RestoreGalleryEntryAsync(
                GalleryPaths[index], existed[index], token)).ConfigureAwait(false);
    }

    // Independent restore must attempt both views even if the first fails.
    // Fake registry callbacks exercise this production helper in smoke tests.
    internal static async Task RestoreGalleryViewsAsync(
        Func<int, CancellationToken, Task> restoreView)
    {
        var failures = new List<Exception>();
        for (int i = 0; i < GalleryPaths.Length; i++)
        {
            try
            {
                await restoreView(i, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures.Add(new InvalidOperationException(
                    "Obnovení Gallery selhalo: " + GalleryPaths[i], ex));
            }
        }
        if (failures.Count > 0)
            throw new AggregateException(
                "Původní stav Gallery nebyl úplně obnoven.", failures);
    }
    // The same production transaction is exercised by pure, injected tests.
    internal static async Task ExecuteGalleryMutationAsync(
        bool hide,
        Func<CancellationToken, Task> capture,
        Func<CancellationToken, Task> apply,
        Func<CancellationToken, Task> restore,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!hide)
        {
            await restore(CancellationToken.None).ConfigureAwait(false);
            return;
        }
        await capture(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        try
        {
            await apply(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception mutationError)
        {
            try { await restore(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception rollbackError)
            {
                throw new AggregateException(
                    "Skrytí Gallery i obnova původního nastavení selhaly.",
                    mutationError, rollbackError);
            }
            throw;
        }
    }

    private static async Task SetGalleryHiddenAsync(bool hide, CancellationToken ct)
    {
        await GalleryGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ExecuteGalleryMutationAsync(
                hide, CaptureGalleryOriginalAsync, ApplyGalleryHiddenAsync,
                RestoreGalleryOriginalAsync, ct).ConfigureAwait(false);
        }
        finally { GalleryGate.Release(); }
    }

    private static TweakDefinition ExplorerGalleryTweak() =>
        TweakFactory.Command(GalleryId, "ui", "Explorer",
            "Skrýt Gallery z Exploreru",
            "Skryje pouze bezpečně obnovitelné, prázdné Gallery namespace položky.",
            DetectGalleryHiddenAsync, SetGalleryHiddenAsync,
            false, TweakRisk.Optional, true, "Nízký",
            "Klíče obsahující hodnoty nebo podklíče zůstávají zachovány; starší změny bez původního záznamu není možné bezpečně obnovit.",
            22000, "Any", true, false,
            revert: ct => SetGalleryHiddenAsync(false, ct));
}
