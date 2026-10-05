using SpankyhoOptimizer.Services;

public sealed class VoiceHistoryWindowPolicyTests
{
    [Fact]
    public void Keeps_all_small_recent_history()
    {
        int start = VoiceHistoryWindowPolicy.GetStartIndex(new[] { 100, 120, 80, 90 });
        Assert.Equal(0, start);
    }

    [Fact]
    public void Caps_history_to_recent_fourteen_messages()
    {
        int[] lengths = Enumerable.Repeat(100, 20).ToArray();
        int start = VoiceHistoryWindowPolicy.GetStartIndex(lengths);
        Assert.Equal(6, start);
    }

    [Fact]
    public void Caps_history_by_character_budget_from_newest_messages()
    {
        int[] lengths = { 1000, 1000, 1000, 1000, 1000, 1000, 1000, 1000 };
        int start = VoiceHistoryWindowPolicy.GetStartIndex(lengths);
        Assert.Equal(2, start);
    }

    [Fact]
    public void Starts_on_complete_user_assistant_exchange()
    {
        int[] lengths = Enumerable.Repeat(100, 15).ToArray();
        int start = VoiceHistoryWindowPolicy.GetStartIndex(lengths);
        Assert.Equal(2, start);
    }
}
