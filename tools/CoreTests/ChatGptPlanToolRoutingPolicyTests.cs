using SpankyhoOptimizer.Services;

public sealed class ChatGptPlanToolRoutingPolicyTests
{
    [Theory]
    [InlineData("Co je DNSSEC?")]
    [InlineData("Jak funguje DNS?")]
    [InlineData("Vysvětli mi rozdíl mezi RAM a VRAM")]
    [InlineData("Proč se používá cache?")]
    [InlineData("Ahoj, jak se máš?")]
    public void General_conversation_uses_tool_free_fast_path(string input)
    {
        Assert.True(ChatGptPlanToolRoutingPolicy.CanUseToolFreeFastPath(input));
    }

    [Theory]
    [InlineData("Jaké je dnes počasí?")]
    [InlineData("Jaká je teď teplota CPU?")]
    [InlineData("Kolik je hodin?")]
    [InlineData("Proč mi laguje Firefox?")]
    [InlineData("Jaký je stav PC?")]
    [InlineData("A zítra?")]
    public void Live_or_diagnostic_queries_keep_native_tools(string input)
    {
        Assert.False(ChatGptPlanToolRoutingPolicy.CanUseToolFreeFastPath(input));
    }

    [Fact]
    public void Normalization_handles_czech_diacritics()
    {
        Assert.Equal(
            "vysvetli mi proc se pouziva cache",
            ChatGptPlanToolRoutingPolicy.Normalize(
                "  Vysvětli mi, proč se používá cache  ")
                .Replace(",", string.Empty, StringComparison.Ordinal));
    }
}
