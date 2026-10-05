using System;
using System.IO;

namespace SpankyhoOptimizer.Services;

public static class UiMotionSettingsService
{
    private static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SpankyhoOptimizer");
    private static string PathName => Path.Combine(Root, "motion.txt");

    public static bool Enabled { get; private set; } = Load();

    public static void SetEnabled(bool enabled)
    {
        bool before = Enabled;
        try
        {
            Directory.CreateDirectory(Root);
            string temp = PathName + ".tmp";
            File.WriteAllText(temp, enabled ? "1" : "0");
            File.Move(temp, PathName, overwrite: true);
            bool actual = Load();
            if (actual != enabled)
                throw new IOException($"UI motion read-back nesouhlasí. Požadováno={enabled}, skutečnost={actual}.");
            Enabled = actual;
        }
        catch
        {
            Enabled = before;
            throw;
        }
    }

    private static bool Load()
    {
        try { return !File.Exists(PathName) || File.ReadAllText(PathName).Trim() != "0"; }
        catch { return true; }
    }
}
