using SpankyhoOptimizer.Services;

public sealed class VoiceRoutingTests
{
    [Theory]
    [InlineData("po čas si", "pocasi")]
    [InlineData("po časy", "pocasi")]
    [InlineData("pocas si", "pocasi")]
    [InlineData("poás", "pocasi")]
    [InlineData("poas", "pocasi")]
    [InlineData("POČASÍ", "pocasi")]
    public void Weather_stt_variants_normalize_to_weather(string input, string expected)
    {
        Assert.Equal(expected, SpankyVoiceToolService.NormalizeRoutingText(input));
    }

    [Theory]
    [InlineData("nucni rezim vetraku", "nocni rezim vetraku")]
    [InlineData("chat gpt", "chatgpt")]
    [InlineData("ge force", "geforce")]
    [InlineData("fire fox", "firefox")]
    [InlineData("diablo imortal", "diablo immortal")]
    public void Common_asr_aliases_normalize_before_routing(string input, string expected)
    {
        Assert.Equal(expected, SpankyVoiceToolService.NormalizeRoutingText(input));
    }

    [Theory]
    [InlineData("teploda procesoru", "teplota procesoru")]
    [InlineData("kolik stupnu ma procesor", "kolik stupnu ma procesor")]
    [InlineData("vytizeni gbu", "vytizeni gpu")]
    [InlineData("jaky mam pnig", "jaky mam ping")]
    public void Fuzzy_intent_matching_tolerates_small_stt_errors(string input, string phrase)
    {
        string normalized = SpankyVoiceToolService.NormalizeRoutingText(input);
        Assert.True(SpankyVoiceToolService.MatchesIntent(normalized, phrase));
    }

    [Theory]
    [InlineData("jake je pocasi v brne", "Brno")]
    [InlineData("jak je v praze", "Praha")]
    [InlineData("pocasi v ostrave zitra", "Ostrava")]
    [InlineData("pocasi v new yorku", "New York")]
    public void Weather_location_parser_normalizes_czech_locatives(string input, string expected)
    {
        string normalized = SpankyVoiceToolService.NormalizeRoutingText(input);
        Assert.Equal(expected, SpankyVoiceToolService.ExtractWeatherLocation(normalized));
    }

    [Fact]
    public async Task Static_time_route_uses_fast_path()
    {
        string? reply = await SpankyVoiceToolService.TryHandleAsync("kolik je hodin");
        Assert.False(string.IsNullOrWhiteSpace(reply));
        Assert.StartsWith("Je ", reply);
    }

    [Fact]
    public async Task Static_date_route_uses_fast_path()
    {
        string? reply = await SpankyVoiceToolService.TryHandleAsync("jake je datum");
        Assert.False(string.IsNullOrWhiteSpace(reply));
        Assert.StartsWith("Dnes je ", reply);
    }

    [Fact]
    public async Task Fan_status_route_uses_fast_path()
    {
        string? reply = await SpankyVoiceToolService.TryHandleAsync("stav vetraku");
        Assert.False(string.IsNullOrWhiteSpace(reply));
        Assert.Contains("větr", reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Optimizer_version_route_is_specific()
    {
        string? reply = await SpankyVoiceToolService.TryHandleAsync("jaka je verze optimizeru");
        Assert.False(string.IsNullOrWhiteSpace(reply));
        Assert.Contains("Spankyho Optimizer je verze", reply, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Windows:", reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Windows_version_route_is_specific()
    {
        string? reply = await SpankyVoiceToolService.TryHandleAsync("jaky mam windows");
        Assert.False(string.IsNullOrWhiteSpace(reply));
        Assert.DoesNotContain("Spankyho Optimizer je verze", reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Gpu_temperature_route_uses_fast_path()
    {
        string? reply = await SpankyVoiceToolService.TryHandleAsync("teplota gpu");
        Assert.False(string.IsNullOrWhiteSpace(reply));
    }

    [Fact]
    public async Task Native_tool_dispatcher_handles_process_lookup()
    {
        string reply = await ChatGptPlanConversationService.ExecuteNativeToolForTestAsync(
            "is_application_running",
            """{"name":"definitely-not-a-real-spanky-process-xyz"}""");

        Assert.Contains("neběží", reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Native_tool_dispatcher_returns_live_ram_summary()
    {
        string reply = await ChatGptPlanConversationService.ExecuteNativeToolForTestAsync("get_ram_usage");

        Assert.False(string.IsNullOrWhiteSpace(reply));
        Assert.Contains("GB", reply, StringComparison.OrdinalIgnoreCase);
    }
}
