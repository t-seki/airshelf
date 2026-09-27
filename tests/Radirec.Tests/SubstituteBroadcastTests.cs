using System.IO.Abstractions.TestingHelpers;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Radirec.Core.Attention;
using Radirec.Core.Configuration;
using Radirec.Core.Ledger;
using Radirec.Core.Model;
using Radirec.Core.Normalization;
using Radirec.Core.Reapply;
using Radirec.Core.Sources;
using Radirec.Gui.Core;
using Radirec.Hosting;

namespace Radirec.Tests;

/// <summary>
/// 差し替え放送(ADR-0021)の判定・設定・保留のストア・要対応・トーストの文言。取り込みの流れは
/// <see cref="SubstituteBroadcastIngestTests"/>。
/// </summary>
public sealed class SubstituteBroadcastTests : IDisposable
{
    private const string Lf = "ニッポン放送";
    private const string Folder = "パンサー向井のチャリで30分";
    private const string Substitute = "辛坊治郎 ズーム そこまで言うか！";

    private readonly string _dir = Directory.CreateTempSubdirectory("radirec-substitute-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static FilenameProgramDecision Decision(
        FilenameProgramTreatment treat, string? album = null, string name = Substitute, string station = Lf, string folder = Folder) => new()
    {
        Match = new FilenameProgramMatch { Station = station, Folder = folder, FilenameProgram = name },
        Treat = treat,
        Album = album,
    };

    private static ProgramResolver Resolver(params FilenameProgramDecision[] decisions)
        => new(new ProgramMasterConfig { FilenamePrograms = [.. decisions] });

    // ---- 判定(ProgramResolver.JudgeFilenameProgram)----

    [Theory]
    [InlineData("パンサー向井のチャリで30分")]
    [InlineData("パンサー向井の チャリで３０分 ゲスト回")] // 空白・全角数字は照合キーで吸収する
    [InlineData("")] // 名前が無ければ疑う手がかりが無い
    public void フォルダ名を含む回は含む扱い(string name)
    {
        var judgement = Resolver().JudgeFilenameProgram(Lf, Folder, name);

        Assert.Equal(FilenameProgramVerdict.ContainsFolder, judgement.Verdict);
    }

    [Fact]
    public void 含まず判断も無い回は判断待ち()
    {
        Assert.Equal(FilenameProgramVerdict.Unjudged, Resolver().JudgeFilenameProgram(Lf, Folder, Substitute).Verdict);
    }

    [Theory]
    [InlineData(FilenameProgramTreatment.SameProgram, FilenameProgramVerdict.SameProgram)]
    [InlineData(FilenameProgramTreatment.Skip, FilenameProgramVerdict.Skip)]
    public void 判断があればそれに従う(FilenameProgramTreatment treat, FilenameProgramVerdict expected)
    {
        Assert.Equal(expected, Resolver(Decision(treat)).JudgeFilenameProgram(Lf, Folder, Substitute).Verdict);
    }

    [Fact]
    public void 判断は照合キーで引く_局もフォルダも名前も()
    {
        var resolver = Resolver(Decision(FilenameProgramTreatment.OtherProgram, " 辛坊治郎 ズーム ", name: "辛坊治郎ズームそこまで言うか！"));

        var judgement = resolver.JudgeFilenameProgram(Lf, "パンサー向井の チャリで30分", "辛坊治郎 ズーム そこまで言うか!");

        Assert.Equal(new FilenameProgramJudgement(FilenameProgramVerdict.OtherProgram, "辛坊治郎 ズーム"), judgement);
    }

    [Fact]
    public void 判断は局が違えば効かない()
    {
        var resolver = Resolver(Decision(FilenameProgramTreatment.Skip));

        Assert.Equal(FilenameProgramVerdict.Unjudged, resolver.JudgeFilenameProgram("TBSラジオ", Folder, Substitute).Verdict);
    }

    [Fact]
    public void 別の番組なのにアルバム名が無ければ設定の誤り()
    {
        var ex = Assert.Throws<ProgramsConfigException>(() => Resolver(Decision(FilenameProgramTreatment.OtherProgram, album: " ")));

        Assert.Contains("Album", ex.Message);
        Assert.Contains(Substitute, ex.Message);
    }

    [Fact]
    public void 別の番組の回は判断のアルバムで組み_パートを持たない()
    {
        var programs = new ProgramMasterConfig
        {
            // フォルダが統合番組に入っていても、別の番組の回はパートにならない。
            Programs = [new ProgramAlias
            {
                Match = new ProgramMatch { Station = Lf, Folders = [Folder, Folder + "Part2"] },
                Canonical = new ProgramCanonical { Album = "チャリ" },
            }],
            FilenamePrograms = [Decision(FilenameProgramTreatment.OtherProgram, "辛坊治郎 ズーム")],
        };
        var assembler = new EpisodeAssembler(new StationResolver(new StationMasterConfig()), new ProgramResolver(programs));
        var source = new RajirekoImporter().Parse(Rec(Substitute));

        var episode = assembler.Assemble(source);

        Assert.Equal("辛坊治郎 ズーム", episode.Program);
        Assert.Null(episode.PartIndex);
        Assert.Equal(FilenameProgramVerdict.OtherProgram, episode.FilenameVerdict);
        Assert.Equal(Substitute, episode.FilenameProgram);
    }

