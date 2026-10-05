using System;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public static class VoiceLocalToolFastPathService
{
    public static async Task DeliverAsync(
        string userText,
        string reply,
        Func<string, CancellationToken, Task> speakAsync,
        Func<string, string, CancellationToken, Task>? rememberAsync,
        CancellationToken token)
    {
        if (rememberAsync != null)
        {
            SafeTaskService.Forget(
                rememberAsync(userText, reply, token),
                "voice",
                "Remember local tool exchange");
        }

        await speakAsync(reply, token).ConfigureAwait(false);
    }
}
