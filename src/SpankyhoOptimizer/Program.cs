using System;
using System.Diagnostics;
using System.IO;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Explorer helpers must run before WinUI startup and without a shell process.
        if (ContextMenuNativeActions.TryHandle(args)) return;

        // The machine-wide data root feeds SYSTEM tasks (power-plan guard),
        // AI ownership journals and the local update bridge. Never leave it
        // writable by standard users, even on an older installation.
        DataRootSecurityService.EnsureHardenedAcl();

        if (HasArg(args, "--background-update"))
        {
            BackgroundUpdateService.RunOnceAsync().GetAwaiter().GetResult();
            return;
        }

        if (HasArg(args, "--fan-agent"))
        {
            if (TryForwardToRuntimeHost("--fan-agent"))
                return;

            FanProfileService.RunAgentAsync().GetAwaiter().GetResult();
            return;
        }

        if (HasArg(args, "--ai-agent"))
        {
            if (TryForwardToRuntimeHost("--ai-agent"))
                return;

            FanProfileService.RunAiAgentAsync().GetAwaiter().GetResult();
            return;
        }

        if (HasArg(args, "--fan-watchdog"))
        {
            if (TryForwardToRuntimeHost("--fan-watchdog"))
                return;

            FanProfileService.RunWatchdogAsync().GetAwaiter().GetResult();
            return;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            var app = new App();
            GC.KeepAlive(app);
        });
    }

    private static bool TryForwardToRuntimeHost(string argument)
    {
        try
        {
            string host = Path.Combine(AppContext.BaseDirectory, "SpankyhoOptimizer.RuntimeHost.exe");
            if (!File.Exists(host))
                return false;

            using Process? started = Process.Start(new ProcessStartInfo
            {
                FileName = host,
                Arguments = argument,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            return started != null;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasArg(string[] args, string value)
    {
        foreach (string arg in args)
            if (string.Equals(arg, value, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
