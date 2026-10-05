using System.Net;
using SpankyhoOptimizer.Services;

namespace CoreTests;

public sealed class ChatGptModelFailurePolicyTests
{
    [Fact]
    public void MissingModelInvalidatesCache()
    {
        Assert.True(ChatGptModelFailurePolicy.ShouldInvalidate(
            HttpStatusCode.NotFound, "{}"));
    }

    [Theory]
    [InlineData("{\"error\":\"model is not available\"}")]
    [InlineData("{\"message\":\"invalid model slug\"}")]
    [InlineData("{\"message\":\"model does not exist\"}")]
    public void ModelSpecificBadRequestInvalidatesCache(string body)
    {
        Assert.True(ChatGptModelFailurePolicy.ShouldInvalidate(
            HttpStatusCode.BadRequest, body));
    }

    [Fact]
    public void UnrelatedBadRequestKeepsCache()
    {
        Assert.False(ChatGptModelFailurePolicy.ShouldInvalidate(
            HttpStatusCode.BadRequest, "invalid tool arguments"));
    }
}
