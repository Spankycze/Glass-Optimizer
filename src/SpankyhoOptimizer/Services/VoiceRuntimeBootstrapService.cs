using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public static class VoiceRuntimeBootstrapService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SpankyhoOptimizer", "VoiceTools");
    public static string WhisperDir => Path.Combine(Root, "Whisper");
    public static string PiperDir => Path.Combine(Root, "Piper");
    public static string OpenWakeWordDir => Path.Combine(Root, "OpenWakeWord");

    public static async Task EnsureAsync(Action<string>? status = null, CancellationToken token = default)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Root);
            if (VoiceLegacyRuntimeCleanup.TryDelete(WhisperDir))
                LogService.Info("voice", "LEGACY_WHISPER_RUNTIME_REMOVED");
            else
                LogService.Info("voice", "LEGACY_WHISPER_RUNTIME_DELETE_DEFERRED");

            await EnsurePiperAsync(status, token).ConfigureAwait(false);
            await EnsureOpenWakeWordAsync(status, token).ConfigureAwait(false);

            try
            {
                await NeuralTtsWorkerService.WarmAsync(token).ConfigureAwait(false);
                LogService.Info("voice", "TTS_WORKER_PREWARM_OK");
            }
            catch (Exception ex)
            {
                LogService.Error("voice", "Persistent TTS prewarm failed; CLI fallback remains available", ex);
            }

            status?.Invoke("Voice runtime ready");
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task EnsurePiperAsync(Action<string>? status, CancellationToken token)
    {
        string exe = Path.Combine(PiperDir, "piper.exe");
        string model = Path.Combine(PiperDir, "voices", "cs_CZ-jirka-medium.onnx");
        string config = model + ".json";
        if (File.Exists(exe) && File.Exists(model) && File.Exists(config)) return;

        status?.Invoke("Stahuji český hlas Piper…");
        Directory.CreateDirectory(PiperDir);
        Directory.CreateDirectory(Path.GetDirectoryName(model)!);

        if (!File.Exists(exe))
        {
            string zip = Path.Combine(Path.GetTempPath(), "spanky-piper.zip");
            string extract = Path.Combine(Path.GetTempPath(), "spanky-piper-" + Guid.NewGuid().ToString("N"));
            try
            {
                await DownloadFileAsync(
                    "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip",
                    zip, token).ConfigureAwait(false);
                ZipFile.ExtractToDirectory(zip, extract, true);
                foreach (string file in Directory.GetFiles(extract, "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(extract, file);
                    string[] parts = relative.Split(Path.DirectorySeparatorChar);
                    if (parts.Length > 1) relative = Path.Combine(parts.Skip(1).ToArray());
                    string target = Path.Combine(PiperDir, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, true);
                }
            }
            finally
            {
                try { File.Delete(zip); } catch { }
                try { Directory.Delete(extract, true); } catch { }
            }
        }

        if (!File.Exists(model))
            await DownloadFileAsync("https://huggingface.co/rhasspy/piper-voices/resolve/main/cs/cs_CZ/jirka/medium/cs_CZ-jirka-medium.onnx?download=true", model, token).ConfigureAwait(false);
        if (!File.Exists(config))
            await DownloadFileAsync("https://huggingface.co/rhasspy/piper-voices/resolve/main/cs/cs_CZ/jirka/medium/cs_CZ-jirka-medium.onnx.json?download=true", config, token).ConfigureAwait(false);
    }

    private static async Task EnsureOpenWakeWordAsync(Action<string>? status, CancellationToken token)
    {
        string python = Path.Combine(OpenWakeWordDir, ".venv", "Scripts", "python.exe");
        string edgeTts = Path.Combine(OpenWakeWordDir, ".venv", "Scripts", "edge-tts.exe");
        string bridge = Path.Combine(OpenWakeWordDir, "wakeword_bridge.py");
        string model = Path.Combine(OpenWakeWordDir, "models", "hey_jarvis_v0.1.onnx");
        if (File.Exists(python) && File.Exists(edgeTts) && File.Exists(bridge) && File.Exists(model)) return;

        status?.Invoke("Stahuji openWakeWord…");
        Directory.CreateDirectory(OpenWakeWordDir);
        string uv = Path.Combine(OpenWakeWordDir, "uv.exe");
        if (!File.Exists(uv))
        {
            string releases = await GetStringAsync("https://api.github.com/repos/astral-sh/uv/releases/latest", token).ConfigureAwait(false);
            using JsonDocument json = JsonDocument.Parse(releases);
            string? assetUrl = json.RootElement.GetProperty("assets").EnumerateArray()
                .FirstOrDefault(a => string.Equals(a.GetProperty("name").GetString(), "uv-x86_64-pc-windows-msvc.zip", StringComparison.OrdinalIgnoreCase))
                .GetProperty("browser_download_url").GetString();
            if (string.IsNullOrWhiteSpace(assetUrl)) throw new InvalidOperationException("uv Windows asset nebyl nalezen.");

            string zip = Path.Combine(Path.GetTempPath(), "spanky-uv.zip");
            string extract = Path.Combine(Path.GetTempPath(), "spanky-uv-" + Guid.NewGuid().ToString("N"));
            try
            {
                await DownloadFileAsync(assetUrl, zip, token).ConfigureAwait(false);
                ZipFile.ExtractToDirectory(zip, extract, true);
                string found = Directory.GetFiles(extract, "uv.exe", SearchOption.AllDirectories).First();
                File.Copy(found, uv, true);
            }
            finally
            {
                try { File.Delete(zip); } catch { }
                try { Directory.Delete(extract, true); } catch { }
            }
        }

        if (!File.Exists(python))
        {
            await RunAsync(uv, $"venv \"{Path.Combine(OpenWakeWordDir, ".venv")}\" --python 3.12", OpenWakeWordDir, token).ConfigureAwait(false);
            await RunAsync(uv, $"pip install --python \"{python}\" openwakeword", OpenWakeWordDir, token).ConfigureAwait(false);
        }
        if (!File.Exists(edgeTts))
            await RunAsync(uv, $"pip install --python \"{python}\" edge-tts", OpenWakeWordDir, token).ConfigureAwait(false);

        Directory.CreateDirectory(Path.Combine(OpenWakeWordDir, "models"));
        await File.WriteAllTextAsync(bridge, BridgeScript, new UTF8Encoding(false), token).ConfigureAwait(false);
        if (!File.Exists(model))
        {
            string code = "from openwakeword.utils import download_models; download_models(['hey_jarvis'], target_directory=r'" +
                          Path.Combine(OpenWakeWordDir, "models").Replace("'", "\\'") + "')";
            await RunAsync(python, "-c \"" + code.Replace("\"", "\\\"") + "\"", OpenWakeWordDir, token).ConfigureAwait(false);
        }
    }

    private static async Task<string> GetStringAsync(string url, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("SpankyhoOptimizer/1.0");
        using HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
    }

    private static async Task DownloadFileAsync(string url, string path, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".download";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("SpankyhoOptimizer/1.0");
        using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            await input.CopyToAsync(output, token).ConfigureAwait(false);
        File.Move(temp, path, true);
    }

    private static async Task RunAsync(string exe, string arguments, string workingDirectory, CancellationToken token)
    {
        ProcessResult result = await ProcessRunner
            .RunAsync(exe, arguments, TimeSpan.FromMinutes(5), token)
            .ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException($"{Path.GetFileName(exe)} selhal ({result.ExitCode}): {result.StdErr}{result.StdOut}");
    }

    private const string BridgeScript = """
import os
import sys
import time
import numpy as np
from openwakeword.model import Model
BASE = os.path.dirname(os.path.abspath(__file__))
MODELS = os.path.join(BASE, "models")
WAKE_MODEL = os.path.join(MODELS, "hey_jarvis_v0.1.onnx")
MELSPEC = os.path.join(MODELS, "melspectrogram.onnx")
EMBED = os.path.join(MODELS, "embedding_model.onnx")
model = Model(wakeword_models=[WAKE_MODEL], inference_framework="onnx", vad_threshold=0.0, melspec_model_path=MELSPEC, embedding_model_path=EMBED)
FRAME_BYTES = 2560
THRESHOLD = 0.55
buffer = bytearray()
last_wake = 0.0
print("READY " + os.path.basename(WAKE_MODEL), flush=True)
while True:
    chunk = sys.stdin.buffer.read(3200)
    if not chunk: break
    buffer.extend(chunk)
    while len(buffer) >= FRAME_BYTES:
        frame = bytes(buffer[:FRAME_BYTES]); del buffer[:FRAME_BYTES]
        scores = model.predict(np.frombuffer(frame, dtype=np.int16))
        if scores:
            score = max(float(v) for v in scores.values())
            now = time.monotonic()
            if score >= THRESHOLD and now - last_wake >= 2.0:
                last_wake = now
                print(f"WAKE {os.path.basename(WAKE_MODEL)} {score:.4f}", flush=True)
""";
}
