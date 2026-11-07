using System.Globalization;
using System.Text.RegularExpressions;
using Lux3D.Core;
using Xunit;

namespace Lux3D.Core.Tests;

public sealed class LocalTextTests
{
    [Theory]
    [InlineData(1033, "zh-CN", "en")]
    [InlineData(1036, "zh-CN", "en")]
    [InlineData(1041, "zh-CN", "en")]
    [InlineData(2052, "en-US", "zh-Hans")]
    [InlineData(1028, "en-US", "zh-Hans")]
    [InlineData(3076, "en-US", "zh-Hans")]
    [InlineData(4, "en-US", "zh-Hans")]
    [InlineData(null, "zh-Hans", "zh-Hans")]
    [InlineData(null, "zh-Hant", "zh-Hans")]
    [InlineData(null, "zh-SG", "zh-Hans")]
    [InlineData(0, "zh-TW", "zh-Hans")]
    [InlineData(127, "zh-CN", "zh-Hans")]
    [InlineData(-1, "zh-CN", "zh-Hans")]
    [InlineData(int.MaxValue, "zh-CN", "zh-Hans")]
    [InlineData(null, "fr-FR", "en")]
    [InlineData(null, "invalid_!", "en")]
    [InlineData(null, null, "en")]
    public void HostLanguageWinsAndSystemIsOnlyFallback(int? host, string? system, string expected)
    {
        CultureInfo culture = CultureInfo.CurrentCulture, ui = CultureInfo.CurrentUICulture;
        Assert.Equal(expected, LocalText.ResolveLanguage(host, system));
        Assert.Same(culture, CultureInfo.CurrentCulture);
        Assert.Same(ui, CultureInfo.CurrentUICulture);
    }

    [Fact]
    public void EnglishIsDefaultAndUnknownMessagesRemainUnchanged()
    {
        Assert.Equal("Generate (uses service credits)", LocalText.Get("Generate (uses service credits)"));
        Assert.Equal("生成（将消耗服务额度）", LocalText.Get("Generate (uses service credits)", "zh-Hans"));
        const string custom = "Customer model 你好 / custom provider message";
        Assert.Equal(custom, LocalText.Get(custom, "zh-Hans"));
        Assert.Equal(custom, LocalText.RecordedMessage(custom, "en"));
        Assert.Null(LocalText.RecordedMessage(null, "en"));
    }

    [Fact]
    public void CatalogHasEnglishSourcesAndMatchingInterpolationArguments()
    {
        Assert.True(LocalText.ChineseCatalog.Count > 450);
        foreach (var pair in LocalText.ChineseCatalog)
        {
            Assert.DoesNotMatch("[\\p{IsCJKUnifiedIdeographs}]", pair.Key);
            Assert.Equal(pair.Value, LocalText.Get(pair.Key, "zh-Hans"));
            Assert.Equal(pair.Key, LocalText.Get(pair.Key, "en"));
            string[] Fields(string value) => Regex.Matches(value, @"\{\d+(?:,[^}:]+)?(?::[^}]+)?\}").Select(m => m.Value).Order().ToArray();
            Assert.Equal(Fields(pair.Key), Fields(pair.Value));
            object[] data = Enumerable.Range(0, 16).Select(i => (object)(i + .25)).ToArray();
            _ = string.Format(CultureInfo.InvariantCulture, pair.Key, data);
            _ = string.Format(CultureInfo.GetCultureInfo("zh-CN"), pair.Value, data);
        }
    }

    [Fact]
    public void EnglishUiRetainsTheUsersNumericCulture()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal("Height: 1,5", LocalText.Format("Height: {0:0.0}", 1.5));
            Assert.Equal("en", LocalText.Language);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void KnownHistoricalDiagnosticsFollowCurrentLanguageWithoutChangingUnknownText()
    {
        Assert.Equal("The task record is corrupt.", LocalText.RecordedMessage("任务记录损坏。", "en"));
        Assert.Equal("任务记录损坏。", LocalText.RecordedMessage("The task record is corrupt.", "zh-Hans"));
        Assert.Equal("任务记录损坏。 extra user text", LocalText.RecordedMessage("任务记录损坏。 extra user text", "en"));
    }
}