    [Fact]
    public void らじれこのファイル名から局_録音種別_日時を除いた部分がファイル名の番組名()
    {
        var parsed = new RajirekoImporter().Parse(Rec(Substitute));

        Assert.Equal(Substitute, parsed.FilenameProgram);
    }

    private static RecordingFile Rec(string name, string folder = Folder, string stamp = "20241021000000") => new()
    {
        FilePath = $"/radio/{Lf}/{folder}/[{Lf}]{name}(TimeFree)_{stamp}.m4a",
        FileName = $"[{Lf}]{name}(TimeFree)_{stamp}.m4a",
        StationFolder = Lf,
        ProgramFolder = folder,
        Extension = ".m4a",
    };

    // ---- programs.json ----

    [Fact]
    public void programs_json_の判断を名前で読める()
    {
        var config = ConfigLoader.Parse<ProgramMasterConfig>("""
            { "FilenamePrograms": [
                { "Match": { "Station": "ニッポン放送", "Folder": "パンサー向井のチャリで30分", "FilenameProgram": "辛坊治郎 ズーム そこまで言うか！" },
                  "Treat": "OtherProgram", "Album": "辛坊治郎 ズーム", "Note": "月曜の差し替え" } ] }
            """);

        var decision = Assert.Single(config.FilenamePrograms);
        Assert.Equal(FilenameProgramTreatment.OtherProgram, decision.Treat);
        Assert.Equal("辛坊治郎 ズーム", decision.Album);
        Assert.Equal("月曜の差し替え", decision.Note);
        Assert.Equal(Substitute, decision.Match.FilenameProgram);
    }

    [Fact]
    public void Treat_を書き忘れたら読めない()
    {
        // 省略を「この番組の回」(列挙の既定値)と読むと、差し替え放送が黙ってフォルダの番組に入る。
        Assert.ThrowsAny<JsonException>(() => ConfigLoader.Parse<ProgramMasterConfig>("""
            { "FilenamePrograms": [ { "Match": { "Station": "a", "Folder": "b", "FilenameProgram": "c" } } ] }
            """));
    }

    [Fact]
    public void 書き手は判断を名前で書き_空なら書かない()
    {
        var path = Path.Combine(_dir, "programs.json");
        File.WriteAllText(path, "{}");

        ProgramsFileWriter.Write(ProgramsFileWriter.Read(path), new ProgramMasterConfig(), DateTime.Now);
        Assert.DoesNotContain("FilenamePrograms", File.ReadAllText(path));

        var config = new ProgramMasterConfig { FilenamePrograms = [Decision(FilenameProgramTreatment.Skip)] };
        ProgramsFileWriter.Write(ProgramsFileWriter.Read(path), config, DateTime.Now);
        var written = File.ReadAllText(path);
        Assert.Contains("\"Treat\": \"Skip\"", written);
        Assert.DoesNotContain("\"Album\"", written); // null は書かない
        Assert.Equal(FilenameProgramTreatment.Skip, ProgramsFileWriter.Read(path).Config.FilenamePrograms.Single().Treat);
    }

    // ---- 保留のストア(SqliteLedger)----

