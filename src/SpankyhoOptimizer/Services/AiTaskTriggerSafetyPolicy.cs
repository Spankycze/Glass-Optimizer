using System;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Scheduler COM NextRunTime is insufficient proof for event/logon/idle tasks.
/// Read the built-in task definition before any AI disable. Never infer a
/// safe maintenance gap from a missing NextRunTime or unknown trigger XML.
/// </summary>
internal static class AiTaskTriggerSafetyPolicy
{
    internal static string? BlockReason(string? taskXml, DateTime? nextRunUtc, DateTime nowUtc)
    {
        if (nowUtc.Kind != DateTimeKind.Utc)
            return "scheduler clock evidence is not UTC";
        if (string.IsNullOrWhiteSpace(taskXml) || taskXml.Length > 262144)
            return "task definition unavailable or oversized";
        if (nextRunUtc is not DateTime next || next.Kind != DateTimeKind.Utc ||
            next <= nowUtc.AddMinutes(30))
            return "next scheduled run unknown, invalid, overdue or due soon";

        try
        {
            using var input = new StringReader(taskXml);
            using XmlReader reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 262144,
                IgnoreComments = true
            });
            XDocument xml = XDocument.Load(reader, LoadOptions.None);
            XElement? root = xml.Root;
            if (root is null || root.Name.LocalName != "Task")
                return "task definition root is unknown";
            XElement? triggers = root.Elements().FirstOrDefault(
                x => x.Name.LocalName == "Triggers");
            if (triggers is null)
                return "task triggers missing";

            int schedulable = 0;
            foreach (XElement trigger in triggers.Elements())
            {
                // Disabled triggers cannot fire; their presence is not demand.
                XElement? enabled = trigger.Elements().FirstOrDefault(
                    x => x.Name.LocalName == "Enabled");
                if (enabled is not null &&
                    string.Equals(enabled.Value.Trim(), "false",
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                if (trigger.Name.LocalName is "CalendarTrigger" or "TimeTrigger")
                {
                    schedulable++;
                    continue;
                }
                // EventTrigger, BootTrigger, LogonTrigger, IdleTrigger,
                // RegistrationTrigger, SessionStateChangeTrigger and
                // unknown extension triggers have unpredictable demand.
                return "event, logon, boot, idle or unknown demand trigger";
            }
            return schedulable > 0 ? null : "no enabled predictable calendar trigger";
        }
        catch (System.Xml.XmlException)
        {
            return "invalid task trigger definition";
        }
        catch (InvalidOperationException)
        {
            return "unreadable task trigger definition";
        }
    }
}
