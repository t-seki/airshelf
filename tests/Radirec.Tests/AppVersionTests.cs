using Radirec.Gui.Core;

namespace Radirec.Tests;

public class AppVersionTests
{
    [Theory]
    [InlineData("0.3.0", "0.3.0")]
    [InlineData("0.0.0-dev", "0.0.0-dev")]
    [InlineData("0.3.0+abc123", "0.3.0")]           // build metadata を落とす
    [InlineData("0.3.0-rc1+abc123", "0.3.0-rc1")]   // prerelease は残し metadata だけ落とす
    [InlineData("  0.3.0  ", "0.3.0")]              // 前後空白を除去
    [InlineData("0.3.0 +abc123", "0.3.0")]          // '+' 直前の空白も残さない
    public void Format_strips_build_metadata_keeps_prerelease(string input, string expected) =>
        Assert.Equal(expected, AppVersion.Format(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+onlymetadata")]
    public void Format_falls_back_to_unknown_when_empty(string? input) =>
        Assert.Equal("unknown", AppVersion.Format(input));

    [Fact]
    public void Of_reads_informational_version_and_drops_metadata()
    {
        // テストアセンブリ自身の informational version から整形経路が通ること(smoke)。
        var text = AppVersion.Of(typeof(AppVersionTests).Assembly);

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.DoesNotContain('+', text);
    }
}
