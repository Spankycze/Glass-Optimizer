using System;
using System.IO;
using Windows.UI.ViewManagement;

namespace SpankyhoOptimizer.Services;

public static class UiMotionSettingsService
{
    private static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SpankyhoOptimizer");
    private static string PathName => Path.Combine(Root, "motion.txt");
    private static bool _systemAnimationsEnabled = true;
    private static DateTime _lastSystemRefreshUtc = DateTime.MinValue;

    public static bool Enabled { get; private set; } = Load();
    public static bool SystemAnimationsEnabled => _systemAnimationsEnabled;
    public static bool EffectiveEnabled => Enabled && _systemAnimationsEnabled;

    public static void RefreshSystemPreference(bool force = false)
    {
        if (!force && DateTime.UtcNow - _lastSystemRefreshUtc < TimeSpan.FromSeconds(2))
            return;

        _lastSystemRefreshUtc = DateTime.UtcNow;
        try
        {
            _systemAnimationsEnabled = new UISettings().AnimationsEnabled;
        }
        catch
        {
            // Older/restricted Windows environments should not disable motion accidentally.
            _systemAnimationsEnabled = true;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        bool before = Enabled;
        try
        {
            Enabled = PersistAndVerify(PathName, enabled);
            RefreshSystemPreference(force: true);
        }
        catch
        {
            Enabled = before;
            throw;
        }
    }

    // Atomic same-directory write with an invocation-unique temporary file:
    // concurrent instances cannot trample each other's "motion.txt.tmp".
    // Never report success based on a fallback Load() result.
    private static bool PersistAndVerify(string targetPath, bool enabled)
    {
        string directory = Path.GetDirectoryName(targetPath)
            ?? throw new IOException("Neplatná cesta nastavení animací.");
        Directory.CreateDirectory(directory);
        string temp = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            string expected = enabled ? "1" : "0";
            File.WriteAllText(temp, expected);
            File.Move(temp, targetPath, overwrite: true);
            string actual = File.ReadAllText(targetPath).Trim();
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
                throw new IOException($"UI motion read-back nesouhlasí. Požadováno={expected}, skutečnost={actual}.");
            return actual == "1";
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    // Test file writes on an isolated disposable path, never real motion.txt.
    // Called by the existing full release smoke process only.
    internal static string RunIsolatedPersistenceSmoke()
    {
        string fixture = Path.Combine(Path.GetTempPath(), "spanky-motion-smoke-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(fixture, "motion.txt");
        try
        {
            if (PersistAndVerify(path, false) || File.ReadAllText(path).Trim() != "0")
                throw new InvalidOperationException("UI motion isolated disable persistence failed.");
            if (!PersistAndVerify(path, true) || File.ReadAllText(path).Trim() != "1")
                throw new InvalidOperationException("UI motion isolated enable persistence failed.");
            if (Directory.GetFiles(fixture, "*.tmp").Length != 0)
                throw new InvalidOperationException("UI motion atomic temporary files leaked.");
            return "UI_MOTION_PERSISTENCE_ISOLATED_SMOKE_OK writeReadback=true tempLeak=0 actualUserSettingsTouched=false";
        }
        finally
        {
            if (Directory.Exists(fixture)) Directory.Delete(fixture, recursive: true);
        }
    }

    private static bool Load()
    {
        try { return !File.Exists(PathName) || File.ReadAllText(PathName).Trim() != "0"; }
        catch { return true; }
    }
}
