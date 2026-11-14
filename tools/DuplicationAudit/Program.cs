using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: DuplicationAudit <root> <minLines> <outJson>");
    return 2;
}

string root = Path.GetFullPath(args[0]);
int minLines = int.TryParse(args[1], out int parsed) ? Math.Max(4, parsed) : 12;
string outJson = Path.GetFullPath(args[2]);
string src = Path.Combine(root, "src");
int minUseful = (int)Math.Ceiling(minLines * 0.75);
var map = new Dictionary<string, List<Location>>(StringComparer.Ordinal);

foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
{
    if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        continue;

    string[] raw = File.ReadAllLines(file);
    string?[] norm = new string?[raw.Length];

    for (int i = 0; i < raw.Length; i++)
    {
        string s = Normalize(raw[i]);
        // Imports repeated across partial class files are not duplicated implementation.
        if (s.Length != 0 && !s.StartsWith("//", StringComparison.Ordinal) &&
            !s.StartsWith("using ", StringComparison.Ordinal) &&
            !s.StartsWith("global using ", StringComparison.Ordinal))
            norm[i] = s;
    }

    for (int i = 0; i <= norm.Length - minLines; i++)
    {
        int useful = 0;
        var sb = new StringBuilder(384);
        for (int j = 0; j < minLines; j++)
        {
            string? s = norm[i + j];
            if (string.IsNullOrEmpty(s))
                continue;
            if (useful++ > 0)
                sb.AppendLine();
            sb.Append(s);
        }

        if (useful < minUseful)
            continue;

        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
        if (!map.TryGetValue(hash, out var list))
        {
            list = new List<Location>(2);
            map.Add(hash, list);
        }
        list.Add(new Location(Path.GetRelativePath(root, file), i + 1));
    }
}

var duplicates = map
    .Where(kvp => kvp.Value.Select(x => x.File).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any())
    .Select(kvp => new Duplicate(kvp.Key, kvp.Value))
    .ToArray();

Directory.CreateDirectory(Path.GetDirectoryName(outJson)!);
await File.WriteAllTextAsync(outJson, JsonSerializer.Serialize(new
{
    utc = DateTime.UtcNow.ToString("O"),
    minLines,
    count = duplicates.Length,
    duplicates
}, new JsonSerializerOptions { WriteIndented = true }));

Console.WriteLine(JsonSerializer.Serialize(new { count = duplicates.Length }));
return 0;

static string Normalize(string value)
{
    if (string.IsNullOrWhiteSpace(value))
        return string.Empty;

    var sb = new StringBuilder(value.Length);
    bool wasSpace = false;
    foreach (char c in value.Trim())
    {
        bool space = char.IsWhiteSpace(c);
        if (space)
        {
            if (!wasSpace)
                sb.Append(' ');
        }
        else
        {
            sb.Append(c);
        }
        wasSpace = space;
    }
    return sb.ToString();
}

sealed record Location(string File, int Line);
sealed record Duplicate(string Hash, List<Location> Locations);
