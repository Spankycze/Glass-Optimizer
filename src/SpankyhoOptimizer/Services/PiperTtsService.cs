using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace SpankyhoOptimizer.Services;

public static class PiperTtsService
{
    private static string ToolDir => VoiceRuntimeBootstrapService.PiperDir;
    private static string PiperExe => Path.Combine(ToolDir, "piper.exe");
    private static string VoiceModel => Path.Combine(ToolDir, "voices", "cs_CZ-jirka-medium.onnx");
    private static string VoiceConfig => VoiceModel + ".json";

    public static bool IsAvailable =>
        File.Exists(PiperExe) && File.Exists(VoiceModel) && File.Exists(VoiceConfig);

    public static async Task SpeakAsync(string text, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        if (!IsAvailable)
            throw new FileNotFoundException("Piper Czech TTS runtime is not available.");
        string id = Guid.NewGuid().ToString("N");
        string inputPath = Path.Combine(Path.GetTempPath(), $"spanky-piper-{id}.txt");
        string wavPath = Path.Combine(Path.GetTempPath(), $"spanky-piper-{id}.wav");

        try
        {
            await File.WriteAllTextAsync(inputPath, text.Trim(), new UTF8Encoding(false), token).ConfigureAwait(false);

            var psi = new ProcessStartInfo
            {
                FileName = PiperExe,
                WorkingDirectory = ToolDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(VoiceModel);
            psi.ArgumentList.Add("--config");
            psi.ArgumentList.Add(VoiceConfig);
            psi.ArgumentList.Add("--output_file");
            psi.ArgumentList.Add(wavPath);
            using var process = new Process { StartInfo = psi };
            process.Start();
            await process.StandardInput.WriteAsync(text.Trim()).ConfigureAwait(false);
            await process.StandardInput.WriteLineAsync().ConfigureAwait(false);
            process.StandardInput.Close();
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token).ConfigureAwait(false);

            string outText = await stdout.ConfigureAwait(false);
            string errText = await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(wavPath))
                throw new InvalidOperationException($"Piper TTS failed ({process.ExitCode}): {errText}{outText}");

            using var reader = new WaveFileReader(wavPath);
            using var output = new WaveOut();
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            output.PlaybackStopped += (_, e) =>
            {
                if (e.Exception != null) completed.TrySetException(e.Exception);
                else completed.TrySetResult();
            };
            output.Init(reader);
            output.Play();

            double timeoutSeconds = Math.Clamp(10 + (text.Length / 9d), 15, 50);
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), token).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(inputPath); } catch { }
            try { File.Delete(wavPath); } catch { }
        }
    }
}
