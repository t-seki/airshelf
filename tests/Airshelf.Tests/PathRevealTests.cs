using Airshelf.Gui.Core;

namespace Airshelf.Tests;

/// <summary>
/// 台帳の Windows パス(ローカル / UNC)の分解。**このテストは Linux 上でも走る**ため、
/// System.IO.Path に依存しないこと(Linux では '\' が区切りにならず、Windows パス全体が
/// 1 要素になる)を実質的に検証している。#82 で踏んだ OS 依存の罠と同種。
/// </summary>
public sealed class PathRevealTests
{
    [Theory]
    [InlineData(@"I:\radio\radirec\RADIO\ニッポン放送\ANN0\2026-06-12.m4a", "2026-06-12.m4a")]
    [InlineData(@"\\nas\share\radio\ann0.m4a", "ann0.m4a")]
    [InlineData(@"a.m4a", "a.m4a")] // 区切り無し
    [InlineData("", "")]
    public void FileName_takes_the_last_segment(string path, string expected) =>
        Assert.Equal(expected, PathReveal.FileName(path));

    [Fact]
    public void FileName_ignores_trailing_separator() =>
        Assert.Equal("ANN0", PathReveal.FileName(@"I:\radio\ANN0\"));

    [Theory]
    [InlineData(@"I:\radio\ANN0\a.m4a", @"I:\radio\ANN0")]
    [InlineData(@"\\nas\share\radio\a.m4a", @"\\nas\share\radio")]
    [InlineData(@"\\nas\share\radio", @"\\nas\share")]
    public void Parent_walks_up_one_level(string path, string expected) =>
        Assert.Equal(expected, PathReveal.Parent(path));

    [Fact]
    public void Parent_normalizes_drive_relative_to_root() =>
        // "I:" はドライブ相対(カレントディレクトリ)を指すので、開く先としてはルートに正規化する。
        Assert.Equal(@"I:\", PathReveal.Parent(@"I:\radio"));

    [Theory]
    [InlineData(@"I:\")] // ドライブルート
    [InlineData(@"\\nas\share")] // UNC の共有ルート(親 \\nas は共有名を欠き開けない)
    [InlineData(@"\\nas")] // UNC のサーバルート。親は区切りだけになるので遡らない
    [InlineData(@"\radio")] // 親がルートのみ
    [InlineData("a.m4a")] // 区切り無し
    [InlineData("")]
    [InlineData(null)]
    public void Parent_stops_at_roots(string? path) =>
        // 「カレントドライブのルート」を意味する `\` を返してしまわないこと。祖先探索が
        // 無関係な場所に着地するのを防ぐ。
        Assert.Null(PathReveal.Parent(path));

    [Fact]
    public void NearestExistingDirectory_returns_the_first_existing_ancestor()
    {
        // 番組フォルダごと消えていて、放送局フォルダまで遡ると存在する状況。
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"I:\radio\RADIO\ニッポン放送",
            @"I:\radio\RADIO",
        };

        var found = PathReveal.NearestExistingDirectory(
            @"I:\radio\RADIO\ニッポン放送\ANN0\2026-06-12.m4a",
            existing.Contains);

        Assert.Equal(@"I:\radio\RADIO\ニッポン放送", found);
    }

    [Fact]
    public void NearestExistingDirectory_skips_the_path_itself()
    {
        // 引数自身は候補にしない(「存在しないファイル」に対して呼ぶ関数なので、必ず親から始める)。
        var found = PathReveal.NearestExistingDirectory(
            @"I:\radio\ANN0",
            p => p == @"I:\radio\ANN0" || p == @"I:\radio");

        Assert.Equal(@"I:\radio", found);
    }

    [Theory]
    // 通常のファイルパス(末尾がバックスラッシュでない)はそのまま囲むだけ。
    [InlineData(@"I:\radio\ANN0\2026-06-12.m4a", "\"I:\\radio\\ANN0\\2026-06-12.m4a\"")]
    [InlineData(@"\\nas\share\radio\a.m4a", "\"\\\\nas\\share\\radio\\a.m4a\"")]
    // ドライブルートは末尾がバックスラッシュ。2 倍にしないと閉じ引用符がエスケープされて引数が壊れる。
    [InlineData(@"I:\", "\"I:\\\\\"")]
    [InlineData(@"\\nas\share\", "\"\\\\nas\\share\\\\\"")]
    public void QuoteForCommandLine_doubles_trailing_backslashes(string path, string expected) =>
        Assert.Equal(expected, PathReveal.QuoteForCommandLine(path));

    [Fact]
    public void NearestExistingDirectory_returns_null_when_nothing_exists() =>
        // NAS ごと不達のケース。呼び出し側は「見つかりません」と出して explorer を叩かない。
        Assert.Null(PathReveal.NearestExistingDirectory(@"\\nas\share\radio\a.m4a", _ => false));
}
