namespace SpankyhoOptimizer.Services;

public static class VoiceTranscriptionContext
{
    public const string Prompt =
        "Přepis přirozené hovorové češtiny pro hlasového asistenta Spanky ve Spankyho Optimizeru. " +
        "Vrať přesně to, co uživatel řekl; neodpovídej na obsah. Zachovej slang, krátké povely a oslovení 'hej vole'. " +
        "Při nejistotě preferuj české názvy režimů a přesné názvy funkcí, her, aplikací, hardwaru a technických výrazů.";

    public static readonly string[] Keywords =
    {
        "Spanky",
        "Spankyho Optimizer",
        "procesor",
        "CPU",
        "grafická karta",
        "GPU",
        "RAM",
        "teplota procesoru",
        "vytížení procesoru",
        "ventilátory",
        "větráky",
        "otáčky ventilátorů",
        "noční režim",
        "tichý režim",
        "automatický režim ventilátorů",
        "výkonný režim",
        "gaming profil",
        "hej vole",
        "slyšíš",
        "počasí",
        "předpověď počasí",
        "Praha",
        "Firefox",
        "Windows",
        "OpenAI",
        "ChatGPT",
        "Gemini",
        "NVIDIA",
        "GeForce",
        "Ryzen",
        "Gigabyte",
        "Kick",
        "Task Scheduler",
        "PowerShell",
        "Spicetify",
        "Defender",
        "Debloater",
        "Windows Update",
        "DNS",
        "MTU",
        "NVMe",
        "Diablo Immortal",
        "Ryzen 7 3700X",
        "RTX 3060 Ti",
        "M27Q",
        "Lian Li",
        "RuntimeHost",
        "fan agent",
        "spusť DNS benchmark",
        "otevři Firefox",
        "jaký mám Windows",
        "teplota GPU",
        "ping",
        "latence"
    };
}
