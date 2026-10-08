using Airshelf.Core.Configuration;
using Airshelf.Core.Normalization;

namespace Airshelf.Tests;

/// <summary>
/// 統合設定の無い兄弟フォルダの組から、programs.json に追記する統合設定の提案を組み立てる(#144)。
/// 実データの兄弟 8 組(<see cref="SiblingFoldersTests.RealFolders"/>)で、アルバム名と Folders の並びを確かめる。
/// </summary>
public sealed class SiblingProgramProposalsTests
{
    private static ProgramResolver Resolver(params ProgramAlias[] programs) => new(new ProgramMasterConfig { Programs = [.. programs] });

    private static ProgramAlias Alias(string station, string album, params string[] folders) => new()
    {
        Match = new ProgramMatch { Station = station, Folders = [.. folders] },
        Canonical = new ProgramCanonical { Album = album },
    };

    [Fact]
    public void 実データの8組から_印を除いたアルバム名とパートの順のFoldersを作る()
    {
        var groups = SiblingFolders.Find(SiblingFoldersTests.RealFolders);

        var proposals = SiblingProgramProposals.Build(groups, Resolver());

        Assert.Equal(
            Flatten(
            [
                ("TBSラジオ", "パンサー向井のふらっと", new[] { "パンサー向井のふらっと(1)", "パンサー向井のふらっと(2)" }),
                ("TBSラジオ", "土曜ワイドラジオTOKYOナイツのちゃきちゃき大放送", ["土曜ワイドラジオTOKYOナイツのちゃきちゃき大放送(1)", "土曜ワイドラジオTOKYOナイツのちゃきちゃき大放送(2)"]),
                ("TBSラジオ", "爆笑問題の日曜サンデー", ["爆笑問題の日曜サンデー (1)", "爆笑問題の日曜サンデー (2)"]),
                // 印の無いフォルダが先頭。アルバム名は元の表記(全角の ！)のまま = 今までの回のアルバム名と同じ
                ("ニッポン放送", "垣花正あなたとハッピー！", ["垣花正あなたとハッピー！", "垣花正あなたとハッピー！Part2"]),
                ("ニッポン放送", "辛坊治郎 ズーム そこまで言うか！", ["辛坊治郎 ズーム そこまで言うか！", "辛坊治郎 ズーム そこまで言うか！ Part1", "辛坊治郎 ズーム そこまで言うか！ Part2"]),
                // 時間帯は最初の数字で比べる(文字列順の 10時11時 → 8時9時 ではない)
                ("文化放送", "おとなりさん", ["おとなりさん 8時9時", "おとなりさん 9時10時", "おとなりさん 10時11時"]),
                ("文化放送", "てるのりのワルノリ", ["てるのりのワルノリ (1)", "てるのりのワルノリ (2)"]),
                ("文化放送", "ますだおかだ岡田圭右とアンタッチャブル柴田英嗣のおかしば", ["ますだおかだ岡田圭右とアンタッチャブル柴田英嗣のおかしば10時1", "ますだおかだ岡田圭右とアンタッチャブル柴田英嗣のおかしば11時1", "ますだおかだ岡田圭右とアンタッチャブル柴田英嗣のおかしば12時1"]),
            ]),
            Flatten(proposals
                .Select(p => (p.Station, p.Album, p.Folders.ToArray()))
                .OrderBy(p => p.Station, StringComparer.Ordinal)
                .ThenBy(p => p.Item3[0], StringComparer.Ordinal)));
        Assert.All(proposals, p => Assert.False(p.JoinsExisting));
    }

    // xunit v3 の Assert.Equal は ValueTuple の IEquatable を先に使い、中の配列を参照で比べる。文字列にして比べる(順序は保つ)。
    private static string[] Flatten(IEnumerable<(string Station, string Album, string[] Folders)> proposals) =>
        [.. proposals.Select(p => p.Station + "|" + p.Album + "|" + string.Join(";", p.Folders))];

    [Theory]
    [InlineData("番組（１）", "番組")]            // 全角の括弧・数字も除く
    [InlineData("番組　（２）", "番組")]          // 全角の空白も
    [InlineData("ＡＢＣラジオ Part3", "ＡＢＣラジオ")] // 残す部分は元の表記(全角英字)のまま
    [InlineData("番組！", "番組！")]              // 印が無ければそのまま
    [InlineData("  番組 (1)  ", "番組")]
    public void OriginalBaseName_は印を除き元の表記を残す(string folder, string expected)
        => Assert.Equal(expected, SiblingFolders.OriginalBaseName(folder));

    [Fact]
    public void 同じ番号ならフォルダ名の序数順_2桁の番号は数で比べる()
    {
        Assert.Equal(
            ["X", "X Part2", "X(2)", "X(10)"],
            SiblingFolders.OrderAsParts(["X(10)", "X Part2", "X(2)", "X"]).ToArray());
    }

    [Fact]
    public void 兄弟が統合番組に入っていれば_その統合番組へ足すフォルダだけを示す()
    {
        var group = new SiblingFolderGroup("文化放送", "おとなりさん",
            ["おとなりさん 10時11時", "おとなりさん 11時12時", "おとなりさん 8時9時", "おとなりさん 9時10時"]);
        var resolver = Resolver(Alias("文化放送", "おとなりさん(統合)", "おとなりさん 8時9時", "おとなりさん 9時10時", "おとなりさん 10時11時"));

        var proposal = Assert.Single(SiblingProgramProposals.Build([group], resolver));

        Assert.True(proposal.JoinsExisting);
        Assert.Equal("おとなりさん(統合)", proposal.Album);
        Assert.Equal(["おとなりさん 11時12時"], proposal.Folders);
    }

    [Fact]
    public void 今の設定で統合設定の無いフォルダが残らない組は提案しない()
    {
        // dry-run の後に利用者が programs.json に統合設定を足していた
        var group = new SiblingFolderGroup("TBSラジオ", "番組", ["番組(1)", "番組(2)"]);
        var resolver = Resolver(Alias("TBSラジオ", "番組", "番組(1)", "番組(2)"));

        Assert.Empty(SiblingProgramProposals.Build([group], resolver));
    }
}
