using Airshelf.Core.Configuration;
using Airshelf.Hosting;

namespace Airshelf.Tests;

// 「保存して適用」(#154)で、確かめたときの下書きと、保存して読み直した設定が同じかを値で判定する。
public sealed class ReapplySettingsTests
{
    private static ReapplySettings Settings(string album = "安住紳一郎の日曜天国", string? note = null, string station = "TBS") => new(
        new StationMasterConfig { Stations = [new StationAlias { Canonical = "TBSラジオ", Aliases = [station] }] },
        new ProgramMasterConfig
        {
            Programs =
            [
                new ProgramAlias
                {
                    Match = new ProgramMatch { Station = "TBSラジオ", Folders = ["安住(1)", "安住(2)"] },
                    Canonical = new ProgramCanonical { Album = album },
                    Note = note,
                },
            ],
            KeepSeparate = [new KeptApartSiblings { Match = new ProgramMatch { Station = "文化放送", Folders = ["A(1)", "A(2)"] } }],
        });

    [Fact]
    public void Same_content_read_again_is_same_even_as_different_objects()
    {
        var draft = Settings();
        var reread = Settings(); // 保存してから読み直すと、同じ中身の別のインスタンスになる

        Assert.NotSame(draft.Programs, reread.Programs);
        Assert.True(draft.HasSameValuesAs(reread));
    }

    [Fact]
    public void Saved_then_reread_from_json_is_same()
    {
        var draft = Settings(note: "時間帯で 2 つに分けて録っている");
        var json = System.Text.Encoding.UTF8.GetString(ProgramsFileWriter.Serialize(draft.Programs));
        var reread = new ReapplySettings(draft.Stations, ConfigLoader.Parse<ProgramMasterConfig>(json));

        Assert.True(draft.HasSameValuesAs(reread));
    }

    [Theory]
    [InlineData("安住紳一郎の日曜天国!", null, "TBS")] // 番組の設定が違う
    [InlineData("安住紳一郎の日曜天国", "メモ", "TBS")] // Note だけ違う
    [InlineData("安住紳一郎の日曜天国", null, "TBSR")] // 放送局の設定が違う
    public void Any_difference_is_not_same(string album, string? note, string station)
        => Assert.False(Settings().HasSameValuesAs(Settings(album, note, station)));
}
