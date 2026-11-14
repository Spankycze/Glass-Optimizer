using System;
using System.IO;
using System.Security.Cryptography;

internal static class PackageOverlay
{
    internal static bool HasPinnedPayload => PackagedInstallerInfo.Size > 0 &&
        PackagedInstallerInfo.Sha256.Length == 64;

    internal static int Extract(string target)
    {
        if (!HasPinnedPayload) return 40;
        if (!Path.IsPathFullyQualified(target) ||
            !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return 41;
        string? temporary = null;
        try
        {
            string? current = Environment.ProcessPath;
            if (current is null) return 42;
            using var source = File.OpenRead(current);
            if (source.Length < PackagedInstallerInfo.Size) return 42;
            source.Seek(-PackagedInstallerInfo.Size, SeekOrigin.End);
            temporary = target + "." + Guid.NewGuid().ToString("N") + ".part";
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[65536];
                long remaining = PackagedInstallerInfo.Size;
                while (remaining > 0)
                {
                    int n = source.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length));
                    if (n == 0) return 42;
                    remaining -= n;
                    hash.AppendData(buffer.AsSpan(0, n));
                    output.Write(buffer, 0, n);
                }
                output.Flush(true);
            }
            var actual = hash.GetHashAndReset();
            if (!CryptographicOperations.FixedTimeEquals(
                actual, Convert.FromHexString(PackagedInstallerInfo.Sha256))) return 43;
            File.Move(temporary, target, overwrite: false);
            temporary = null;
            return 0;
        }
        catch (IOException) { return 44; }
        catch (UnauthorizedAccessException) { return 44; }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // Verify directly from the executable overlay: no TEMP files or 67 MB disk writes.
    internal static int Verify()
    {
        if (!HasPinnedPayload) return 40;
        try
        {
            string? current = Environment.ProcessPath;
            if (current is null) return 42;
            using var source = File.OpenRead(current);
            if (source.Length < PackagedInstallerInfo.Size) return 42;
            source.Seek(-PackagedInstallerInfo.Size, SeekOrigin.End);
            return CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(source),
                Convert.FromHexString(PackagedInstallerInfo.Sha256)) ? 0 : 43;
        }
        catch (IOException) { return 44; }
        catch (UnauthorizedAccessException) { return 44; }
    }
}
