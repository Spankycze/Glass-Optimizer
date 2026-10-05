using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public sealed record SpicetifyTweakState(
    bool DisableSentry,
    bool DisableUiLogging,
    bool ExposeApis,
    bool CheckUpgrade,
    bool InjectCss,
    bool InjectThemeJs,
    bool ReplaceColors,
    bool OverwriteAssets);

public static partial class SpicetifyService
{
    private static readonly object ConfigCacheLock = new();
    private static Dictionary<string, string>? _configCache;
    private static DateTime _configCacheWriteUtc;
    private static readonly object TweakStateCacheLock = new();
    private static SpicetifyTweakState? _tweakStateCache;
    private static DateTime _tweakStateCacheAt;

    public static string ConfigFilePath => Path.Combine(ConfigRoot, "config-xpui.ini");

    public static async Task<SpicetifyTweakState> GetTweakStateAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (TweakStateCacheLock)
        {
            if (_tweakStateCache != null && DateTime.UtcNow - _tweakStateCacheAt < TimeSpan.FromSeconds(30))
                return _tweakStateCache;
        }
        var state = await Task.Run(ReadTweakState, token).ConfigureAwait(false);
        lock (TweakStateCacheLock)
        {
            _tweakStateCache = state;
            _tweakStateCacheAt = DateTime.UtcNow;
        }
        return state;
    }

    private static void InvalidateTweakStateCache()
    {
        lock (TweakStateCacheLock)
        {
            _tweakStateCache = null;
            _tweakStateCacheAt = default;
        }
    }
    private static SpicetifyTweakState ReadTweakState()
    {
        var values = ReadConfigValues();
        bool Flag(string key, bool fallback) =>
            values.TryGetValue(key, out string? value) ? value.Trim() is "1" or "true" or "True" : fallback;

        return new SpicetifyTweakState(
            Flag("disable_sentry", true),
            Flag("disable_ui_logging", true),
            Flag("expose_apis", true),
            Flag("check_spicetify_update", true),
            Flag("inject_css", true),
            Flag("inject_theme_js", true),
            Flag("replace_colors", true),
            Flag("overwrite_assets", false));
    }

    public static SpicetifyTweakState RecommendedTweaks => new(
        DisableSentry: true,
        DisableUiLogging: true,
        ExposeApis: true,
        CheckUpgrade: true,
        InjectCss: true,
        InjectThemeJs: true,
        ReplaceColors: true,
        OverwriteAssets: false);
    public static async Task<ProcessResult> ApplyTweaksAsync(SpicetifyTweakState state, CancellationToken token)
    {
        SpicetifyTweakState before = await GetTweakStateAsync(token).ConfigureAwait(false);

        ProcessResult config = await ApplyTweakConfigOnlyAsync(state, token).ConfigureAwait(false);
        if (!config.Success) return config;

        ProcessResult apply = await ApplyAsync(token).ConfigureAwait(false);
        if (!apply.Success)
        {
            await TryRollbackTweaksAsync(before).ConfigureAwait(false);
            return apply;
        }

        InvalidateTweakStateCache();
        SpicetifyTweakState readBack = await GetTweakStateAsync(token).ConfigureAwait(false);
        if (readBack != state)
        {
            await TryRollbackTweaksAsync(before).ConfigureAwait(false);
            return new ProcessResult(
                ExitCode: 87,
                StdOut: apply.StdOut,
                StdErr: "Spicetify tweak read-back nesouhlasí s požadovanými hodnotami.");
        }

        LogService.Info("spicetify",
            $"SPICETIFY_TWEAKS_VERIFIED sentry={readBack.DisableSentry} uiLog={readBack.DisableUiLogging} apis={readBack.ExposeApis} upgrade={readBack.CheckUpgrade} css={readBack.InjectCss} js={readBack.InjectThemeJs} colors={readBack.ReplaceColors} assets={readBack.OverwriteAssets}");
        return apply;
    }

    private static Task<ProcessResult> ApplyTweakConfigOnlyAsync(
        SpicetifyTweakState state,
        CancellationToken token)
    {
        string args = "config " +
            $"disable_sentry {(state.DisableSentry ? 1 : 0)} " +
            $"disable_ui_logging {(state.DisableUiLogging ? 1 : 0)} " +
            $"expose_apis {(state.ExposeApis ? 1 : 0)} " +
            $"check_spicetify_update {(state.CheckUpgrade ? 1 : 0)} " +
            $"inject_css {(state.InjectCss ? 1 : 0)} " +
            $"inject_theme_js {(state.InjectThemeJs ? 1 : 0)} " +
            $"replace_colors {(state.ReplaceColors ? 1 : 0)} " +
            $"overwrite_assets {(state.OverwriteAssets ? 1 : 0)}";
        return RunCliAsync(args, token, TimeSpan.FromSeconds(45));
    }

    private static async Task TryRollbackTweaksAsync(SpicetifyTweakState before)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            ProcessResult rollbackConfig = await ApplyTweakConfigOnlyAsync(before, cts.Token).ConfigureAwait(false);
            if (rollbackConfig.Success)
                _ = await ApplyAsync(cts.Token).ConfigureAwait(false);
            InvalidateTweakStateCache();
            LogService.Info("spicetify", "SPICETIFY_TWEAKS_ROLLBACK_DONE");
        }
        catch (Exception ex)
        {
            LogService.Error("spicetify", "Spicetify tweak rollback failed", ex);
        }
    }

    public static Task<ProcessResult> ApplyRecommendedTweaksAsync(CancellationToken token) =>
        ApplyTweaksAsync(RecommendedTweaks, token);

    public static IReadOnlySet<string> GetEnabledExtensions()
    {
        var values = ReadConfigValues();
        if (!values.TryGetValue("extensions", out string? raw) || string.IsNullOrWhiteSpace(raw))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return raw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static async Task<ProcessResult> ApplyExtensionsAsync(
        IReadOnlyCollection<string> allExtensions,
        IReadOnlyCollection<string> enabledExtensions,
        CancellationToken token)
    {
        var current = GetEnabledExtensions();
        var desired = enabledExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string extension in allExtensions.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            bool isEnabled = current.Contains(extension);
            bool shouldEnable = desired.Contains(extension);
            if (isEnabled == shouldEnable) continue;

            string value = shouldEnable ? extension : extension + "-";
            ProcessResult change = await RunCliAsync("config extensions " + Quote(value), token, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (!change.Success) return change;
        }
        return await ApplyAsync(token).ConfigureAwait(false);
    }

    public static void OpenMarketplaceRoot()
    {
        string path = Path.Combine(CustomAppsRoot, "marketplace");
        Directory.CreateDirectory(path);
        ProcessRunner.Open(path);
    }
    private static Dictionary<string, string> ReadConfigValues()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(ConfigFilePath)) return result;
            DateTime writeUtc = File.GetLastWriteTimeUtc(ConfigFilePath);
            lock (ConfigCacheLock)
            {
                if (_configCache != null && writeUtc == _configCacheWriteUtc)
                    return new Dictionary<string, string>(_configCache, StringComparer.OrdinalIgnoreCase);
            }
            foreach (string line in File.ReadLines(ConfigFilePath))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith(';') || trimmed.StartsWith('#') || trimmed.StartsWith('['))
                    continue;
                int equals = trimmed.IndexOf('=');
                if (equals <= 0) continue;
                string key = trimmed[..equals].Trim();
                string value = trimmed[(equals + 1)..].Trim();
                if (key.Length > 0) result[key] = value;
            }
            lock (ConfigCacheLock)
            {
                _configCache = new Dictionary<string, string>(result, StringComparer.OrdinalIgnoreCase);
                _configCacheWriteUtc = writeUtc;
            }
        }
        catch (Exception ex)
        {
            LogService.Error("spicetify", "Config read failed", ex);
        }
        return result;
    }
}
