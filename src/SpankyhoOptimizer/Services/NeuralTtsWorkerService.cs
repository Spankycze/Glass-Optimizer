using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public static class NeuralTtsWorkerService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Process? _process;
    private static Task? _stderrDrainTask;
    private static int _nextRequestId;

    private static string PythonExe => Path.Combine(
        VoiceRuntimeBootstrapService.OpenWakeWordDir,
        ".venv", "Scripts", "python.exe");

    private static string WorkerScript => Path.Combine(
        VoiceRuntimeBootstrapService.OpenWakeWordDir,
        "spanky-tts-worker.py");

    public static bool IsAvailable => File.Exists(PythonExe);
    public static bool IsRunning => _process is { HasExited: false };

    public static async Task WarmAsync(CancellationToken token = default)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await EnsureStartedAsync(token).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task<long> GenerateAsync(
        string text,
        string outputPath,
        CancellationToken token)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Process process = await EnsureStartedAsync(token).ConfigureAwait(false);
            int id = Interlocked.Increment(ref _nextRequestId);
            string request = NeuralTtsWorkerProtocol.CreateRequest(id, text, outputPath);
            var latency = Stopwatch.StartNew();

            await process.StandardInput.WriteLineAsync(request).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(token).ConfigureAwait(false);

            string? line = await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false);
            if (line == null)
                throw new EndOfStreamException("Persistent TTS worker closed stdout.");

            NeuralTtsWorkerResponse response = NeuralTtsWorkerProtocol.ParseResponse(line);
            if (response.Id != id)
                throw new InvalidDataException($"TTS worker response id mismatch: {response.Id} != {id}.");
            if (!response.Ok || !File.Exists(outputPath))
                throw new InvalidOperationException(response.Error ?? "Persistent TTS generation failed.");

            return latency.ElapsedMilliseconds;
        }
        catch
        {
            StopWorkerNoThrow();
            throw;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<Process> EnsureStartedAsync(CancellationToken token)
    {
        if (_process is { HasExited: false } running)
            return running;

        if (!File.Exists(PythonExe))
            throw new FileNotFoundException("Python runtime for persistent TTS is not available.", PythonExe);

        Directory.CreateDirectory(Path.GetDirectoryName(WorkerScript)!);
        string script = BuildWorkerScript();
        if (!File.Exists(WorkerScript) ||
            !string.Equals(await File.ReadAllTextAsync(WorkerScript, token).ConfigureAwait(false), script, StringComparison.Ordinal))
        {
            await File.WriteAllTextAsync(WorkerScript, script, token).ConfigureAwait(false);
        }

        var psi = new ProcessStartInfo
        {
            FileName = PythonExe,
            WorkingDirectory = VoiceRuntimeBootstrapService.OpenWakeWordDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add(WorkerScript);

        var process = new Process { StartInfo = psi };
        process.Start();

        _stderrDrainTask = Task.Run(async () =>
        {
            try
            {
                while (!process.HasExited)
                {
                    string? line = await process.StandardError.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) break;
                    if (line.Length > 0)
                        LogService.Info("voice", "TTS_WORKER " + line);
                }
            }
            catch { }
        }, CancellationToken.None);

        string? ready = await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false);
        if (!string.Equals(ready, "READY", StringComparison.Ordinal))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            try { process.Dispose(); } catch { }
            throw new InvalidOperationException("Persistent TTS worker did not become ready.");
        }

        _process = process;
        LogService.Info("voice", "TTS_WORKER_READY");
        return process;
    }

    public static async Task StopAsync()
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            StopWorkerNoThrow();
            Task? stderr = _stderrDrainTask;
            _stderrDrainTask = null;
            if (stderr != null)
            {
                try { await stderr.ConfigureAwait(false); } catch { }
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private static void StopWorkerNoThrow()
    {
        Process? process = _process;
        _process = null;
        if (process == null)
            return;

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { }

        try { process.Dispose(); } catch { }
    }

    private static string BuildWorkerScript() => """
import asyncio
import json
import os
import sys
import time
import edge_tts

VOICE = "cs-CZ-AntoninNeural"
RATE = "-4%"
PITCH = "-5Hz"

async def main():
    print("READY", flush=True)
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue

        try:
            req = json.loads(line)
            rid = int(req["id"])
            text = str(req["text"])
            output = str(req["output"])
            t0 = time.perf_counter()
            communicate = edge_tts.Communicate(
                text,
                VOICE,
                rate=RATE,
                pitch=PITCH)
            await communicate.save(output)
            ms = int((time.perf_counter() - t0) * 1000)
            print(json.dumps({
                "id": rid,
                "ok": True,
                "generation_ms": ms
            }, ensure_ascii=False), flush=True)
        except Exception as ex:
            rid = req.get("id", 0) if "req" in locals() and isinstance(req, dict) else 0
            print(json.dumps({
                "id": rid,
                "ok": False,
                "generation_ms": 0,
                "error": str(ex)
            }, ensure_ascii=False), flush=True)

asyncio.run(main())
""";
}
