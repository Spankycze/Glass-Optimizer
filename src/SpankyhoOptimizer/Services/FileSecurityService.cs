using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public sealed record FileSecurityInfo(string Sha256, string SignatureStatus, string Signer)
{
    public bool IsTrustedSignature => SignatureStatus.Equals("Valid", StringComparison.OrdinalIgnoreCase);
}

public static class FileSecurityService
{
    public static async Task<string> ComputeSha256Async(string path, CancellationToken token = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        byte[] hash = await sha.ComputeHashAsync(stream, token);
        return Convert.ToHexString(hash);
    }

    public static async Task<FileSecurityInfo> InspectAsync(string path, CancellationToken token = default)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Soubor nebyl nalezen.", path);
        string hash = await ComputeSha256Async(path, token);
        token.ThrowIfCancellationRequested();
        var signature = NativeSystemProbeService.ReadSignature(path);
        return new FileSecurityInfo(hash, signature.Status, signature.Signer);
    }

    public static void RequireMicrosoftOrNvidia(FileSecurityInfo info)
    {
        if (!info.IsTrustedSignature)
            throw new InvalidOperationException("Digitální podpis instalačního souboru není platný.");
        if (!info.Signer.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) &&
            !info.Signer.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Instalační soubor není podepsaný očekávaným vydavatelem.");
    }
}
