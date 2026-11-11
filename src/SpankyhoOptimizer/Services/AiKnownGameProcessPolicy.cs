using System;
using System.Collections.Frozen;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Conservative known-executable game recognition for automatic AI workload
/// protection. Names must identify a game binary, not a launcher, web browser,
/// game engine, anti-cheat or generic process name. No filesystem probe,
/// Windows settings write, or injected game hook is required.
/// </summary>
internal static class AiKnownGameProcessPolicy
{
    private static readonly FrozenSet<string> KnownExecutables = new[]
    {
        "DiabloImmortal", "Diablo Immortal", "Diablo IV", "DiabloIV",
        "cs2", "dota2", "VALORANT-Win64-Shipping", "FortniteClient-Win64-Shipping",
        "r5apex", "Overwatch", "destiny2", "GTA5", "GTA5_Enhanced",
        "RDR2", "Cyberpunk2077", "eldenring", "RocketLeague",
        "RainbowSix", "RainbowSix_Vulkan", "League of Legends", "Minecraft.Windows",
        "RobloxPlayerBeta", "Warframe.x64", "HogwartsLegacy", "starfield",
        "bg3", "bf2042", "cod", "BlackOps6", "Wow", "WowClassic",
        "PathOfExile", "PathOfExileSteam", "PathOfExile_x64",
        "TheFinals", "Helldivers2", "MonsterHunterWorld"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    internal static bool IsKnownGame(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        ReadOnlySpan<char> name = processName.AsSpan();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        // Only process names, never caller-supplied filesystem paths or
        // command lines. Reject ambiguous aliases before GPU admission.
        if (name.IsEmpty || name.IndexOfAny("\\/:\"".AsSpan()) >= 0)
            return false;
        // Span lookup avoids an allocation for the occasional .exe suffix.
        return KnownExecutables.GetAlternateLookup<ReadOnlySpan<char>>().Contains(name);
    }
}
