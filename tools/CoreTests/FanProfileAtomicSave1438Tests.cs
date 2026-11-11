using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using SpankyhoOptimizer.Services;
using Xunit;

namespace CoreTests;

public sealed class FanProfileAtomicSave1438Tests
{
    [Fact]
    public void AtomicProfileSaveNormalizesAndReplacesWithoutCreatingSharedTmp()
    {
        string directory = Path.Combine(Path.GetTempPath(), "spanky-fanprofile-1438-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "fan-profile.json");
        try
        {
            FanProfile first = FanProfileService.WriteProfileSnapshot(path,
                new FanProfile { TargetRpm = 1100, CpuTempTargetEnabled = false,
                    BrightnessPercent = 45 });
            FanProfile second = FanProfileService.WriteProfileSnapshot(path,
                new FanProfile { TargetRpm = 1620, CpuTempTargetEnabled = true,
                    BrightnessPercent = 101, ChannelCurveEnabled = new[] { true } });
            Assert.Equal(1100, first.TargetRpm);
            Assert.Equal(1620, second.TargetRpm);
            Assert.Equal(100, second.BrightnessPercent);
            Assert.Equal(4, second.ChannelCurveEnabled.Length);
            FanProfile restored = JsonSerializer.Deserialize<FanProfile>(File.ReadAllText(path))!;
            Assert.Equal(second.TargetRpm, restored.TargetRpm);
            Assert.Equal(second.BrightnessPercent, restored.BrightnessPercent);
            Assert.True(restored.ChannelCurveEnabled[0]);
            Assert.False(restored.ChannelCurveEnabled[1]);
            Assert.DoesNotContain(Directory.GetFiles(directory),
                filename => Path.GetFileName(filename).Contains(".tmp", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ParallelProfileSavesCannotShareAnUnsafeFixedTemporaryFilename()
    {
        string directory = Path.Combine(Path.GetTempPath(), "spanky-fanprofile-concurrent-1438-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "fan-profile.json");
        try
        {
            FanProfileService.WriteProfileSnapshot(path, new FanProfile { TargetRpm = 1100 });
            var writers = Enumerable.Range(0, 4).Select(writer => Task.Run(() =>
            {
                for (int i = 0; i < 5; i++)
                    FanProfileService.WriteProfileSnapshot(path,
                        new FanProfile { TargetRpm = 1100 + writer * 100 + i,
                            EffectName = "writer-" + writer });
            }));
            await Task.WhenAll(writers);
            FanProfile restored = JsonSerializer.Deserialize<FanProfile>(File.ReadAllText(path))!;
            Assert.InRange(restored.TargetRpm, 1100, 1404);
            Assert.StartsWith("writer-", restored.EffectName);
            Assert.DoesNotContain(Directory.GetFiles(directory),
                filename => Path.GetFileName(filename).Contains(".tmp", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
