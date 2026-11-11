using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

// Explicit regression command only. Fixtures never change a real service or run upstream scripts.
internal static class GithubIntegrationSmoke
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static object? Call(string type, string method, params object?[] args)
    {
        var t = typeof(UpstreamSourceUpdateService).Assembly.GetType("SpankyhoOptimizer.Services." + type) ?? typeof(App).Assembly.GetType("SpankyhoOptimizer.Services." + type)
            ?? throw new InvalidOperationException("Missing production integration: " + type);
        var m = t.GetMethod(method, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing integration operation: " + method);
        try { return m.Invoke(null, args); }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }
    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static async Task<object> Batch(bool skip, Func<ServiceChangeRequest, CancellationToken, Task> apply, CancellationToken token = default)
    {
        var items = new[] { new ServiceChangeRequest("missing", 3), new ServiceChangeRequest("present", 3) };
        var task = (Task)Call("ServiceBatchExecution", "RunAsync", items, apply, skip, token)!;
        await task.ConfigureAwait(false);
        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }
    private static string[] Field(object value, string name) => (string[])value.GetType().GetProperty(name)!.GetValue(value)!;

    internal static async Task RunAsync()
    {
        var results = new List<object>(); int failures = 0;
        async Task Check(string name, Func<Task> test)
        {
            try { await test().ConfigureAwait(false); results.Add(new { name, outcome = "PASS" }); }
            catch (Exception ex) { failures++; results.Add(new { name, outcome = "FAIL", error = ex.Message }); }
        }
        Task Policy(bool force, bool enabled, bool snapshot, DateTime? checkedUtc, bool expected)
        {
            bool actual = (bool)Call("UpstreamUpdatePolicy", "ShouldProbe", force, enabled, snapshot, checkedUtc, DateTime.UtcNow)!;
            Require(actual == expected, "Incorrect upstream due/opt-out policy."); return Task.CompletedTask;
        }
        await Check("manual-force-overrides-auto-optout", () => Policy(true, false, true, DateTime.UtcNow, true)).ConfigureAwait(false);
        await Check("auto-optout-remains-respected", () => Policy(false, false, false, null, false)).ConfigureAwait(false);
        await Check("missing-snapshot-not-treated-as-current", () => Policy(false, true, false, DateTime.UtcNow, true)).ConfigureAwait(false);
        await Check("hourly-source-freshness", () => Policy(false, true, true, DateTime.UtcNow.AddHours(-2), true)).ConfigureAwait(false);
        await Check("fresh-source-does-not-poll", () => Policy(false, true, true, DateTime.UtcNow, false)).ConfigureAwait(false);
        await Check("download-does-not-equal-installed-code", () =>
        {
            Require((string)Call("UpstreamUpdatePolicy", "IntegrationMode", "sophia")! == "reference", "Sophia snapshot presented as runtime.");
            Require((string)Call("UpstreamUpdatePolicy", "IntegrationMode", "winhance")! == "native-port", "Native port presented as auto-imported code.");
            Require((string)Call("UpstreamUpdatePolicy", "IntegrationMode", "dnshop")! == "live-catalog", "DNS consumer missing.");
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        await Check("missing-service-skipped-and-next-applied", async () =>
        {
            int calls = 0;
             var r = await Batch(true, (item, _) => { calls++; if (item.Name == "missing") throw new Win32Exception(1060); return Task.CompletedTask; }).ConfigureAwait(false);
            Require(calls == 2 && Field(r,"Errors").Length == 0 && Field(r,"Skipped").Length == 1, "Missing service aborted or falsely failed the batch.");
        }).ConfigureAwait(false);
        await Check("strict-service-batch-still-reports-missing", async () =>
        {
            var r = await Batch(false, (item, _) => item.Name == "missing" ? Task.FromException(new Win32Exception(1060)) : Task.CompletedTask).ConfigureAwait(false);
            Require(Field(r,"Errors").Length == 1 && Field(r,"Skipped").Length == 0, "Strict mutation lost its missing-service error.");
        }).ConfigureAwait(false);
        await Check("access-denied-never-treated-as-missing", async () =>
        {
            var r = await Batch(true, (item, _) => item.Name == "missing" ? Task.FromException(new Win32Exception(5)) : Task.CompletedTask).ConfigureAwait(false);
            Require(Field(r,"Errors").Length == 1 && Field(r,"Skipped").Length == 0, "Access denied was swallowed.");
        }).ConfigureAwait(false);
        await Check("batch-cancellation-is-propagated", async () =>
        {
            using var cts = new CancellationTokenSource(); int calls = 0; bool cancelled = false;
             try { await Batch(true, (_, _) => { calls++; cts.Cancel(); return Task.FromCanceled(cts.Token); }, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { cancelled = true; }
            Require(cancelled && calls == 1, "Cancellation was converted into a per-service error.");
        }).ConfigureAwait(false);
        string root = Path.Combine(Path.GetTempPath(), "SpankyGithubTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root,"snapshot","config"));
        string source = Path.Combine(root,"snapshot","config","tweaks.json");
        string destination = Path.Combine(root,"active-policy.json");
        const string commit = "07ccd8e2e755a706f31569808b31f5b77acad6a9";
        string Fixture(string extra = "") => "{\"WPFTweaksServices\":{\"InvokeScript\":[\"THIS MUST NEVER EXECUTE\"],\"service\":[" +
            "{\"Name\":\"CscService\",\"StartupType\":\"Disabled\"},{\"Name\":\"DiagTrack\",\"StartupType\":\"Disabled\"}," +
            "{\"Name\":\"MapsBroker\",\"StartupType\":\"Manual\"},{\"Name\":\"StorSvc\",\"StartupType\":\"Manual\"}," +
            "{\"Name\":\"SharedAccess\",\"StartupType\":\"Disabled\"}" + extra + "]}}";
        try
        {
            await Check("winutil-data-import-feeds-native-consumer", () =>
            {
                File.WriteAllText(source, Fixture());
                Call("WinUtilServicePolicy", "ImportSnapshot", Path.Combine(root,"snapshot"), commit, destination);
                var values = (ServiceChangeRequest[])Call("WinUtilServicePolicy", "ReadFile", destination)!;
                Require(values.Length == 4 && values.All(x => x.Name != "SharedAccess") && values.Single(x=>x.Name=="CscService").StartType == 4, "Allowed native policy not imported correctly.");
                return Task.CompletedTask;
            }).ConfigureAwait(false);
            await Check("invalid-import-keeps-previous-policy", () =>
            {
                File.WriteAllText(source, Fixture()); Call("WinUtilServicePolicy", "ImportSnapshot", Path.Combine(root,"snapshot"), commit, destination);
                string previous = File.ReadAllText(destination); File.WriteAllText(source, "{\"WPFTweaksServices\":{\"service\":[]}}"); bool rejected = false;
                try { Call("WinUtilServicePolicy", "ImportSnapshot", Path.Combine(root,"snapshot"), commit, destination); } catch (InvalidDataException) { rejected = true; }
                Require(rejected && File.ReadAllText(destination) == previous, "Malformed snapshot replaced the known-good policy."); return Task.CompletedTask;
            }).ConfigureAwait(false);
            await Check("upstream-multiline-json-remains-inert", () =>
            {
                File.WriteAllText(source, Fixture().Replace("THIS MUST NEVER EXECUTE", "first line\r\nsecond line\n\tlast line"));
                Call("WinUtilServicePolicy", "ImportSnapshot", Path.Combine(root,"snapshot"), commit, destination);
                var data = (ServiceChangeRequest[])Call("WinUtilServicePolicy", "ReadFile", destination)!;
                Require(data.Length == 4, "Literal multiline strings broke the data-only importer.");
                return Task.CompletedTask;
             }).ConfigureAwait(false);
             await Check("duplicate-service-data-is-rejected", () =>
            {
                File.WriteAllText(source, Fixture(",{\"Name\":\"CscService\",\"StartupType\":\"Automatic\"}")); bool rejected = false;
                try { Call("WinUtilServicePolicy", "ImportSnapshot", Path.Combine(root,"snapshot"), commit, destination); } catch (InvalidDataException) { rejected = true; }
                Require(rejected, "Conflicting service entries accepted."); return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
        finally { Directory.Delete(root, true); }
        await Check("source-cache-writable-without-admin", () =>
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + Path.DirectorySeparatorChar;
            Require(UpstreamSourceUpdateService.Root.StartsWith(local, StringComparison.OrdinalIgnoreCase), "Source cache still requires ProgramData write privileges.");
            Directory.CreateDirectory(UpstreamSourceUpdateService.Root);
            string probe = Path.Combine(UpstreamSourceUpdateService.Root, ".test-" + Guid.NewGuid().ToString("N"));
            try { File.WriteAllText(probe, "probe"); Require(File.ReadAllText(probe)=="probe", "Source cache write/read failed."); }
            finally { if (File.Exists(probe)) File.Delete(probe); }
            return Task.CompletedTask;
            }).ConfigureAwait(false);
        await Check("bundled-or-active-policy-is-consumed", () =>
        {
            var data = (ServiceChangeRequest[])Call("WinUtilServicePolicy", "ReadRecommended")!;
            Require(data.Length == 4 && data.All(x => x.Name != "SharedAccess"), "Native consumer is disconnected from the policy data.");
            return Task.CompletedTask;
         }).ConfigureAwait(false);
         await Check("missing-copy-not-labelled-newer-commit", () =>
        {
            var status = new UpstreamSourceStatus("winutil", "WinUtil", "ChrisTitusTech/winutil", commit, DateTime.UtcNow, null, false, null)
                { LatestCommit = commit };
            Require(!status.NewerSourceAvailable, "A missing local copy is incorrectly reported as a newer GitHub revision.");
            return Task.CompletedTask;
         }).ConfigureAwait(false);
        await Check("native-worker-missing-service-contract", async () =>
        {
            // A random service that is confirmed absent cannot have any configuration changed.
            string name = "Spanky_Test_Absent_" + Guid.NewGuid().ToString("N");
            Require(ServiceSafetyService.ReadStartType(name) == null, "Unexpected fixture collision.");
            var items = new[] { new ServiceChangeRequest(name, 3) };
            var optional = (Task<string[]>)Call("WorkerMutationService", "SetServicesBatchAsync", new ServiceBatchRequest(items, true), CancellationToken.None)!;
             Require((await optional.ConfigureAwait(false)).Length == 0, "Native missing-service error was not skipped by Recommended.");
            var strict = (Task<string[]>)Call("WorkerMutationService", "SetServicesBatchAsync", new ServiceBatchRequest(items), CancellationToken.None)!;
             Require((await strict.ConfigureAwait(false)).Length == 1, "Strict native batch lost a real ERROR_SERVICE_DOES_NOT_EXIST error.");
         }).ConfigureAwait(false);
         await Check("clean-install-bundled-policy-without-cache", () =>
        {
            // Read the installed artifact explicitly, even when a user cache happens to exist.
            string file = Path.Combine(AppContext.BaseDirectory, "Assets", "WinUtilServicePolicy.json");
            var data = (ServiceChangeRequest[])Call("WinUtilServicePolicy", "ReadFile", file)!;
            Require(data.Length == 4, "Clean-install fallback is missing or invalid.");
            return Task.CompletedTask;
         }).ConfigureAwait(false);
        await Check("packaged-winutil-attribution-present", () =>
        {
            string license = Path.Combine(AppContext.BaseDirectory, "Assets", "Licenses", "WinUtil-MIT.txt");
            Require(File.Exists(license) && File.ReadAllText(license).Contains("CT Tech Group LLC", StringComparison.Ordinal), "WinUtil license not packaged.");
            return Task.CompletedTask;
         }).ConfigureAwait(false);
        string report = Path.Combine(LogService.Root, "github-integration-smoke.json");
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { version=typeof(App).Assembly.GetName().Version?.ToString(), utc=DateTime.UtcNow, passed=failures==0, results }, JsonOptions)).ConfigureAwait(false);
        if (failures != 0) throw new InvalidOperationException("GitHub integration regression failures: " + failures);
    }
}
