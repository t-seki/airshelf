using System.IO.Abstractions.TestingHelpers;
using Radirec.Core.Attention;
using Radirec.Core.Configuration;
using Radirec.Core.Ledger;
using Radirec.Core.Normalization;
using Radirec.Core.Reapply;
using Radirec.Core.Sources;

namespace Radirec.Tests;

/// <summary>
/// 要対応の計算(#155・ADR-0018)。台帳の記録と番組の設定だけから、統合設定の無い兄弟フォルダの組を出す。
/// ファイルシステムは空の <see cref="MockFileSystem"/> を渡し、取込元・出力先のファイルが無くても計算できる(NAS に触れない)ことを確かめる。
/// </summary>
public sealed class AttentionCalculatorTests
{
    private const string Tbs = "TBSラジオ";
    private const string Qr = "文化放送";

    // らじれこの {局}/{番組フォルダ}/[局]番組(TimeFree)_yyyyMMddHHmmss.m4a。ファイル名の番組部は識別に使わない。
    private static LedgerEntry Entry(string station, string folder, string stamp = "20240923083000") => new()
    {
        IdentityKey = $"{station}|{folder}|{stamp}",
        Station = station,
        Program = folder,
        BroadcastDate = new DateOnly(2024, 9, 23),
        SourcePath = $"/radio/{station}/{folder}/[{station}]{folder}(TimeFree)_{stamp}.m4a",
        OutputPath = $"/out/{station}/{folder}/{folder} 2024-09-23.m4a",
        RuleVersion = "1",
        UsedFallback = true,
        ImportedAtUtc = DateTimeOffset.UnixEpoch,
    };

    private static ProgramAlias Merged(string station, string album, params string[] folders) => new()
    {
        Match = new ProgramMatch { Station = station, Folders = [.. folders] },
        Canonical = new ProgramCanonical { Album = album },
    };

    private static KeptApartSiblings Kept(string station, params string[] folders) => new()
    {
        Match = new ProgramMatch { Station = station, Folders = [.. folders] },
    };

    private static AttentionItems Compute(IEnumerable<LedgerEntry> ledger, ProgramMasterConfig? programs)
    {
        var resolver = new LedgerEpisodeResolver(
            new MockFileSystem().Path,
            new RajirekoImporter(),
            new StationResolver(new StationMasterConfig()),
            new ProgramResolver(programs ?? new ProgramMasterConfig()));
        return AttentionCalculator.Compute(ledger, resolver, new AttentionPrograms(programs));
    }

    [Fact]
    public void Lists_sibling_folders_without_settings()
    {
        var items = Compute([Entry(Tbs, "X(1)"), Entry(Tbs, "X(2)"), Entry(Tbs, "Y")], new ProgramMasterConfig());

        var sibling = Assert.Single(items.UnintegratedSiblings);
        Assert.Equal(Tbs, sibling.Group.Station);
        Assert.Equal(["X(1)", "X(2)"], sibling.Group.Folders);
        Assert.Equal(["X(1)", "X(2)"], sibling.FoldersWithoutSetting);
        Assert.Null(sibling.MergedAlbum);
        Assert.False(sibling.IsKeptApartGrown);
    }

    [Fact]
    public void Integrated_group_is_not_listed()
    {
        var programs = new ProgramMasterConfig { Programs = [Merged(Tbs, "X", "X(1)", "X(2)")] };

        var items = Compute([Entry(Tbs, "X(1)"), Entry(Tbs, "X(2)")], programs);

        Assert.Empty(items.UnintegratedSiblings);
    }

    [Fact]
    public void Group_partly_in_a_merged_program_names_the_album_W124()
    {
        var programs = new ProgramMasterConfig { Programs = [Merged(Tbs, "X!", "X(1)", "X(2)")] };

        var items = Compute([Entry(Tbs, "X(1)"), Entry(Tbs, "X(2)"), Entry(Tbs, "X(3)")], programs);

        var sibling = Assert.Single(items.UnintegratedSiblings);
        Assert.Equal("X!", sibling.MergedAlbum);
        Assert.Equal(["X(3)"], sibling.FoldersWithoutSetting);
    }

