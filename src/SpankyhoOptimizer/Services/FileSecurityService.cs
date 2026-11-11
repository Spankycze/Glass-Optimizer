using System;
using System.IO;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
        // Read the actual X.500 Organization RDN, not a matching substring in
        // a CN, OU or unrelated organization (e.g. "Not NVIDIA Corporation").
        if (!HasApprovedSignerOrganization(info.Signer))
            throw new InvalidOperationException("Instalační soubor není podepsaný očekávaným vydavatelem.");
    }

    private static bool HasApprovedSignerOrganization(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return false;
        try
        {
            // NativeSystemProbeService.ReadSignature supplies the verified
            // Authenticode certificate's Subject. Reparse its X.500 name to
            // identify the O attribute without trusting its text formatting.
            var dn = new X500DistinguishedName(subject);
            var reader = new AsnReader(dn.RawData, AsnEncodingRules.DER);
            var rdns = reader.ReadSequence();
            string? organization = null;
            while (rdns.HasData)
            {
                var entries = rdns.ReadSetOf();
                while (entries.HasData)
                {
                    var attribute = entries.ReadSequence();
                    string oid = attribute.ReadObjectIdentifier();
                    if (oid == "2.5.4.10")
                    {
                        // Multiple organization attributes are ambiguous.
                        if (organization != null) return false;
                        var tag = attribute.PeekTag();
                        if (tag.TagClass != TagClass.Universal) return false;
                        var stringTag = (UniversalTagNumber)tag.TagValue;
                        if (stringTag is not (UniversalTagNumber.UTF8String or
                            UniversalTagNumber.PrintableString or UniversalTagNumber.BMPString or
                            UniversalTagNumber.TeletexString))
                            return false;
                        organization = attribute.ReadCharacterString(stringTag);
                    }
                    else
                    {
                        attribute.ReadEncodedValue();
                    }
                    if (attribute.HasData) return false;
                }
            }
            if (reader.HasData) return false;
            return string.Equals(organization, "NVIDIA Corporation", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(organization, "Microsoft Corporation", StringComparison.OrdinalIgnoreCase);
        }
        catch (AsnContentException) { return false; }
        catch (CryptographicException) { return false; }
        catch (ArgumentException) { return false; }
    }

    internal static string RunTrustedSignerIdentitySmoke()
    {
        var cases = new (string Subject, string Status, bool Approved)[]
        {
            ("CN=Microsoft Windows, O=Microsoft Corporation, C=US", "Valid", true),
            ("CN=NVIDIA Corporation, O=NVIDIA Corporation, C=US", "Valid", true),
            ("CN=Definitely Not Microsoft Corporation, O=Evil Corp, C=US", "Valid", false),
            ("CN=NVIDIA Corporation, O=Evil Corp, C=US", "Valid", false),
            ("CN=\"Fake, O=NVIDIA Corporation\", O=Evil Corp, C=US", "Valid", false),
            ("CN=Other Corp, O=Not NVIDIA Corporation, C=US", "Valid", false),
            ("CN=Microsoft Corporation, O=Microsoft Corporation, C=US", "Invalid", false),
            ("garbage unsigned blob", "Valid", false)
        };
        int accepted = 0, rejected = 0;
        foreach (var item in cases)
        {
            bool ok = true;
            try { RequireMicrosoftOrNvidia(new FileSecurityInfo("synthetic", item.Status, item.Subject)); }
            catch (InvalidOperationException) { ok = false; }
            if (ok != item.Approved)
                throw new InvalidOperationException("Untrusted GPU installer publisher was misclassified: " + item.Subject);
            if (ok) accepted++; else rejected++;
        }
        return $"TRUSTED_SIGNER_IDENTITY_SMOKE_OK accepted={accepted} rejected={rejected} certFiles=0 driverInstalls=0";
    }
}
