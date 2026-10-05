using System;

namespace SpankyhoOptimizer.Services;

public static class GeminiCaptureAccuracyPolicy
{
    public static int EndSilenceChunks(int speechChunks) => speechChunks <= 80 ? 7 : 9;
    public static double StartThreshold(double noiseFloor) => Math.Max(0.0075, noiseFloor * 2.5);
    public static double ContinueThreshold(double noiseFloor) => Math.Max(0.0045, noiseFloor * 1.6);

    public static double UpdateNoiseFloor(double current, double rms)
    {
        // A quiet spoken word must not raise the estimated background noise.
        if (rms >= StartThreshold(current) * 0.70) return current;
        return Math.Max(0.0008, current * 0.96 + rms * 0.04);
    }

    public static byte[] NormalizeQuietPcm(byte[] pcm, out double gain)
    {
        gain = 1;
        int samples = pcm.Length / 2;
        if (samples == 0) return pcm;
        double sum = 0, peak = 0;
        for (int i = 0; i + 1 < pcm.Length; i += 2)
        {
            double value = BitConverter.ToInt16(pcm, i) / 32768d;
            sum += value * value;
            peak = Math.Max(peak, Math.Abs(value));
        }
        double rms = Math.Sqrt(sum / samples);
        if (rms < 0.001 || rms >= 0.035 || peak <= 0) return pcm;
        gain = Math.Max(1, Math.Min(Math.Min(0.045 / rms, 4), 0.92 / peak));
        if (gain <= 1.05) { gain = 1; return pcm; }
        byte[] result = (byte[])pcm.Clone();
        for (int i = 0; i + 1 < result.Length; i += 2)
        {
            short value = (short)Math.Clamp(Math.Round(BitConverter.ToInt16(pcm, i) * gain), short.MinValue, short.MaxValue);
            result[i] = (byte)value;
            result[i + 1] = (byte)(value >> 8);
        }
        return result;
    }
}
