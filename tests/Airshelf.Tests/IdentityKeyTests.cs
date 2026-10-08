using Airshelf.Core.Model;

namespace Airshelf.Tests;

public sealed class IdentityKeyTests
{
    private static readonly DateOnly Day = new(2026, 6, 12);

    [Fact]
    public void SameCanonicalAndDate_AreEqual()
    {
        var a = new IdentityKey("J-WAVE", "LOGISTEED RADIONOMICS", Day);
        var b = new IdentityKey("J-WAVE", "LOGISTEED RADIONOMICS", Day);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void SpacingAndCaseVariations_AreEqual()
    {
        // 表記ゆれ(空白・全角)を同一性で吸収する
        var a = new IdentityKey("J-WAVE", "SONYSONPO QUEST FOR THE FUTURE", Day);
        var b = new IdentityKey("ｊ－ｗａｖｅ", "SONY SONPO QUEST FOR THE FUTURE", Day);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void DifferentDate_AreNotEqual()
    {
        var a = new IdentityKey("J-WAVE", "番組", Day);
        var b = new IdentityKey("J-WAVE", "番組", Day.AddDays(1));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DifferentProgram_AreNotEqual()
    {
        var a = new IdentityKey("J-WAVE", "番組A", Day);
        var b = new IdentityKey("J-WAVE", "番組B", Day);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Value_IsStablePipeDelimitedForm()
    {
        var key = new IdentityKey("J-WAVE", "LOGISTEED RADIONOMICS", Day);

        Assert.Equal("J-WAVE|LOGISTEEDRADIONOMICS|2026-06-12", key.Value);
        Assert.Equal(key.Value, key.ToString());
    }

    [Fact]
    public void PreservesCanonicalDisplayValues()
    {
        var key = new IdentityKey("J-WAVE", "LOGISTEED RADIONOMICS", Day);

        Assert.Equal("J-WAVE", key.CanonicalStation);
        Assert.Equal("LOGISTEED RADIONOMICS", key.CanonicalProgram);
        Assert.Equal(Day, key.BroadcastDate);
    }
}
