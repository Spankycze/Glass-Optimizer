using System;
using System.ServiceProcess;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Read-only SCM probe at the final mutation boundary. Missing/failed driver
/// enumeration is unknown demand, never proof of inactivity.
/// </summary>
internal static class AiKernelDriverRuntimeProbe
{
    internal static string? BlockReason(string serviceName)
    {
        ServiceController[] devices = Array.Empty<ServiceController>();
        try
        {
            devices = ServiceController.GetDevices();
            foreach (ServiceController device in devices)
            {
                if (!string.Equals(device.ServiceName, serviceName,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                return AiDriverAutomationPolicy.LiveDriverBlockReason(
                    discovered: true, device.Status);
            }
            return AiDriverAutomationPolicy.LiveDriverBlockReason(
                discovered: false, ServiceControllerStatus.Stopped);
        }
        catch (Exception)
        {
            // Inability to inspect SCM state must not grant a driver write.
            return "driver SCM state unavailable";
        }
        finally
        {
            foreach (ServiceController device in devices)
                device.Dispose();
        }
    }
}
