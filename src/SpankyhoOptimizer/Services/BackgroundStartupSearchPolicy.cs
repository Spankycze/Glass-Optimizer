using System;
using System.Collections.Generic;

namespace SpankyhoOptimizer.Services;

// Keep the same four-field matching text as the historical Background Activity
// view, but build it once per startup item rather than once per app definition.
internal static class BackgroundStartupSearchPolicy
{
    // A bounded stack buffer is reused for each startup row. Keep a
    // full-size heap fallback for unusually long commands: do not truncate
    // user data or weaken substring matches that span joined fields.
    internal const int ScratchCharacters = 1024;

    internal static string BuildSearchText(string? name, string? command,
        string? filePath, string? publisher) =>
        string.Join(" ", name ?? string.Empty, command ?? string.Empty,
            filePath ?? string.Empty, publisher ?? string.Empty);

    internal static ReadOnlySpan<char> FormatInto(
        string? name, string? command, string? filePath, string? publisher,
        Span<char> destination)
    {
        ReadOnlySpan<char> a = name.AsSpan();
        ReadOnlySpan<char> b = command.AsSpan();
        ReadOnlySpan<char> c = filePath.AsSpan();
        ReadOnlySpan<char> d = publisher.AsSpan();
        long length = (long)a.Length + b.Length + c.Length + d.Length + 3;
        if (length > destination.Length) return default;
        int position = 0;
        a.CopyTo(destination); position += a.Length;
        destination[position++] = ' ';
        b.CopyTo(destination[position..]); position += b.Length;
        destination[position++] = ' ';
        c.CopyTo(destination[position..]); position += c.Length;
        destination[position++] = ' ';
        d.CopyTo(destination[position..]); position += d.Length;
        return destination[..position];
    }

    internal static bool MatchesFields(
        string? name, string? command, string? filePath, string? publisher,
        IReadOnlyList<string> tokens)
    {
        Span<char> scratch = stackalloc char[ScratchCharacters];
        ReadOnlySpan<char> formatted = FormatInto(
            name, command, filePath, publisher, scratch);
        return formatted.IsEmpty
            ? MatchesText(BuildSearchText(name, command, filePath, publisher),
                tokens)
            : MatchesText(formatted, tokens);
    }

    internal static bool MatchesText(string searchText, IReadOnlyList<string> tokens) =>
        MatchesText(searchText.AsSpan(), tokens);

    internal static bool MatchesText(
        ReadOnlySpan<char> searchText, IReadOnlyList<string> tokens)
    {
        foreach (string token in tokens)
            if (searchText.Contains(token.AsSpan(), StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