    [Fact]
    public void Kept_apart_group_is_not_listed()
    {
        var programs = new ProgramMasterConfig { KeepSeparate = [Kept(Tbs, "X(1)", "X(2)")] };

        var items = Compute([Entry(Tbs, "X(1)"), Entry(Tbs, "X(2)")], programs);

        Assert.Empty(items.UnintegratedSiblings);
    }

    [Fact]
    public void Kept_apart_group_matches_by_the_same_key_as_ingest()
    {
        // 照合は取り込みと同じキー(全角・半角の違いは同じフォルダ)。
        var programs = new ProgramMasterConfig { KeepSeparate = [Kept(Tbs, "X（1）", "X（2）")] };

        var items = Compute([Entry(Tbs, "X(1)"), Entry(Tbs, "X(2)")], programs);

        Assert.Empty(items.UnintegratedSiblings);
    }

    [Fact]
    public void Kept_apart_group_that_grew_is_listed_again_W125()
    {
        var programs = new ProgramMasterConfig { KeepSeparate = [Kept(Tbs, "X(1)", "X(2)")] };

        var items = Compute([Entry(Tbs, "X(1)"), Entry(Tbs, "X(2)"), Entry(Tbs, "X(3)")], programs);

        var sibling = Assert.Single(items.UnintegratedSiblings);
        Assert.True(sibling.IsKeptApartGrown);
        Assert.Equal(["X(3)"], sibling.FoldersNewToKeptApart);
        Assert.Equal(["X(1)", "X(2)", "X(3)"], sibling.Group.Folders);
    }

    [Fact]
    public void Kept_apart_on_another_station_does_not_hide_the_group()
    {
        var programs = new ProgramMasterConfig { KeepSeparate = [Kept(Qr, "X(1)", "X(2)")] };

        var items = Compute([Entry(Tbs, "X(1)"), Entry(Tbs, "X(2)")], programs);

        var sibling = Assert.Single(items.UnintegratedSiblings);
        Assert.Equal(Tbs, sibling.Group.Station);
        Assert.False(sibling.IsKeptApartGrown);
    }

    [Fact]
    public void Same_folder_names_on_different_stations_are_not_siblings()
    {
        var items = Compute([Entry(Tbs, "X(1)"), Entry(Qr, "X(2)")], new ProgramMasterConfig());

        Assert.Empty(items.UnintegratedSiblings);
    }

    [Fact]
    public void Programs_file_unused_lists_no_siblings()
    {
        // ProgramsFile が空(programs.json を使わない設定)では、統合も「分けておく」も記録できないので出さない(#155 の決定 1)。
        var items = Compute([Entry(Tbs, "X(1)"), Entry(Tbs, "X(2)")], programs: null);

        Assert.Empty(items.UnintegratedSiblings);
    }

    [Fact]
    public void Non_rajireko_entries_are_ignored()
    {
        var radikool = Entry(Tbs, "X(2)") with { SourcePath = "/radikool/X(2).mp3" };

        var items = Compute([Entry(Tbs, "X(1)"), radikool], new ProgramMasterConfig());

        Assert.Empty(items.UnintegratedSiblings);
    }

    [Fact]
    public void Resolver_decides_as_recorded_like_reapply()
    {
        // 再適用の「記録どおりか」と同じ判定を共有する(#172 の食い違いの計算もこれを使う)。
        var resolver = new LedgerEpisodeResolver(
            new MockFileSystem().Path,
            new RajirekoImporter(),
            new StationResolver(new StationMasterConfig()),
            new ProgramResolver(new ProgramMasterConfig { Programs = [Merged(Tbs, "X", "X(1)", "X(2)")] }));

        var asRecorded = resolver.Resolve(Entry(Tbs, "Y"));
        var drifted = resolver.Resolve(Entry(Tbs, "X(1)")); // 台帳は X(1)・パート 0、今の設定は X・パート 1

        Assert.True(asRecorded!.IsAsRecorded);
        Assert.False(drifted!.IsAsRecorded);
        Assert.Equal("X", drifted.Episode!.Program);
        Assert.Equal(new ProgramFolder(Tbs, "X(1)"), drifted.Folder);
        Assert.Null(resolver.Resolve(Entry(Tbs, "Z") with { SourcePath = "/radikool/Z.mp3" }));
    }
}
