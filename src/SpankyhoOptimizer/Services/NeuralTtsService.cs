using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace SpankyhoOptimizer.Services;

public static class NeuralTtsService
{
    private const string VoiceName = "cs-CZ-AntoninNeural";
    private static string EdgeTtsExe => Path.Combine(
        VoiceRuntimeBootstrapService.OpenWakeWordDir,
        ".venv", "Scripts", "edge-tts.exe");

    public static bool IsAvailable => File.Exists(EdgeTtsExe);
    public static long LastFirstAudioLatencyMs { get; private set; }

    public static async Task SpeakAsync(string text, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        if (!IsAvailable)
            throw new FileNotFoundException("Neural TTS runtime is not available.");

        string[] chunks = VoiceTtsChunkingPolicy.Split(text);

        if (chunks.Length == 0)
            return;

        Task<GeneratedChunk> pending = GenerateChunkAsync(chunks[0], token);
        for (int i = 0; i < chunks.Length; i++)
        {
            token.ThrowIfCancellationRequested();

            GeneratedChunk current = await pending.ConfigureAwait(false);
            Task<GeneratedChunk>? next = i + 1 < chunks.Length
                ? GenerateChunkAsync(chunks[i + 1], token)
                : null;

            try
            {
                await PlayChunkAsync(current, token, i == 0).ConfigureAwait(false);
            }
            finally
            {
                try { File.Delete(current.Path); } catch { }
            }

            if (next != null)
                pending = next;
        }
    }

    private static async Task<GeneratedChunk> GenerateChunkAsync(string text, CancellationToken token)
    {
        string mp3 = Path.Combine(Path.GetTempPath(), $"spanky-neural-{Guid.NewGuid():N}.mp3");
        using var generationCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        generationCts.CancelAfter(VoiceTtsLatencyPolicy.GenerationTimeout);

        try
        {
            long generationMs = await NeuralTtsWorkerService
                .GenerateAsync(text, mp3, generationCts.Token)
                .ConfigureAwait(false);
            return new GeneratedChunk(mp3, text.Length, generationMs);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            try { File.Delete(mp3); } catch { }
            LogService.Info(
                "voice",
                $"VOICE_LATENCY tts_worker_timeout_ms={(long)VoiceTtsLatencyPolicy.GenerationTimeout.TotalMilliseconds}");
            return await GenerateChunkCliAsync(text, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { File.Delete(mp3); } catch { }
            throw;
        }
        catch (Exception ex)
        {
            try { File.Delete(mp3); } catch { }
            LogService.Error("voice", "Persistent neural TTS failed; using CLI fallback", ex);
            return await GenerateChunkCliAsync(text, token).ConfigureAwait(false);
        }
    }

    private static async Task<GeneratedChunk> GenerateChunkCliAsync(string text, CancellationToken token)
    {
        string mp3 = Path.Combine(Path.GetTempPath(), $"spanky-neural-{Guid.NewGuid():N}.mp3");
        var latency = Stopwatch.StartNew();
        using var cliCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cliCts.CancelAfter(VoiceTtsLatencyPolicy.GenerationTimeout);
        CancellationToken cliToken = cliCts.Token;

        var psi = new ProcessStartInfo
        {
            FileName = EdgeTtsExe,
            WorkingDirectory = VoiceRuntimeBootstrapService.OpenWakeWordDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("--voice");
        psi.ArgumentList.Add(VoiceName);
        psi.ArgumentList.Add("--rate=-4%");
        psi.ArgumentList.Add("--pitch=-5Hz");
        psi.ArgumentList.Add("--text");
        psi.ArgumentList.Add(text);
        psi.ArgumentList.Add("--write-media");
        psi.ArgumentList.Add(mp3);

        using var process = new Process { StartInfo = psi };
        process.Start();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cliToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cliToken);

        try
        {
            await process.WaitForExitAsync(cliToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { }

            try { File.Delete(mp3); } catch { }
            throw new TimeoutException("Neural TTS CLI generation timed out.");
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { }

            try { File.Delete(mp3); } catch { }
            throw;
        }

        string outText = await stdout.ConfigureAwait(false);
        string errText = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0 || !File.Exists(mp3))
        {
            try { File.Delete(mp3); } catch { }
            throw new InvalidOperationException($"Neural TTS failed ({process.ExitCode}): {errText}{outText}");
        }

        return new GeneratedChunk(mp3, text.Length, latency.ElapsedMilliseconds);
    }

    private static async Task PlayChunkAsync(GeneratedChunk chunk, CancellationToken token, bool first)
    {
        using var reader = new MediaFoundationReader(chunk.Path);
        using var output = new WaveOut();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        output.PlaybackStopped += (_, e) =>
        {
            if (e.Exception != null) completed.TrySetException(e.Exception);
            else completed.TrySetResult();
        };
        output.Init(reader);

        using CancellationTokenRegistration registration = token.Register(() =>
        {
            try { output.Stop(); } catch { }
        });

        output.Play();
        if (first)
        {
            LastFirstAudioLatencyMs = chunk.GenerationMs;
            LogService.Info("voice", $"VOICE_LATENCY tts_first_audio_ms={LastFirstAudioLatencyMs} chars={chunk.Characters}");
        }

        double timeoutSeconds = Math.Clamp(8 + (chunk.Characters / 8d), 12, 45);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), token).ConfigureAwait(false);
    }

    private sealed record GeneratedChunk(string Path, int Characters, long GenerationMs);
}
