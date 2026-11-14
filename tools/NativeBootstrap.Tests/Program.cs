using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

internal sealed class FakeHandler(Func<HttpRequestMessage,int,HttpResponseMessage> callback) : HttpMessageHandler
{
    internal int Calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(callback(request, ++Calls));
}
internal static class DownloaderTests
{
    private static int passed;
    private static readonly Uri Source = new("https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/fixture");
    private static readonly byte[] Bytes = "Verified fixture for native bootstrap download"u8.ToArray();

    private static HttpResponseMessage Ok(byte[] bytes, long? declared = null, bool unknown = false)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        if (declared.HasValue) response.Content.Headers.ContentLength = declared;
        if (unknown) response.Content.Headers.ContentLength = null;
        return response;
    }
    private static HttpResponseMessage Redirect(string? location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Redirect);
        if (location is not null) response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }
    private static async Task Check(string name, int expected, Func<string,Task<int>> run,
        bool fileExpected = false)
    {
        string folder = Path.Combine(Path.GetTempPath(), "spanky-native-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string destination = Path.Combine(folder, "runtime.exe");
            int actual = await run(destination);
            if (actual != expected) throw new Exception(name + ": status " + actual + " expected " + expected);
            if (File.Exists(destination) != fileExpected) throw new Exception(name + ": destination state");
            if (Directory.GetFiles(folder, "*.part").Length != 0) throw new Exception(name + ": leaked partial");
            Console.WriteLine("PASS " + name);
            passed++;
        }
        finally { Directory.Delete(folder, true); }
    }
    private static async Task<int> Download(string target,
        Func<HttpRequestMessage,int,HttpResponseMessage> callback,
        byte[]? sha = null, long max = 1024, Uri? source = null,
        Action<long,long?>? onProgress = null, CancellationToken cancel = default)
    {
        using var handler = new FakeHandler(callback);
        using var client = new HttpClient(handler);
        return await RuntimeDownloader.DownloadAsync(client, source ?? Source, target,
            sha ?? SHA512.HashData(Bytes), max, onProgress, cancel);
    }
    private static async Task DecisionCase(string name, bool initialReady,
        bool readyAfterInstall, int installerResult, int setupResult,
        int expectedResult, int expectedInstalls, int expectedStarts)
    {
        bool ready = initialReady;
        int installs = 0, starts = 0;
        int result = await BootstrapDecision.EnsureThenLaunchAsync(
            () => ready,
            () =>
            {
                installs++;
                if (installerResult == 0) ready = readyAfterInstall;
                return Task.FromResult(installerResult);
            },
            () => { starts++; return Task.FromResult(setupResult); });
        if (result != expectedResult || installs != expectedInstalls ||
            starts != expectedStarts)
            throw new Exception(name + ": actual " + result + "/" + installs + "/" + starts);
        Console.WriteLine("PASS " + name);
        passed++;
    }

    private static void RuntimePresenceCases()
    {
        string root = Path.Combine(Path.GetTempPath(), "spanky-runtime-detect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string shared = Path.Combine(root, "shared");
            string desktop = Path.Combine(shared, "Microsoft.WindowsDesktop.App", "10.0.12");
            string core = Path.Combine(shared, "Microsoft.NETCore.App", "10.0.12");
            void CheckPresence(string title, bool expected)
            {
                bool actual = RuntimePresence.HasDesktop10(root);
                if (actual != expected) throw new Exception("Runtime presence " + title);
                Console.WriteLine("PASS runtime-presence-" + title);
                passed++;
            }
            CheckPresence("missing", false);
            Directory.CreateDirectory(desktop);
            CheckPresence("version-folder-only", false);
            foreach (string name in new[] { "PresentationFramework.dll",
                "Microsoft.WindowsDesktop.App.runtimeconfig.json", "wpfgfx_cor3.dll" })
                File.WriteAllText(Path.Combine(desktop, name), "fixture");
            CheckPresence("desktop-only", false);
            Directory.CreateDirectory(core);
            CheckPresence("core-folder-only", false);
            File.WriteAllText(Path.Combine(core, "System.Private.CoreLib.dll"), "fixture");
            CheckPresence("desktop-and-core", true);
            File.Delete(Path.Combine(desktop, "Microsoft.WindowsDesktop.App.runtimeconfig.json"));
            CheckPresence("missing-manifest", false);
            File.WriteAllText(Path.Combine(desktop, "Microsoft.WindowsDesktop.App.runtimeconfig.json"), "fixture");
            CheckPresence("restored-manifest", true);
            string old = Path.Combine(shared, "Microsoft.WindowsDesktop.App", "9.0.99");
            Directory.Move(desktop, old);
            CheckPresence("desktop9-only", false);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task Main()
    {
        await Check("valid-https", 0, p => Download(p, (_,_) => Ok(Bytes)), true);
        await Check("unknown-content-length", 0, p => Download(p, (_,_) => Ok(Bytes, unknown:true)), true);
        await Check("bad-sha512", 32, p => Download(p, (_,_) => Ok("changed"u8.ToArray())));
        await Check("truncated-response", 31, p => Download(p, (_,_) => Ok(Bytes, Bytes.Length+3)));
        await Check("oversize-header", 31, p => Download(p, (_,_) => Ok(Bytes, 1000), max:100));
        await Check("oversize-body", 31, p => Download(p, (_,_) => Ok(Bytes, unknown:true), max:Bytes.Length-1));
        await Check("http-downgrade-redirect", 30, p => Download(p, (_,_) => Redirect("http://insecure.example/")));
        await Check("missing-redirect-location", 30, p => Download(p, (_,_) => Redirect(null)));
        await Check("https-redirect-chain", 0, p => Download(p,
            (_, n) => n < 3 ? Redirect("https://cdn.example/step"+n) : Ok(Bytes)), true);
        await Check("redirect-loop-limit", 30, p => Download(p, (_, n) => Redirect("https://cdn.example/step"+n)));
        await Check("server-error", 33, p => Download(p, (_,_) => new HttpResponseMessage(HttpStatusCode.NotFound)));
        await Check("reject-http-origin", 30, p => Download(p, (_,_) => Ok(Bytes),
            source:new Uri("http://builds.dotnet.microsoft.com/example")));
        await Check("reject-credential-origin", 30, p => Download(p, (_,_) => Ok(Bytes),
            source:new Uri("https://user:pass@builds.dotnet.microsoft.com/example")));
        await Check("reject-relative-target", 34, _ => Download("runtime.exe", (_,_) => Ok(Bytes)));
        await Check("target-no-overwrite", 34, async p => {
            await File.WriteAllTextAsync(p, "existing");
            int status = await Download(p, (_,_) => Ok(Bytes));
            if (await File.ReadAllTextAsync(p) != "existing") throw new Exception("overwrote target");
            return status;
        }, true);
        await DecisionCase("runtime-present-skip-install", true, true, 0, 0, 0, 0, 1);
        await DecisionCase("runtime-present-setup-failure", true, true, 0, 72, 72, 0, 1);
        await DecisionCase("runtime-missing-installed", false, true, 0, 0, 0, 1, 1);
        await DecisionCase("runtime-recheck-missing", false, false, 0, 0, 54, 1, 0);
        await DecisionCase("download-failed-no-setup", false, false, 51, 0, 51, 1, 0);
        await DecisionCase("uac-denied-no-setup", false, false, 58, 0, 58, 1, 0);
        await DecisionCase("restart-needed-no-setup", false, false, 57, 0, 57, 1, 0);
        await DecisionCase("runtime-installer-error", false, false, 53, 0, 53, 1, 0);
        await DecisionCase("runtime-installed-setup-error", false, true, 0, 71, 71, 1, 1);
        foreach (var pair in new (int Exit, int Expected)[] {
            (0, 0), (1641, 57), (3010, 57), (1603, 53) })
        {
            if (BootstrapDecision.ClassifyRuntimeExit(pair.Exit) != pair.Expected)
                throw new Exception("Installer result classification " + pair.Exit);
            passed++;
        }
        {
            var updates = new List<(long Count,long? Total)>();
            await Check("progress-known-length", 0, path => Download(path, (_,_) => Ok(Bytes),
                onProgress: (done,total) => updates.Add((done,total))), true);
            if (updates.Count < 2 || updates[0] != (0L,Bytes.Length) ||
                updates[^1] != (Bytes.Length,Bytes.Length))
                throw new Exception("Known-length progress is incorrect");
            passed++;
            Console.WriteLine("PASS progress-known-values");
        }
        {
            var updates = new List<(long Count,long? Total)>();
            await Check("progress-unknown-length", 0, path => Download(path,
                (_,_) => Ok(Bytes, unknown:true),
                onProgress: (done,total) => updates.Add((done,total))), true);
            if (updates.Count < 2 || updates[0] != (0L,null) ||
                updates[^1] != (Bytes.Length,null))
                throw new Exception("Unknown-length progress is incorrect");
            passed++;
            Console.WriteLine("PASS progress-unknown-values");
        }
        {
            using var cancel = new CancellationTokenSource();
            await Check("cancel-staging-cleanup", 35, path => Download(path,
                (_,_) => Ok(Bytes), onProgress: (done,_) => { if(done==0) cancel.Cancel(); },
                cancel: cancel.Token));
        }
        {
            byte[] large = new byte[2 * 1024 * 1024];
            new Random(17).NextBytes(large);
            byte[] digest = SHA512.HashData(large);
            var updates = new List<long>();
            await Check("progress-throttled-large", 0,
                path => Download(path, (_,_) => Ok(large), sha:digest,
                    max:large.Length + 1024,
                    onProgress:(done,_) => updates.Add(done)), true);
            if (updates.Count > 6 || updates.Count < 3 || updates[0] != 0 ||
                updates[^1] != large.Length ||
                updates.Zip(updates.Skip(1), (a,b) => b > a).Any(ok => !ok))
                throw new Exception("Progress throttling or monotonic updates failed");
            passed++;
            Console.WriteLine("PASS progress-throttled-monotonic");
            using var canceled = new CancellationTokenSource();
            await Check("cancel-mid-stream-cleanup", 35,
                path => Download(path, (_,_) => Ok(large), sha:digest,
                    max:large.Length + 1024,
                    onProgress:(done,_) => { if(done >= 512 * 1024) canceled.Cancel(); },
                    cancel:canceled.Token));
        }
        RuntimePresenceCases();
        if (passed != 44) throw new Exception("Incomplete native bootstrap regression suite: " + passed);
        Console.WriteLine("NATIVE_DOWNLOADER_TESTS_OK 15/15");
        Console.WriteLine("NATIVE_BOOTSTRAP_DECISIONS_OK 13/13");
        Console.WriteLine("NATIVE_RUNTIME_PRESENCE_OK 8/8");
        Console.WriteLine("NATIVE_PROGRESS_CANCEL_TESTS_OK 8/8");
        Console.WriteLine("NATIVE_TOTAL_TESTS_OK " + passed + "/44");
    }
}