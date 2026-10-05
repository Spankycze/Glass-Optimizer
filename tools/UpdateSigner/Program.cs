using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args.Length < 1) return 2;
if (args[0].Equals("keygen", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 3) return 2;
    using var rsa = RSA.Create(3072);
    File.WriteAllBytes(args[1], rsa.ExportPkcs8PrivateKey());
    File.WriteAllText(args[2], Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()));
    Console.WriteLine("KEYGEN_OK");
    return 0;
}
if (args[0].Equals("sign", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 3) return 2;
    byte[] privateKey = File.ReadAllBytes(args[1]);
    var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(args[2]))
        ?? throw new InvalidDataException("Manifest is invalid.");
    using var rsa = RSA.Create();
    rsa.ImportPkcs8PrivateKey(privateKey, out _);
    string payload = Canonical(manifest);
    byte[] signature = rsa.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    manifest = manifest with { Signature = Convert.ToBase64String(signature), SignatureAlgorithm = "RSA-PSS-SHA256" };
    File.WriteAllText(args[2], JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("SIGN_OK");
    return 0;
}
return 2;

static string Canonical(Manifest m) => string.Join("\n", new[]
{
    m.Version.Trim(), m.Url.Trim(), m.Sha256.Trim().ToUpperInvariant(), m.Changelog ?? string.Empty
});

public sealed record Manifest(
    string Version,
    string Url,
    string Sha256,
    string? Changelog,
    string? Signature = null,
    string? SignatureAlgorithm = null);