using PLC.Shared.DistributedConcurrentDictionary;

namespace PLC.Shared.DistributedConcurrentDictionary.Tests;

public sealed class FusionRedisDictionaryOptionsTests
{
    [Fact]
    public void WithTopic_AppendsSegment_ToTrimmedBasePrefix()
    {
        FusionRedisDictionaryOptions template = new FusionRedisDictionaryOptions
        {
            KeyPrefix = "myapp:",
            FailureMode = FusionRedisFailureMode.ReadOnlyStale,
        };

        FusionRedisDictionaryOptions scoped = template.WithTopic("orders");

        Assert.Equal("myapp:orders", scoped.KeyPrefix);
        Assert.Equal(FusionRedisFailureMode.ReadOnlyStale, scoped.FailureMode);
        Assert.Equal("myapp:", template.KeyPrefix);
    }

    [Fact]
    public void WithTopic_TrimsTopicColons()
    {
        FusionRedisDictionaryOptions template = new FusionRedisDictionaryOptions { KeyPrefix = "app" };
        FusionRedisDictionaryOptions scoped = template.WithTopic(":invoices:");
        Assert.Equal("app:invoices", scoped.KeyPrefix);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithTopic_RejectsBlankTopic(string? topic)
    {
        FusionRedisDictionaryOptions template = new FusionRedisDictionaryOptions { KeyPrefix = "app" };
        Assert.ThrowsAny<ArgumentException>(() => template.WithTopic(topic!));
    }

    [Fact]
    public void WithTopic_RejectsEmptyKeyPrefix()
    {
        FusionRedisDictionaryOptions template = new FusionRedisDictionaryOptions { KeyPrefix = "   " };
        Assert.Throws<InvalidOperationException>(() => template.WithTopic("t"));
    }
}
