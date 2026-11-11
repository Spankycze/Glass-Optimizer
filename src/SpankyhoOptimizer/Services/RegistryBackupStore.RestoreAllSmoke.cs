using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public sealed partial class RegistryBackupStore
{
    // Fault-injected production batch; no registry keys or user settings touched.
    internal static async Task RunRestoreAllSmokeAsync()
    {
        static void Check(bool condition, string scenario)
        {
            if (!condition)
                throw new InvalidOperationException("RESTORE_ALL_SMOKE_FAIL: " + scenario);
        }
        var calls = new List<string>();
        Func<string, CancellationToken, Task<bool>> succeeds = (id, ct) =>
        {
            Check(!ct.IsCancellationRequested, "restore cancellation token");
            calls.Add(id);
            return Task.FromResult(true);
        };

        // 1. An empty backup set is a successful no-op.
        Check(await ExecuteRestoreAllTransactionAsync(
            Array.Empty<string>(), succeeds, CancellationToken.None) == 0 &&
            calls.Count == 0, "empty backup set");

        // 2. Normal batch visits every original ID in the original order.
        Check(await ExecuteRestoreAllTransactionAsync(
            new[] { "first", "second", "third" }, succeeds, CancellationToken.None) == 3 &&
            calls.SequenceEqual(new[] { "first", "second", "third" }),
            "all entries and ordering");

        // 3. False (missing backup) must be surfaced, never reported success.
        var errors = new List<Exception>();
        calls.Clear();
        try
        {
            await ExecuteRestoreAllTransactionAsync(
                new[] { "missing" }, (id, ct) =>
                {
                    calls.Add(id);
                    return Task.FromResult(false);
                }, CancellationToken.None);
        }
        catch (AggregateException ex) { errors.AddRange(ex.InnerExceptions); }
        Check(errors.Count == 1 && calls.SequenceEqual(new[] { "missing" }) &&
            errors[0].ToString().Contains("missing"), "missing snapshot reports failure");

        // 4. A first failure does not prevent subsequent independent restores.
        calls.Clear(); errors.Clear();
        try
        {
            await ExecuteRestoreAllTransactionAsync(
                new[] { "fail", "pass" }, (id, ct) =>
                {
                    calls.Add(id);
                    if (id == "fail") throw new IOException("injected I/O");
                    return Task.FromResult(true);
                }, CancellationToken.None);
        }
        catch (AggregateException ex) { errors.AddRange(ex.InnerExceptions); }
        Check(errors.Count == 1 &&
            calls.SequenceEqual(new[] { "fail", "pass" }), "continue after first failure");

        // 5. Last failure still causes total batch result to be non-success.
        errors.Clear(); calls.Clear();
        try
        {
            await ExecuteRestoreAllTransactionAsync(
                new[] { "pass", "fail" }, (id, ct) =>
                {
                    calls.Add(id);
                    if (id == "fail") throw new IOException("last item");
                    return Task.FromResult(true);
                }, CancellationToken.None);
        }
        catch (AggregateException ex) { errors.AddRange(ex.InnerExceptions); }
        Check(errors.Count == 1 &&
              calls.SequenceEqual(new[] { "pass", "fail" }), "last failure surfaced");

        // 6. Several errors are aggregated, not just the first.
        errors.Clear(); calls.Clear();
        try
        {
            await ExecuteRestoreAllTransactionAsync(new[] { "a", "b", "c" },
                (id, ct) =>
                {
                    calls.Add(id);
                    throw new IOException("fault " + id);
                }, CancellationToken.None);
        }
        catch (AggregateException ex) { errors.AddRange(ex.InnerExceptions); }
        Check(errors.Count == 3 && calls.SequenceEqual(new[] { "a", "b", "c" }),
              "all independent failures aggregated");

        // 7. Root failure causes are preserved for each failed ID.
        Check(errors[0].InnerException is IOException &&
              errors[1].InnerException is IOException &&
              errors[2].ToString().Contains('c'), "root cause retained");

        // 8. Mixed failure types are both visible.
        errors.Clear(); calls.Clear();
        try
        {
            await ExecuteRestoreAllTransactionAsync(new[] { "access", "data" },
                (id, ct) =>
                {
                    calls.Add(id);
                    if (id == "access")
                        throw new UnauthorizedAccessException("denied");
                    throw new InvalidDataException("invalid original");
                }, CancellationToken.None);
        }
        catch (AggregateException ex) { errors.AddRange(ex.InnerExceptions); }
        Check(errors.Count == 2 &&
              errors[0].InnerException is UnauthorizedAccessException &&
              errors[1].InnerException is InvalidDataException, "mixed causes");

        // 9. Duplicate same-ID entries must not mutate the same setting twice.
        calls.Clear(); errors.Clear();
        try
        {
            await ExecuteRestoreAllTransactionAsync(
                new[] { "same", "same", "next" }, succeeds, CancellationToken.None);
        }
        catch (AggregateException ex) { errors.AddRange(ex.InnerExceptions); }
        Check(errors.Count == 1 &&
              calls.SequenceEqual(new[] { "same", "next" }), "duplicate id blocked");

        // 10. IDs compare case-insensitively (same as original backup store).
        calls.Clear(); errors.Clear();
        try
        {
            await ExecuteRestoreAllTransactionAsync(new[] { "ABC", "abc" },
                succeeds, CancellationToken.None);
        }
        catch (AggregateException ex) { errors.AddRange(ex.InnerExceptions); }
        Check(errors.Count == 1 && calls.SequenceEqual(new[] { "ABC" }),
              "case-insensitive duplicate");

        // 11. Missing IDs cannot be executed but are reported.
        errors.Clear(); calls.Clear();
        try
        {
            await ExecuteRestoreAllTransactionAsync(new[] { "", "valid" },
                succeeds, CancellationToken.None);
        }
        catch (AggregateException ex) { errors.AddRange(ex.InnerExceptions); }
        Check(errors.Count == 1 && calls.SequenceEqual(new[] { "valid" }),
              "empty ID rejected");

        // 12. Whitespace IDs are also refused.
        errors.Clear(); calls.Clear();
        try
        {
            await ExecuteRestoreAllTransactionAsync(new[] { " ", "ok" },
                succeeds, CancellationToken.None);
        }
        catch (AggregateException ex) { errors.AddRange(ex.InnerExceptions); }
        Check(errors.Count == 1 && calls.SequenceEqual(new[] { "ok" }),
              "whitespace ID rejected");

        // 13. An already cancelled batch must not touch the first snapshot.
        using var pre = new CancellationTokenSource();
        pre.Cancel();
        bool cancelled = false; calls.Clear();
        try
        {
            await ExecuteRestoreAllTransactionAsync(
                new[] { "a" }, succeeds, pre.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && calls.Count == 0, "pre-cancelled no-op");

        // 14. Cancellation between restores stops future changes.
        using var mid = new CancellationTokenSource();
        cancelled = false; calls.Clear();
        try
        {
            await ExecuteRestoreAllTransactionAsync(
                new[] { "a", "b", "c" }, (id, ct) =>
                {
                    calls.Add(id);
                    if (id == "a") mid.Cancel();
                    return Task.FromResult(true);
                }, mid.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && calls.SequenceEqual(new[] { "a" }),
              "cancel between entries");

        // 15. A delegate signalling cancellation propagates immediately.
        using var inside = new CancellationTokenSource();
        cancelled = false; calls.Clear();
        try
        {
            await ExecuteRestoreAllTransactionAsync(
                new[] { "one", "two" }, (id, ct) =>
                {
                    calls.Add(id);
                    inside.Cancel();
                    throw new OperationCanceledException(inside.Token);
                }, inside.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && calls.SequenceEqual(new[] { "one" }),
              "active cancellation propagated");

        // 16. An unrelated OperationCanceledException is a per-item error,
        // so it must not abort independent remaining restores.
        errors.Clear(); calls.Clear();
        try
        {
            await ExecuteRestoreAllTransactionAsync(
                new[] { "unrelated", "good" }, (id, ct) =>
                {
                    calls.Add(id);
                    if (id == "unrelated")
                        throw new OperationCanceledException("unrelated token");
                    return Task.FromResult(true);
                }, CancellationToken.None);
        }
        catch (AggregateException ex) { errors.AddRange(ex.InnerExceptions); }
        Check(errors.Count == 1 &&
              calls.SequenceEqual(new[] { "unrelated", "good" }),
              "unrelated cancellation isolated");

        // 17. Both callback and input validation fail fast on null.
        bool invalid = false;
        try
        {
            await ExecuteRestoreAllTransactionAsync(
                Array.Empty<string>(), null!, CancellationToken.None);
        }
        catch (ArgumentNullException) { invalid = true; }
        Check(invalid, "null callback refused");

        // 18. Null ID collection fails before calling any callback.
        invalid = false; calls.Clear();
        try
        {
            await ExecuteRestoreAllTransactionAsync(
                null!, succeeds, CancellationToken.None);
        }
        catch (ArgumentNullException) { invalid = true; }
        Check(invalid && calls.Count == 0, "null collection refused");
    }
}