    private static readonly DateTimeOffset At = new(2026, 9, 27, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 保留を記録して読み_消せる()
    {
        var db = Path.Combine(_dir, "ledger.db");
        using (var ledger = new SqliteLedger(db))
        {
            IHeldRecordingStore store = ledger;
            Assert.False(store.IsHeld("/a.m4a"));
            store.Record(new HeldRecording("/a.m4a", Lf, Folder, Substitute, At));
            store.Record(new HeldRecording("/b.m4a", Lf, Folder, Substitute, At));
            store.Clear("/b.m4a");
            Assert.True(store.IsHeld("/a.m4a"));
        }

        // 再起動を跨いで残る。取込失敗のストアとは別のテーブル。
        using var readOnly = new SqliteLedger(db, readOnly: true);
        Assert.Equal([new HeldRecording("/a.m4a", Lf, Folder, Substitute, At)], readOnly.ListHeld());
        Assert.Empty(readOnly.List());
    }

    [Fact]
    public void 保留のテーブルが無い古い台帳は_読み取り専用で開いても空()
    {
        var db = Path.Combine(_dir, "old.db");
        using (var connection = new SqliteConnection($"Data Source={db};Pooling=False"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "CREATE TABLE imports (identity_key TEXT, broadcast_time TEXT, part_index INTEGER);";
            cmd.ExecuteNonQuery();
        }

        using var readOnly = new SqliteLedger(db, readOnly: true);

        Assert.Empty(readOnly.ListHeld());
        Assert.False(((IHeldRecordingStore)readOnly).IsHeld("/a.m4a"));
    }

    // ---- 要対応(AttentionCalculator)----

    private static LedgerEntry Entry(string name, string stamp, string folder = Folder, string? album = null) => new()
    {
        IdentityKey = $"{Lf}|{album ?? folder}|{stamp}",
        Station = Lf,
        Program = album ?? folder,
        BroadcastDate = new DateOnly(2024, 10, 21),
        SourcePath = $"/radio/{Lf}/{folder}/[{Lf}]{name}(TimeFree)_{stamp}.m4a",
        OutputPath = $"/out/{Lf}/{album ?? folder}/{stamp}.m4a",
        RuleVersion = "1",
        UsedFallback = true,
        ImportedAtUtc = DateTimeOffset.UnixEpoch,
    };

    private static AttentionItems Attention(
        IEnumerable<LedgerEntry> ledger, ProgramMasterConfig programs, IEnumerable<HeldRecording>? held = null)
    {
        var resolver = new LedgerEpisodeResolver(
            new MockFileSystem().Path, new RajirekoImporter(),
            new StationResolver(new StationMasterConfig()), new ProgramResolver(programs));
        return AttentionCalculator.Compute(ledger, resolver, new AttentionPrograms(programs), held: held);
    }

    [Fact]
    public void 保留した回と取り込み済みの判断待ちの回を_組ごとにまとめる()
    {
        var ledger = new[]
        {
            Entry(Folder, "20241014000000"),         // 正規の回(出さない)
            Entry(Substitute, "20241021000000"),     // 取り込み済みの判断待ち
            Entry("辛坊治郎 ズーム そこまで言うか!", "20241209000000"), // 表記ゆれ(半角!)は同じ組
        };
        var held = new[] { new HeldRecording("/radio/x.m4a", Lf, Folder, Substitute, At) };

        var items = Attention(ledger, new ProgramMasterConfig(), held);

        var group = Assert.Single(items.SubstituteSuspects);
        Assert.Equal(new SubstituteSuspectGroup(Lf, Folder, Substitute, HeldCount: 1, ImportedCount: 2), group);
        Assert.Equal(1, items.Count);
        Assert.Empty(items.SettingsDrift);
    }

    [Fact]
    public void 判断を書けば_ストアに残っている保留も取り込み済みの回も出さない()
    {
        var programs = new ProgramMasterConfig { FilenamePrograms = [Decision(FilenameProgramTreatment.SameProgram)] };
        var held = new[] { new HeldRecording("/radio/x.m4a", Lf, Folder, Substitute, At) };

        var items = Attention([Entry(Substitute, "20241021000000")], programs, held);

        Assert.Empty(items.SubstituteSuspects);
        Assert.Equal(0, items.Count);
    }

    [Fact]
    public void 取り込まないと判断した取り込み済みの回は_消す予定に出し_食い違いには出さない()
    {
        var programs = new ProgramMasterConfig { FilenamePrograms = [Decision(FilenameProgramTreatment.Skip)] };

        var items = Attention([Entry(Substitute, "20241021000000"), Entry(Substitute, "20241209000000")], programs);

        Assert.Equal([new PendingRemovalGroup(Lf, Folder, Folder, Substitute, 2)], items.PendingRemovals);
        Assert.Empty(items.SettingsDrift);
        Assert.Empty(items.SubstituteSuspects);
        Assert.Equal(1, items.Count);
    }

    [Fact]
    public void 別の番組と判断した取り込み済みの回は_食い違いとして出る()
    {
        var programs = new ProgramMasterConfig
        {
            FilenamePrograms = [Decision(FilenameProgramTreatment.OtherProgram, "辛坊治郎 ズーム")],
        };

        var items = Attention([Entry(Substitute, "20241021000000")], programs);

        Assert.Equal([new SettingsDriftGroup(Lf, Folder, "辛坊治郎 ズーム", 1)], items.SettingsDrift);
        Assert.Empty(items.PendingRemovals);
    }

    // ---- トースト ----

    [Fact]
    public void 保留のトーストは判断待ちの件数と_組ごとの行()
    {
        var texts = IngestToastFormatter.BuildHeldTexts(new IngestHeldPayload
        {
            HeldCount = 3,
            Groups =
            [
                new IngestHeldGroupPayload { Folder = Folder, FilenameProgram = Substitute, Count = 2 },
                new IngestHeldGroupPayload { Folder = "日向坂46の「ひ」", FilenameProgram = "プロ野球日本シリーズ", Count = 1 },
            ],
        });

        Assert.Equal(
            [
                "判断待ち 3件（差し替え放送の疑い）",
                $"{Substitute}（{Folder}）2件",
                "プロ野球日本シリーズ（日向坂46の「ひ」）",
            ],
            texts);
    }
}
