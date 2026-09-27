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
        FilenameProgramTreatment treat, string? album = null, string name = Substitute, string station = Lf, string folder = Folder,
        FilenameProgramMatchMode mode = FilenameProgramMatchMode.Exact) => new()
    {
        Match = new FilenameProgramMatch { Station = station, Folder = folder, FilenameProgram = name, Mode = mode },
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

    // ---- 照合の種類(#240)----

    private static FilenameProgramJudgement Judge(string name, params FilenameProgramDecision[] decisions)
        => Resolver(decisions).JudgeFilenameProgram(Lf, Folder, name);

    [Theory]
    [InlineData("辛坊治郎 ズーム そこまで言うか！", FilenameProgramVerdict.Skip)]
    [InlineData("辛坊治郎ズーム 特番", FilenameProgramVerdict.Skip)] // 照合キーの上で比べる(空白は吸収)
    [InlineData("特番 辛坊治郎 ズーム", FilenameProgramVerdict.Unjudged)] // 前方一致は先頭だけ
    public void 前方一致はファイル名の番組名の先頭で照合する(string name, FilenameProgramVerdict expected)
    {
        var prefix = Decision(FilenameProgramTreatment.Skip, name: "辛坊治郎 ズーム", mode: FilenameProgramMatchMode.Prefix);

        Assert.Equal(expected, Judge(name, prefix).Verdict);
    }

    [Theory]
    [InlineData("イースト駅前クリニック presents 川島明のねごと", FilenameProgramVerdict.SameProgram)]
    [InlineData("ブレインスリープ presents 川島明の ねごと 特別編", FilenameProgramVerdict.SameProgram)]
    [InlineData("川島明のそもそもの話", FilenameProgramVerdict.Unjudged)]
    public void 部分一致はファイル名の番組名のどこでも照合する(string name, FilenameProgramVerdict expected)
    {
        var contains = Decision(FilenameProgramTreatment.SameProgram, name: "川島明のねごと", mode: FilenameProgramMatchMode.Contains);

        Assert.Equal(expected, Judge(name, contains).Verdict);
    }

    [Fact]
    public void 完全一致は前方一致と部分一致より優先する()
    {
        const string name = "野村萬斎のラジオで福袋【ゲスト】羽生結弦";
        var decisions = new[]
        {
            Decision(FilenameProgramTreatment.SameProgram, name: "萬斎", mode: FilenameProgramMatchMode.Contains),
            Decision(FilenameProgramTreatment.OtherProgram, "福袋", name: "野村萬斎のラジオで福袋", mode: FilenameProgramMatchMode.Prefix),
            Decision(FilenameProgramTreatment.Skip, name: name),
        };

        Assert.Equal(FilenameProgramVerdict.Skip, Judge(name, decisions).Verdict);
        Assert.Equal(FilenameProgramVerdict.Skip, Judge(name, [.. decisions.Reverse()]).Verdict); // 書いた順によらない
    }

    [Fact]
    public void 前方一致は部分一致より優先し_照合キーが長い部分一致にも勝つ()
    {
        // 種類をまたいで長さは比べない。
        var decisions = new[]
        {
            Decision(FilenameProgramTreatment.Skip, name: "野村萬斎のラジオで福袋【ゲスト】", mode: FilenameProgramMatchMode.Contains),
            Decision(FilenameProgramTreatment.OtherProgram, "福袋", name: "野村", mode: FilenameProgramMatchMode.Prefix),
        };

        Assert.Equal(FilenameProgramVerdict.OtherProgram, Judge("野村萬斎のラジオで福袋【ゲスト】中越典子", decisions).Verdict);
    }

    [Theory]
    [InlineData(FilenameProgramMatchMode.Prefix)]
    [InlineData(FilenameProgramMatchMode.Contains)]
    public void 同じ種類なら照合キーが長い方を優先する(FilenameProgramMatchMode mode)
    {
        var shorter = Decision(FilenameProgramTreatment.OtherProgram, "福袋", name: "野村萬斎のラジオで福袋", mode: mode);
        var longer = Decision(FilenameProgramTreatment.Skip, name: "野村萬斎のラジオで福袋【ゲスト】羽生結弦", mode: mode);

        Assert.Equal(FilenameProgramVerdict.Skip, Judge("野村萬斎のラジオで福袋【ゲスト】羽生結弦　パート１", shorter, longer).Verdict);
        Assert.Equal(FilenameProgramVerdict.Skip, Judge("野村萬斎のラジオで福袋【ゲスト】羽生結弦　パート１", longer, shorter).Verdict);
        Assert.Equal(FilenameProgramVerdict.OtherProgram, Judge("野村萬斎のラジオで福袋【ゲスト】中越典子", shorter, longer).Verdict);
    }

    [Fact]
    public void 種類も照合キーも同じ判断は後に書いた方()
    {
        var first = Decision(FilenameProgramTreatment.Skip, name: "辛坊治郎", mode: FilenameProgramMatchMode.Prefix);
        var second = Decision(FilenameProgramTreatment.SameProgram, name: "辛坊 治郎", mode: FilenameProgramMatchMode.Prefix);

        Assert.Equal(FilenameProgramVerdict.SameProgram, Judge(Substitute, first, second).Verdict);
    }

    [Fact]
    public void 完全一致と前方一致に同じ文字列を書いても別の判断()
    {
        var exact = Decision(FilenameProgramTreatment.Skip, name: "辛坊治郎");
        var prefix = Decision(FilenameProgramTreatment.SameProgram, name: "辛坊治郎", mode: FilenameProgramMatchMode.Prefix);

        Assert.Equal(FilenameProgramVerdict.Skip, Judge("辛坊治郎", exact, prefix).Verdict);
        Assert.Equal(FilenameProgramVerdict.SameProgram, Judge(Substitute, exact, prefix).Verdict);
    }

    [Fact]
    public void 前方一致と部分一致は別の局_別のフォルダには効かない()
    {
        var prefix = Decision(FilenameProgramTreatment.Skip, name: "辛坊", mode: FilenameProgramMatchMode.Prefix);
        var contains = Decision(FilenameProgramTreatment.Skip, name: "ズーム", mode: FilenameProgramMatchMode.Contains);
        var resolver = Resolver(prefix, contains);

        Assert.Equal(FilenameProgramVerdict.Unjudged, resolver.JudgeFilenameProgram("TBSラジオ", Folder, Substitute).Verdict);
        Assert.Equal(FilenameProgramVerdict.Unjudged, resolver.JudgeFilenameProgram(Lf, "別のフォルダ", Substitute).Verdict);
    }

    [Theory]
    [InlineData(FilenameProgramMatchMode.Prefix, "")]
    [InlineData(FilenameProgramMatchMode.Contains, "　 ")]
    public void 照合キーが空の前方一致と部分一致は設定の誤り(FilenameProgramMatchMode mode, string name)
    {
        // 空の照合キーはフォルダのすべての回に当たってしまう。
        var ex = Assert.Throws<ProgramsConfigException>(() => Resolver(Decision(FilenameProgramTreatment.Skip, name: name, mode: mode)));

        Assert.Contains(mode.ToString(), ex.Message);
        Assert.Contains(Folder, ex.Message);
    }

    [Fact]
    public void 解釈できない照合の種類は設定の誤り()
    {
        var ex = Assert.Throws<ProgramsConfigException>(
            () => Resolver(Decision(FilenameProgramTreatment.Skip, mode: (FilenameProgramMatchMode)99)));

        Assert.Contains("Mode", ex.Message);
    }

    [Fact]
    public void 別の番組の回は判断のアルバムで組み_統合番組のフォルダのパートを引き継ぐ()
    {
        // #239:2 つの枠(統合番組のパート)にまたがった特番を 1 つのアルバムにまとめても、同じ日の 2 回が -1 / -2 で並ぶように、
        // フォルダのパートを引き継ぐ。
        const string part2 = Folder + "Part2";
        var programs = new ProgramMasterConfig
        {
            Programs = [new ProgramAlias
            {
                Match = new ProgramMatch { Station = Lf, Folders = [Folder, part2] },
                Canonical = new ProgramCanonical { Album = "チャリ" },
            }],
            FilenamePrograms =
            [
                Decision(FilenameProgramTreatment.OtherProgram, "辛坊治郎 ズーム"),
                Decision(FilenameProgramTreatment.OtherProgram, "辛坊治郎 ズーム", folder: part2),
            ],
        };
        var assembler = new EpisodeAssembler(new StationResolver(new StationMasterConfig()), new ProgramResolver(programs));

        var first = assembler.Assemble(new RajirekoImporter().Parse(Rec(Substitute)));
        var second = assembler.Assemble(new RajirekoImporter().Parse(Rec(Substitute, folder: part2)));

        Assert.Equal("辛坊治郎 ズーム", first.Program);
        Assert.Equal(1, first.PartIndex);
        Assert.Equal(FilenameProgramVerdict.OtherProgram, first.FilenameVerdict);
        Assert.Equal(Substitute, first.FilenameProgram);
        Assert.Equal("辛坊治郎 ズーム", second.Program);
        Assert.Equal(2, second.PartIndex);
    }

    [Fact]
    public void 統合番組でないフォルダの別の番組の回はパートを持たない()
    {
        var assembler = new EpisodeAssembler(
            new StationResolver(new StationMasterConfig()),
            new ProgramResolver(new ProgramMasterConfig
            {
                FilenamePrograms = [Decision(FilenameProgramTreatment.OtherProgram, "辛坊治郎 ズーム")],
            }));

        var episode = assembler.Assemble(new RajirekoImporter().Parse(Rec(Substitute)));

        Assert.Equal("辛坊治郎 ズーム", episode.Program);
        Assert.Null(episode.PartIndex);
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

    [Fact]
    public void 照合の種類を名前で読み_省略したら完全一致()
    {
        var config = ConfigLoader.Parse<ProgramMasterConfig>("""
            { "FilenamePrograms": [
                { "Match": { "Station": "a", "Folder": "b", "FilenameProgram": "c" }, "Treat": "Skip" },
                { "Match": { "Station": "a", "Folder": "b", "FilenameProgram": "d", "Mode": "Prefix" }, "Treat": "Skip" },
                { "Match": { "Station": "a", "Folder": "b", "FilenameProgram": "e", "Mode": "Contains" }, "Treat": "Skip" } ] }
            """);

        Assert.Equal(
            [FilenameProgramMatchMode.Exact, FilenameProgramMatchMode.Prefix, FilenameProgramMatchMode.Contains],
            config.FilenamePrograms.Select(d => d.Match.Mode));
    }

    [Fact]
    public void 解釈できない照合の種類は読めない()
    {
        Assert.ThrowsAny<JsonException>(() => ConfigLoader.Parse<ProgramMasterConfig>("""
            { "FilenamePrograms": [ { "Match": { "Station": "a", "Folder": "b", "FilenameProgram": "c", "Mode": "Regex" }, "Treat": "Skip" } ] }
            """));
    }

    [Fact]
    public void 書き手は完全一致の照合の種類を書かず_ほかは名前で書く()
    {
        // 完全一致だけなら、Mode を知らない古い版でも読める。
        var path = Path.Combine(_dir, "programs.json");
        File.WriteAllText(path, "{}");

        var config = new ProgramMasterConfig
        {
            FilenamePrograms =
            [
                Decision(FilenameProgramTreatment.Skip),
                Decision(FilenameProgramTreatment.Skip, name: "辛坊", mode: FilenameProgramMatchMode.Prefix),
                Decision(FilenameProgramTreatment.Skip, name: "ズーム", mode: FilenameProgramMatchMode.Contains),
            ],
        };
        ProgramsFileWriter.Write(ProgramsFileWriter.Read(path), config, DateTime.Now);
        var written = File.ReadAllText(path);

        Assert.DoesNotContain("Exact", written);
        Assert.Contains("\"Mode\": \"Prefix\"", written);
        Assert.Contains("\"Mode\": \"Contains\"", written);
        Assert.Equal(2, written.Split("\"Mode\"").Length - 1);
        Assert.Equal(
            [FilenameProgramMatchMode.Exact, FilenameProgramMatchMode.Prefix, FilenameProgramMatchMode.Contains],
            ProgramsFileWriter.Read(path).Config.FilenamePrograms.Select(d => d.Match.Mode));
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
