using System;
using System.Collections.Generic;
using Microsoft.Win32;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    public static IEnumerable<TweakDefinition> AiTweaks()
    {
        const string ai = "SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsAI";
        yield return TweakFactory.Dword("gt.ai.clicktodo", "ai", "Windows AI", "Click to Do vypnuté",
            "Zakáže Click to Do policy.", RegistryHive.LocalMachine, ai, "DisableClickToDo", 1, null, true, false, TweakRisk.Optional, true);
        yield return TweakFactory.Dword("gt.ai.snapshots", "ai", "Recall", "Ukládání AI snapshots vypnuté",
            "Zakáže ukládání Recall/AI snapshotů další používanou policy.", RegistryHive.LocalMachine, ai, "TurnOffSavingSnapshots", 1, null, true, true, TweakRisk.Safe, true);
        yield return TweakFactory.Dword("gt.ai.settingsagent", "ai", "Windows AI", "Settings Agent vypnutý",
            "Zakáže AI Settings Agent.", RegistryHive.LocalMachine, ai, "DisableSettingsAgent", 1, null, true, false, TweakRisk.Optional, true);
        yield return TweakFactory.Dword("gt.ai.agentconnectors", "ai", "Windows AI", "Agent Connectors vypnuté",
            "Zakáže Windows AI agent connectors.", RegistryHive.LocalMachine, ai, "DisableAgentConnectors", 1, null, true, false, TweakRisk.Optional, true);
        yield return TweakFactory.Dword("gt.ai.agentworkspaces", "ai", "Windows AI", "Agent Workspaces vypnuté",
            "Zakáže AI agent workspaces.", RegistryHive.LocalMachine, ai, "DisableAgentWorkspaces", 1, null, true, false, TweakRisk.Optional, true);
        yield return TweakFactory.Dword("gt.ai.remoteconnectors", "ai", "Windows AI", "Remote Agent Connectors vypnuté",
            "Zakáže vzdálené AI agent connectors.", RegistryHive.LocalMachine, ai, "DisableRemoteAgentConnectors", 1, null, true, false, TweakRisk.Optional, true);
        yield return TweakFactory.Dword("gt.ai.imagecreator", "ai", "Windows AI", "Image Creator vypnutý",
            "Zakáže systémový Windows AI Image Creator policy flag.", RegistryHive.LocalMachine,
            "SOFTWARE\\Microsoft\\PolicyManager\\default\\WindowsAI\\DisableImageCreator", "value", 1, 0, false, false, TweakRisk.Optional, true);
        yield return TweakFactory.Dword("gt.ai.paintcreator", "ai", "Paint", "Paint Image Creator vypnutý",
            "Zakáže generativní Image Creator v Paint.", RegistryHive.LocalMachine,
            "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\Paint", "DisableImageCreator", 1, null, true, false, TweakRisk.Optional);
        yield return TweakFactory.Dword("gt.ai.paintcocreator", "ai", "Paint", "Paint Cocreator vypnutý",
            "Zakáže Cocreator v Paint.", RegistryHive.LocalMachine,
            "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\Paint", "DisableCocreator", 1, null, true, false, TweakRisk.Optional);
        yield return TweakFactory.Dword("gt.ai.paintfill", "ai", "Paint", "Paint Generative Fill vypnutý",
            "Zakáže Generative Fill v Paint.", RegistryHive.LocalMachine,
            "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\Paint", "DisableGenerativeFill", 1, null, true, false, TweakRisk.Optional);
        yield return TweakFactory.Dword("gt.ai.painterase", "ai", "Paint", "Paint Generative Erase vypnutý",
            "Zakáže Generative Erase v Paint.", RegistryHive.LocalMachine,
            "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\Paint", "DisableGenerativeErase", 1, null, true, false, TweakRisk.Optional);
        yield return TweakFactory.Dword("gt.ai.paintbg", "ai", "Paint", "Paint Remove Background vypnutý",
            "Zakáže AI Remove Background v Paint.", RegistryHive.LocalMachine,
            "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\Paint", "DisableRemoveBackground", 1, null, true, false, TweakRisk.Optional);
        yield return TweakFactory.Dword("gt.ai.notepad", "ai", "Notepad", "Notepad AI funkce vypnuté",
            "Zakáže AI funkce ve Windows Notepad.", RegistryHive.LocalMachine,
            "SOFTWARE\\Policies\\WindowsNotepad", "DisableAIFeatures", 1, null, true, false, TweakRisk.Optional);
    }

    public static IEnumerable<TweakDefinition> BrowserTweaks(string browser)
    {
        if (!browser.Equals("Edge", StringComparison.OrdinalIgnoreCase)) yield break;
        const string edge = "SOFTWARE\\Policies\\Microsoft\\Edge";
        yield return TweakFactory.Dword("gt.edge.composeai", "browsers", "AI / Copilot", "Edge Compose AI vypnuté",
            "Zakáže Compose/inline generative AI v Edge.", RegistryHive.LocalMachine, edge, "ComposeInlineEnabled", 0, null, true, false, TweakRisk.Optional);
        yield return TweakFactory.Dword("gt.edge.copilotcontext", "browsers", "AI / Copilot", "Copilot Page Context vypnutý",
            "Zakáže předávání kontextu stránky Copilotu.", RegistryHive.LocalMachine, edge, "CopilotPageContext", 0, null, true, false, TweakRisk.Optional);
        yield return TweakFactory.Dword("gt.edge.copilotcdp", "browsers", "AI / Copilot", "Copilot CDP Page Context vypnutý",
            "Zakáže Copilot CDP page context.", RegistryHive.LocalMachine, edge, "CopilotCDPPageContext", 0, null, true, false, TweakRisk.Optional);
        yield return TweakFactory.Dword("gt.edge.m365copilot", "browsers", "AI / Copilot", "Microsoft 365 Copilot ikona vypnutá",
            "Skryje Microsoft 365 Copilot Chat ikonu v Edge.", RegistryHive.LocalMachine, edge, "Microsoft365CopilotChatIconEnabled", 0, null, true, false, TweakRisk.Optional);
        yield return TweakFactory.Dword("gt.edge.historyai", "browsers", "AI / Copilot", "AI History Search vypnuté",
            "Zakáže AI vyhledávání v historii Edge.", RegistryHive.LocalMachine, edge, "EdgeHistoryAISearchEnabled", 0, null, true, false, TweakRisk.Optional);
        yield return TweakFactory.Dword("gt.edge.builtinai", "browsers", "AI / Copilot", "Built-in AI API vypnuté",
            "Zakáže vestavěná AI API v Edge.", RegistryHive.LocalMachine, edge, "BuiltInAIAPIsEnabled", 0, null, true, false, TweakRisk.Optional);
        yield return TweakFactory.Dword("gt.edge.aithemes", "browsers", "AI / Copilot", "AI generované motivy vypnuté",
            "Zakáže AI generování motivů v Edge.", RegistryHive.LocalMachine, edge, "AIGenThemesEnabled", 0, null, true, false, TweakRisk.Optional);
        yield return TweakFactory.Dword("gt.edge.devtoolsai", "browsers", "AI / Copilot", "DevTools generative AI vypnuté",
            "DevToolsGenAiSettings = 2.", RegistryHive.LocalMachine, edge, "DevToolsGenAiSettings", 2, null, true, false, TweakRisk.Optional);
        yield return TweakFactory.Dword("gt.edge.sharehistoryai", "browsers", "AI / Copilot", "Sdílení historie s Copilot Search vypnuté",
            "Zakáže sdílení browsing history s Copilot Search.", RegistryHive.LocalMachine, edge, "ShareBrowsingHistoryWithCopilotSearchAllowed", 0, null, true, false, TweakRisk.Optional);
    }
    private static TweakDefinition HostsBlockTweak()
    {
        return TweakFactory.Command("gt.privacy.msdomains", "privacy", "Advanced blocklist",
            "Blokovat Microsoft telemetry domény/IP",
            "Převezme Advanced blocklist do označeného hosts bloku a samostatného outbound firewall pravidla bez přepisování cizích hosts záznamů.",
            async ct => await System.Threading.Tasks.Task.Run(GTweakPrivacyBlockService.IsApplied, ct),
            async (on, ct) => await GTweakPrivacyBlockService.SetAppliedAsync(on, ct),
            false, TweakRisk.Advanced, true, "Vysoký",
            "Seznam je velmi agresivní a může ovlivnit OneDrive, Microsoft Store, Xbox, Edge, Windows služby nebo přihlašování.",
            19041, "Any", true, true);
    }
}
