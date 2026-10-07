using Airshelf.Core.Normalization;

namespace Airshelf.Tests;

public sealed class TextNormalizerTests
{
    [Theory]
    [InlineData("LOGISTEED RADIONOMICS", "LOGISTEEDRADIONOMICS")]
    [InlineData("SONYSONPO QUEST", "SONYSONPOQUEST")]     // 空白揺れ …
    [InlineData("SONY SONPO QUEST", "SONYSONPOQUEST")]    // … を同じキーへ
    [InlineData("ＴＯＫＹＯ ＦＭ", "TOKYOFM")]            // 全角英字 → 半角大文字
    [InlineData("ｊ－ｗａｖｅ", "J-WAVE")]                 // 全角小文字 → 半角大文字
    [InlineData("  余白  ", "余白")]                      // 前後空白除去
    [InlineData("", "")]
    public void ToMatchKey_NormalizesWidthWhitespaceAndCase(string input, string expected)
    {
        Assert.Equal(expected, TextNormalizer.ToMatchKey(input));
    }

    [Fact]
    public void ToMatchKey_NullReturnsEmpty()
    {
        Assert.Equal(string.Empty, TextNormalizer.ToMatchKey(null));
    }

    [Fact]
    public void ToMatchKey_FullwidthAmpersandBecomesHalfwidth()
    {
        // アルコ＆ピース(全角 &)と アルコ&ピース(半角 &)を同一視
        Assert.Equal(
            TextNormalizer.ToMatchKey("アルコ&ピース"),
            TextNormalizer.ToMatchKey("アルコ＆ピース"));
    }
}
