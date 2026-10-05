using System.Reflection;
using SpankyhoOptimizer.Services;

public sealed class VoiceCaptureAccuracyTests
{
    [Fact]
    public void NaturalPauseInsideSentenceDoesNotEndUtterance()
    {
        var method = typeof(GeminiSegmentTranscriptionService).GetMethod("ShouldEndSpeech", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.False((bool)method.Invoke(null, new object[] { 4, 18 })!);
    }
    [Fact]
    public void QuietSpeechDoesNotGetLearnedAsBackgroundNoise()
    {
        double floor = 0.003;
        for (int i = 0; i < 30; i++) floor = GeminiCaptureAccuracyPolicy.UpdateNoiseFloor(floor, 0.009);
        Assert.Equal(0.003, floor, 6);
        Assert.True(0.009 > GeminiCaptureAccuracyPolicy.StartThreshold(floor));
    }

    [Fact]
    public void SilenceIsNotAmplifiedOrCopied()
    {
        byte[] silence = new byte[3200];
        Assert.Same(silence, GeminiCaptureAccuracyPolicy.NormalizeQuietPcm(silence, out double gain));
        Assert.Equal(1, gain);
    }

    [Fact]
    public void QuietSpeechIsRaisedWithoutChangingSourceSamples()
    {
        byte[] pcm = new byte[3200];
        for (int i = 0; i < pcm.Length; i += 2) { pcm[i] = 0; pcm[i + 1] = 1; }
        byte[] amplified = GeminiCaptureAccuracyPolicy.NormalizeQuietPcm(pcm, out double gain);
        Assert.Equal(4, gain);
        Assert.Equal(256, System.BitConverter.ToInt16(pcm, 0));
        Assert.Equal(1024, System.BitConverter.ToInt16(amplified, 0));
    }

    [Fact]
    public void LoudTransientDoesNotClipAfterQuietSpeechAmplification()
    {
        byte[] pcm = new byte[3200];
        pcm[0] = 0; pcm[1] = 64;
        byte[] amplified = GeminiCaptureAccuracyPolicy.NormalizeQuietPcm(pcm, out double gain);
        Assert.InRange(gain, 1, 1.84);
        Assert.InRange(System.Math.Abs(System.BitConverter.ToInt16(amplified, 0)), 0, 30147);
    }

    [Fact]
    public void TrailingQuietConsonantsKeepTwoAudioChunks() =>
        Assert.Equal(7, GeminiTrimPolicy.GetRetainedChunkCount(8, 3));
}
