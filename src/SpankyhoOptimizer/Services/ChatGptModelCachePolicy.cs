using System;

namespace SpankyhoOptimizer.Services;

public readonly record struct ChatGptModelCacheDecision(
    bool UseCachedImmediately,
    bool RefreshInBackground,
    bool FetchNow);

public static class ChatGptModelCachePolicy
{
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromMinutes(10);

    public static ChatGptModelCacheDecision Decide(bool hasCachedModel, TimeSpan age)
    {
        if (!hasCachedModel)
            return new(false, false, true);

        bool stale = age >= RefreshAfter;
        return new(true, stale, false);
    }
}
