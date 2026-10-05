using System;
using System.Collections.Generic;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

public static class TweakDependencyRulesService
{
    public static IEnumerable<TweakPreflightIssue> Evaluate(IReadOnlyDictionary<string, TweakDefinition> dirty)
    {
        if (dirty.TryGetValue("net.rssglobal", out var rssGlobal) &&
            dirty.TryGetValue("net.rss", out var rssRegistry) &&
            rssGlobal.Pending != rssRegistry.Pending)
            yield return new("net.rssglobal", "Receive Side Scaling", "Block",
                "Globální RSS a registry RSS musí mít stejný cílový stav.");

        if (dirty.TryGetValue("sys.vbs", out var vbs) &&
            dirty.TryGetValue("sys.hvci", out var hvci) &&
            vbs.Pending && !hvci.Pending)
            yield return new("sys.vbs", "VBS / HVCI", "Block",
                "VBS nelze bezpečně označit jako vypnuté, zatímco HVCI zůstává aktivní.");

        if (dirty.TryGetValue("sys.vbs", out vbs) && vbs.Pending && !dirty.ContainsKey("sys.hvci"))
            yield return new("sys.vbs", "VBS", "Warn",
                "HVCI není součástí stejné dávky; výsledný virtualizační stav bude po aplikaci znovu ověřen.");
        if (dirty.TryGetValue("sys.hypervisor", out var hyper) &&
            dirty.TryGetValue("sys.vbs", out vbs) &&
            hyper.Pending != vbs.Pending)
            yield return new("sys.hypervisor", "Virtualization stack", "Warn",
                "Hypervisor a VBS mají rozdílný cílový stav; po restartu zkontroluj virtualizační funkce.");
    }
}
