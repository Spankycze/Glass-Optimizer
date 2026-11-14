using System.Formats.Tar;
using System.IO;
using System.Text;
using ZstdSharp;

namespace InstallerUiTests;

// Synthetic archives never touch the installed application.
internal static class PayloadFixture
{
    internal static byte[] Build(string fileName, string data = "fixture", bool asSymlink = false,
        int extraManifestFiles = 0, long extraManifestBytes = 0, string? versionOverride = null,
        string? secondName = null)
    {
        byte[] contents = Encoding.UTF8.GetBytes(data);
        using var output = new MemoryStream();
        using (var compression = new CompressionStream(output, 5))
        using (var tar = new TarWriter(compression, TarEntryFormat.Pax))
        {
            byte[] manifest = Encoding.UTF8.GetBytes(
                $"SPANKY_ZSTD_V1|{versionOverride ?? SpankyhoOptimizer.ModernSetup.Program.Version}|{1+extraManifestFiles+(secondName == null ? 0 : 1)}|{contents.Length+extraManifestBytes+(secondName == null ? 0 : contents.Length)}");
            using var ms = new MemoryStream(manifest);
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, ".spanky-payload-manifest-v1")
            { DataStream = ms });
            if (asSymlink)
                tar.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, fileName) { LinkName = "../escape" });
            else
            {
                using var body = new MemoryStream(contents);
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, fileName) { DataStream = body });
                if (secondName != null)
                {
                    using var next = new MemoryStream(contents);
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, secondName) { DataStream = next });
                }
            }
        }
        return output.ToArray();
    }
}
