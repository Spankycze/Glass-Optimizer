using System;
using System.Net;

namespace SpankyhoOptimizer.Services;

public static class ChatGptModelFailurePolicy
{
    public static bool ShouldInvalidate(HttpStatusCode status, string? responseBody)
    {
        if (status == HttpStatusCode.NotFound)
            return true;

        if (status != HttpStatusCode.BadRequest)
            return false;

        string body = responseBody ?? string.Empty;
        return body.Contains("model", StringComparison.OrdinalIgnoreCase) &&
               (body.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("not available", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("invalid", StringComparison.OrdinalIgnoreCase));
    }
}
