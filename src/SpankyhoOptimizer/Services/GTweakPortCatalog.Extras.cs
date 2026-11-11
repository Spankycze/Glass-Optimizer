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
            "Zakáže generativní Image Creator v aplikaci Malování (Paint).", RegistryHive.LocalMachine,
            "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\Paint", "DisableImageCreator", 1, null, true, true, TweakRisk.Safe, true);
        yield return TweakFactory.Dword("gt.ai.paintcocreator", "ai", "Paint", "Paint Cocreator vypnutý",
            "Zakáže AI Cocreator v aplikaci Malování (Paint).", RegistryHive.LocalMachine,
            "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\Paint", "DisableCocreator", 1, null, true, true, TweakRisk.Safe, true);
        yield return TweakFactory.Dword("gt.ai.paintfill", "ai", "Paint", "Paint Generative Fill vypnutý",
            "Zakáže Generative Fill v Paint.", RegistryHive.LocalMachine,
            "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\Paint", "DisableGenerativeFill", 1, null, true, false, TweakRisk.Optional, true);
        yield return TweakFactory.Dword("gt.ai.painterase", "ai", "Paint", "Paint Generative Erase vypnutý",
            "Zakáže Generative Erase v Paint.", RegistryHive.LocalMachine,
            "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\Paint", "DisableGenerativeErase", 1, null, true, false, TweakRisk.Optional, true);
        yield return TweakFactory.Dword("gt.ai.paintbg", "ai", "Paint", "Paint Remove Background vypnutý",
            "Zakáže AI Remove Background v Paint.", RegistryHive.LocalMachine,
            "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\Paint", "DisableRemoveBackground", 1, null, true, false, TweakRisk.Optional, true);
        yield return TweakFactory.Dword("ai.paint", "ai", "Paint", "Vypnout AI funkce v Malování (Paint AI policy)",
            "Zakáže integrované AI funkce v aplikaci Malování přes systémovou politiku Windows Paint.", RegistryHive.LocalMachine,
            "SOFTWARE\\Policies\\Microsoft\\Windows\\Paint", "DisableAIFeatures", 1, null, true, true, TweakRisk.Safe, true);
        yield return TweakFactory.Dword("gt.ai.notepad", "ai", "Notepad", "Vypnout AI funkce v Poznámkovém bloku (machine policy)",
            "Zakáže AI funkce ve Windows Poznámkovém bloku přes systémovou politiku WindowsNotepad.", RegistryHive.LocalMachine,
            "SOFTWARE\\Policies\\WindowsNotepad", "DisableAIFeatures", 1, null, true, true, TweakRisk.Safe, true);
        yield return TweakFactory.Dword("ai.notepad", "ai", "Notepad", "Vypnout AI přepisování (Copilot / Rewrite) v Poznámkovém bloku",
            "Vypne uživatelské AI přepisování textu (Rewrite / Copilot) v nastavení Poznámkového bloku se zálohou původní hodnoty.", RegistryHive.CurrentUser,
            "Software\\Microsoft\\Notepad", "RewriteEnabled", 0, 1, false, true, TweakRisk.Safe, false);
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
