using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    private const string ThisPcNamespacePath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace";
    private const string ThisPcReceiptVersion = "thispc-namespace-v1";
    private static readonly SemaphoreSlim ThisPcMutationGate = new(1, 1);

    private static string ThisPcReceiptPath(string id) =>
        Path.Combine(LogService.Root, id + ".this-pc-original-v1.txt");

    // A receipt captures existence BEFORE the very first attempted removal.
    // It must never be overwritten on a subsequent ON or OFF.
    internal static string EncodeThisPcReceipt(
        string id, string clsid, bool originallyExisted) =>
        ThisPcReceiptVersion + "|" + id + "|" + clsid.ToUpperInvariant() +
        "|" + (originallyExisted ? "1" : "0");

    internal static bool ParseThisPcReceipt(
        string content, string id, string clsid)
    {
        var fields = content.TrimEnd('\r', '\n').Split('|');
        if (fields.Length != 4 ||
            fields[0] != ThisPcReceiptVersion ||
            fields[1] != id ||
            !string.Equals(fields[2], clsid, StringComparison.OrdinalIgnoreCase) ||
            (fields[3] != "0" && fields[3] != "1"))
            throw new InvalidDataException(
                "Záznam původního stavu položky Tento počítač je neplatný.");
        return fields[3] == "1";
    }

    // Removing a pre-existing, populated namespace key loses information.
    // Deliberately restrict this tweak to empty entries. Never delete trees.
    internal static bool CanDeleteThisPcNamespaceKey(
        bool exists, bool hasValues, bool hasChildren) =>
        !exists || (!hasValues && !hasChildren);

    private static bool IsNamespaceEntryEmpty(RegistryKey key) =>
        CanDeleteThisPcNamespaceKey(
            true, key.GetValueNames().Length != 0,
            key.GetSubKeyNames().Length != 0);

    private static Task<bool> DetectThisPcFolderAsync(
        string clsid, CancellationToken token) =>
        Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            using var root = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine, RegistryView.Registry64);
            using var entry = root.OpenSubKey(ThisPcNamespacePath + "\\" + clsid, false);
            return entry is null;
        }, token);

    private static async Task<bool> ReadThisPcReceiptAsync(
        string id, string clsid, CancellationToken token)
    {
        string path = ThisPcReceiptPath(id);
        if (!File.Exists(path))
            throw new InvalidOperationException(
                "Chybí záznam původní existence položky Tento počítač. " +
                "Dříve upravený nebo cizí registr nebude automaticky změněn.");
        string contents = await File.ReadAllTextAsync(path, token).ConfigureAwait(false);
        return ParseThisPcReceipt(contents, id, clsid);
    }

    private static async Task CaptureThisPcOriginalAsync(
        string id, string clsid, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string path = ThisPcReceiptPath(id);
        if (File.Exists(path))
        {
            _ = await ReadThisPcReceiptAsync(id, clsid, token).ConfigureAwait(false);
            return;
        }

        bool existed;
        using (RegistryKey root = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine, RegistryView.Registry64))
        using (RegistryKey? entry = root.OpenSubKey(
            ThisPcNamespacePath + "\\" + clsid, writable: false))
        {
            existed = entry is not null;
            if (entry is not null && !IsNamespaceEntryEmpty(entry))
                throw new InvalidOperationException(
                    "Položka Tento počítač obsahuje další nastavení. " +
                    "Optimizer nebude odstraňovat cizí hodnoty ani podklíče.");
        }
        token.ThrowIfCancellationRequested();
        await AtomicFileService.WriteTextAsync(
            path, EncodeThisPcReceipt(id, clsid, existed), token).ConfigureAwait(false);
    }

    private static async Task RemoveThisPcEntryAsync(
        string id, string clsid, CancellationToken token)
    {
        bool originallyExisted = await ReadThisPcReceiptAsync(id, clsid, token).ConfigureAwait(false);
        await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            using RegistryKey root = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? parent = root.OpenSubKey(
                ThisPcNamespacePath, writable: true);
            if (parent is null) return;

            using (RegistryKey? entry = parent.OpenSubKey(clsid, writable: false))
            {
                if (entry is null) return;
                if (!originallyExisted)
                    throw new InvalidOperationException(
                        "Položka vznikla po uložení původního stavu; její mazání je zablokováno.");
                if (!IsNamespaceEntryEmpty(entry))
                    throw new InvalidOperationException(
                        "Položka Tento počítač obsahuje vlastní hodnoty nebo podklíče. " +
                        "Mazání je zablokováno.");
            }
            token.ThrowIfCancellationRequested();
            // DeleteSubKey (not DeleteSubKeyTree) refuses non-empty entries.
            parent.DeleteSubKey(clsid, throwOnMissingSubKey: false);
            using RegistryKey? after = parent.OpenSubKey(clsid, false);
            if (after is not null)
                throw new IOException(
                    "Windows nepotvrdil odebrání položky Tento počítač.");
        }, token).ConfigureAwait(false);
    }

    private static async Task RestoreThisPcOriginalAsync(
        string id, string clsid, CancellationToken token)
    {
        bool existed = await ReadThisPcReceiptAsync(id, clsid, token).ConfigureAwait(false);
        if (!existed)
        {
            // Originally absent: never invent a namespace entry on OFF.
            return;
        }
        token.ThrowIfCancellationRequested();
        using RegistryKey root = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey? existing = root.OpenSubKey(
            ThisPcNamespacePath + "\\" + clsid, writable: false);
        if (existing is not null)
        {
            // Another app may have re-created/edited it. Do not change it.
            return;
        }
        using RegistryKey restored = root.CreateSubKey(
            ThisPcNamespacePath + "\\" + clsid, writable: true)
            ?? throw new IOException(
                "Původní položka Tento počítač nebyla obnovena.");
        using RegistryKey? check = root.OpenSubKey(
            ThisPcNamespacePath + "\\" + clsid, writable: false);
        if (check is null)
            throw new IOException(
                "Windows nepotvrdil obnovení položky Tento počítač.");
    }

    // Same production transaction tested with fault-injection delegates.
    internal static async Task ExecuteThisPcFolderMutationAsync(
        bool hide, Func<CancellationToken, Task> capture,
        Func<CancellationToken, Task> remove,
        Func<CancellationToken, Task> restore, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!hide)
        {
            await restore(CancellationToken.None).ConfigureAwait(false);
            return;
        }
        await capture(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        try
        {
            await remove(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
        catch (Exception mutationError)
        {
            try { await restore(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception recoveryError)
            {
                throw new AggregateException(
                    "Skrytí složky i obnova původní položky selhaly.",
                    mutationError, recoveryError);
            }
            throw;
        }
    }

    private static async Task SetThisPcFolderAsync(
        string id, string clsid, bool hide, CancellationToken token)
    {
        await ThisPcMutationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await ExecuteThisPcFolderMutationAsync(
                hide, ct => CaptureThisPcOriginalAsync(id, clsid, ct),
                ct => RemoveThisPcEntryAsync(id, clsid, ct),
                ct => RestoreThisPcOriginalAsync(id, clsid, ct), token)
                .ConfigureAwait(false);
        }
        finally { ThisPcMutationGate.Release(); }
    }

    private static TweakDefinition ThisPcFolderTweak(
        string id, string label, string clsid) =>
        TweakFactory.Command(id, "ui", "Tento počítač",
            "Skrýt " + label + " z Tento počítač",
            "Odebere pouze položku navigace Tento počítač; skutečná uživatelská složka se nemaže.",
            ct => DetectThisPcFolderAsync(clsid, ct),
            (hide, ct) => SetThisPcFolderAsync(id, clsid, hide, ct),
            false, TweakRisk.Optional, true, "Nízký",
            "Položky s vlastními daty v registru se nemažou. Obnovení vyžaduje uložený původní stav.",
            19041, "Any", true, false,
            revert: ct => SetThisPcFolderAsync(id, clsid, false, ct));
}
