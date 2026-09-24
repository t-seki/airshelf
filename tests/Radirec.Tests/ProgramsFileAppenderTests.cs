using System.Text;
using Radirec.Core.Configuration;
using Radirec.Core.Normalization;
using Radirec.Hosting;

namespace Radirec.Tests;

/// <summary>
/// 統合設定の提案を programs.json に追記する(#144)。既存の内容・コメントはバイト単位で変えず、"Programs" 配列の
/// 閉じ ] の直前に差し込み、結果がコメント付き JSON として読めること。書く前のバックアップと、途中で失敗しても元の
/// programs.json が残ること。
/// </summary>
public sealed class ProgramsFileAppenderTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 9, 25);
    private static readonly DateTime Now = new(2026, 9, 25, 21, 5, 9);

    private readonly string _dir = Directory.CreateTempSubdirectory("radirec-append-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly SiblingProgramProposal NewEntry =
        new("文化放送", "てるのりのワルノリ", ["てるのりのワルノリ (1)", "てるのりのワルノリ (2)"], JoinsExisting: false);

    private static readonly SiblingProgramProposal JoinEntry =
        new("文化放送", "おとなりさん", ["おとなりさん 11時12時"], JoinsExisting: true);

    // 字下げ 4 の要素として追記される塊(NewEntry だけ)。
    private const string Block = """
            // --- 統合設定の提案(2026-09-25 追記。確認して直してください)---
            {
              "Match": {
                "Station": "文化放送",
                "Folders": [
                  "てるのりのワルノリ (1)",
                  "てるのりのワルノリ (2)"
                ]
              },
              "Canonical": { "Album": "てるのりのワルノリ" }
            }
            // --- 統合設定の提案ここまで ---

        """;

    private static string Insert(string json, params SiblingProgramProposal[] proposals)
        => Encoding.UTF8.GetString(ProgramsFileAppender.Insert(Encoding.UTF8.GetBytes(json), proposals, Today));

    private static ProgramMasterConfig Load(string json) => ConfigLoader.Parse<ProgramMasterConfig>(json);

    [Fact]
    public void 最後の要素の後にカンマが無くコメントがあれば_要素の直後にカンマを補いコメントの後に追記する()
    {
        const string json = """
            {
              "Programs": [
                // 先頭のコメント
                {
                  "Match": { "Station": "TBSラジオ", "Folders": ["A"] },
                  "Canonical": { "Album": "A" }
                } // 行末のコメント, カンマ入り
                /* 最後のコメント, ここにも */
              ]
            }
            """;

        var result = Insert(json, NewEntry);

        Assert.Equal($$"""
            {
              "Programs": [
                // 先頭のコメント
                {
                  "Match": { "Station": "TBSラジオ", "Folders": ["A"] },
                  "Canonical": { "Album": "A" }
                }, // 行末のコメント, カンマ入り
                /* 最後のコメント, ここにも */
            {{Block}}  ]
            }
            """, result);
        var config = Load(result);
        Assert.Equal(["A", "てるのりのワルノリ"], config.Programs.Select(p => p.Canonical.Album));
        Assert.Equal(2, new ProgramResolver(config).Resolve("文化放送", "てるのりのワルノリ (2)").PartIndex);
    }

    [Fact]
    public void 最後の要素の後にカンマがあれば_カンマを足さない()
    {
        const string json = """
            {
              "Programs": [
                { "Match": { "Station": "TBSラジオ", "Folders": ["A"] }, "Canonical": { "Album": "A" } },
              ]
            }
            """;

        Assert.Equal($$"""
            {
              "Programs": [
                { "Match": { "Station": "TBSラジオ", "Folders": ["A"] }, "Canonical": { "Album": "A" } },
            {{Block}}  ]
            }
            """, Insert(json, NewEntry));
    }

    [Fact]
    public void 空の配列でも_Programsの行より一段深く追記する()
    {
        const string json = """
            {
              "Defaults": { "Genre": "ラジオ" },
              "Programs": []
            }
            """;

        var result = Insert(json, NewEntry);

        Assert.Equal($$"""
            {
              "Defaults": { "Genre": "ラジオ" },
              "Programs": [
            {{Block}}  ]
            }
            """, result);
        Assert.Single(Load(result).Programs);
    }

    [Fact]
    public void 要素と閉じ括弧が同じ行でも_改行して追記し閉じ括弧を次の行に置く()
    {
        const string json = """
            {
              "Programs": [
                { "Match": { "Station": "TBSラジオ", "Folders": ["A"] }, "Canonical": { "Album": "A" } }]
            }
            """;

        Assert.Equal($$"""
            {
              "Programs": [
                { "Match": { "Station": "TBSラジオ", "Folders": ["A"] }, "Canonical": { "Album": "A" } },
            {{Block}}  ]
            }
            """, Insert(json, NewEntry));
    }

    [Fact]
    public void 統合番組へ足すだけならコメントだけを追記し_カンマも足さない()
    {
        const string json = """
            {
              "Programs": [
                { "Match": { "Station": "文化放送", "Folders": ["おとなりさん 8時9時"] }, "Canonical": { "Album": "おとなりさん" } }
              ]
            }
            """;

        var result = Insert(json, JoinEntry);

        Assert.Equal("""
            {
              "Programs": [
                { "Match": { "Station": "文化放送", "Folders": ["おとなりさん 8時9時"] }, "Canonical": { "Album": "おとなりさん" } }
                // --- 統合設定の提案(2026-09-25 追記。確認して直してください)---
                // 次の行を、統合番組『おとなりさん』(文化放送)の "Folders" の末尾に足してください:
                //   "おとなりさん 11時12時"
                // --- 統合設定の提案ここまで ---
              ]
            }
            """, result);
        Assert.Single(Load(result).Programs); // 既存のエントリは 1 つのまま
    }

    [Fact]
    public void 新しいエントリと統合番組への案内が混ざれば_エントリどうしだけをカンマで区切る()
    {
        const string json = """
            {
              "Programs": [
              ]
            }
            """;
        var second = NewEntry with { Album = "B", Folders = ["B(1)", "B(2)"] };

        var result = Insert(json, NewEntry, JoinEntry, second);

        Assert.Contains("""
                  "Canonical": { "Album": "てるのりのワルノリ" }
                },
                // 次の行を、統合番組『おとなりさん』(文化放送)の "Folders" の末尾に足してください:
                //   "おとなりさん 11時12時"
                {
            """, result);
        Assert.Contains("""
                  "Canonical": { "Album": "B" }
                }
                // --- 統合設定の提案ここまで ---
            """, result);
        Assert.Equal(2, Load(result).Programs.Count);
    }

    [Fact]
    public void BOMとCRLFのファイルは_BOMを残しCRLFで追記する()
    {
        var json = "{\r\n  \"Programs\": [\r\n    { \"Match\": { \"Station\": \"T\", \"Folders\": [\"A\"] }, \"Canonical\": { \"Album\": \"A\" } }\r\n  ]\r\n}\r\n";
        var original = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(json)).ToArray();

        var updated = ProgramsFileAppender.Insert(original, [NewEntry], Today);

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, updated[..3]);
        var text = Encoding.UTF8.GetString(updated[3..]);
        Assert.Equal(
            json.Replace("\"A\" } }\r\n  ]", "\"A\" } },\r\n" + Block.ReplaceLineEndings("\r\n") + "  ]"),
            text);
        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
    }

    [Fact]
    public void 見本のprogramsjsonに追記しても既存の部分は変わらず読める()
    {
        var sample = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Samples", "programs.sample.json"));
        var before = Load(Encoding.UTF8.GetString(sample));

        var updated = ProgramsFileAppender.Insert(sample, [NewEntry, JoinEntry], Today);

        // 挿入したもの(最後の要素の直後のカンマ・追記の塊)を取り除くと、元とバイト単位で同じ。
        // 改行は見本に合わせる(Windows の checkout では CRLF になりうる)。
        var text = Encoding.UTF8.GetString(updated);
        var nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var blockStart = text.IndexOf("    // --- 統合設定の提案(", StringComparison.Ordinal);
        var blockEnd = text.IndexOf("ここまで ---" + nl, StringComparison.Ordinal) + ("ここまで ---" + nl).Length;
        var withoutBlock = text[..blockStart] + text[blockEnd..];
        var lastElement = "\"パンサー向井のふらっと\" }" + nl + "    }";
        var comma = withoutBlock.IndexOf(lastElement + ",", StringComparison.Ordinal);
        Assert.True(comma > 0, withoutBlock);
        var restored = withoutBlock.Remove(comma + lastElement.Length, 1);
        Assert.Equal(sample, Encoding.UTF8.GetBytes(restored));

        var after = Load(text);
        Assert.Equal(before.Programs.Count + 1, after.Programs.Count);
        Assert.Equal("てるのりのワルノリ", after.Programs[^1].Canonical.Album);
    }

    [Theory]
    [InlineData("""{ "Programs": [ { "Match": """)]                       // 壊れている
    [InlineData("""{ "Defaults": { "Genre": "" } }""")]                   // "Programs" が無い
    [InlineData("""{ "Programs": null }""")]                              // 配列ではない
    public void 追記できない形なら例外で知らせる(string json)
        => Assert.Throws<ProgramsAppendException>(() => Insert(json, NewEntry));

    // ---- ファイルへの書き込み ----

    private string WriteProgramsFile(string content)
    {
        var path = Path.Combine(_dir, "programs.json");
        File.WriteAllText(path, content);
        return path;
    }

    private static readonly SiblingFolderGroup Group =
        new("文化放送", "てるのりのワルノリ", ["てるのりのワルノリ (2)", "てるのりのワルノリ (1)"]);

    private const string Minimal = """
        {
          "Programs": [
          ]
        }
        """;

    [Fact]
    public void 予定を確かめてから書き_書く前にバックアップを作る()
    {
        var path = WriteProgramsFile(Minimal);

        var plan = ProgramsFileAppender.Plan(path, [Group], Today);
        Assert.Equal(Minimal, File.ReadAllText(path)); // 予定を作るだけでは書かない
        var proposal = Assert.Single(plan.Proposals);
        Assert.Equal((NewEntry.Station, NewEntry.Album, false), (proposal.Station, proposal.Album, proposal.JoinsExisting));
        Assert.Equal(NewEntry.Folders, proposal.Folders); // パートの順に並べ直してある
        Assert.StartsWith("// --- 統合設定の提案(2026-09-25", plan.Snippet);

        var backup = ProgramsFileAppender.Apply(plan, Now);

        Assert.Equal(Path.Combine(_dir, "programs.json.bak-20260925-210509"), backup);
        Assert.Equal(Minimal, File.ReadAllText(backup));
        Assert.Equal(plan.Updated, File.ReadAllBytes(path));
        Assert.Single(Load(File.ReadAllText(path)).Programs);
        Assert.Equal(2, Directory.GetFiles(_dir).Length); // 一時ファイルが残らない
    }

    [Fact]
    public void 同じ秒のバックアップがあれば上書きせず別名にする()
    {
        var path = WriteProgramsFile(Minimal);
        File.WriteAllText(path + ".bak-20260925-210509", "前のバックアップ");

        var backup = ProgramsFileAppender.Apply(ProgramsFileAppender.Plan(path, [Group], Today), Now);

        Assert.Equal(path + ".bak-20260925-210509-2", backup);
        Assert.Equal("前のバックアップ", File.ReadAllText(path + ".bak-20260925-210509"));
    }

    [Fact]
    public void バックアップを置けなければ本体を書き換えず_一時ファイルも残さない()
    {
        var path = WriteProgramsFile(Minimal);
        var plan = ProgramsFileAppender.Plan(path, [Group], Today);
        // バックアップの名前の場所に置けない(同名のフォルダがある)。一時ファイルに書いた後、置く段階で失敗する
        Directory.CreateDirectory(path + ".bak-20260925-210509");

        // Linux は IOException、Windows は UnauthorizedAccessException になりうる
        Assert.ThrowsAny<Exception>(() => ProgramsFileAppender.Apply(plan, Now));

        Assert.Equal(Minimal, File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_dir)); // 壊れたバックアップも一時ファイルも残らない
    }

    [Fact]
    public void 見本から作った直後のprogramsjsonにも追記できる()
    {
        // programs.json が無いとき、トレイは起動時と同じ見本から作ってから追記へ進む
        var path = Path.Combine(_dir, "programs.json");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Samples", "programs.sample.json"), path);
        var before = Load(File.ReadAllText(path)).Programs.Count;

        ProgramsFileAppender.Apply(ProgramsFileAppender.Plan(path, [Group], Today), Now);

        var after = Load(File.ReadAllText(path));
        Assert.Equal(before + 1, after.Programs.Count);
        Assert.Equal("てるのりのワルノリ", after.Programs[^1].Canonical.Album);
    }

    [Fact]
    public void 確認している間にファイルが変わったら書かない()
    {
        var path = WriteProgramsFile(Minimal);
        var plan = ProgramsFileAppender.Plan(path, [Group], Today);
        File.WriteAllText(path, Minimal + "\n"); // 利用者がエディタで保存した

        Assert.Throws<ProgramsAppendException>(() => ProgramsFileAppender.Apply(plan, Now));

        Assert.Equal(Minimal + "\n", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_dir)); // バックアップも作らない
    }

    [Fact]
    public void 壊れたファイルは予定を作らず何も書かない()
    {
        var path = WriteProgramsFile("{ \"Programs\": [ ");

        Assert.Throws<ProgramsAppendException>(() => ProgramsFileAppender.Plan(path, [Group], Today));

        Assert.Equal("{ \"Programs\": [ ", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void ファイルが無ければ例外で知らせる()
        => Assert.Throws<ProgramsAppendException>(
            () => ProgramsFileAppender.Plan(Path.Combine(_dir, "programs.json"), [Group], Today));

    [Fact]
    public void 統合設定が既にある組だけなら提案は空で中身も変えない()
    {
        var path = WriteProgramsFile("""
            { "Programs": [ { "Match": { "Station": "文化放送", "Folders": ["てるのりのワルノリ (1)", "てるのりのワルノリ (2)"] }, "Canonical": { "Album": "X" } } ] }
            """);

        var plan = ProgramsFileAppender.Plan(path, [Group], Today);

        Assert.Empty(plan.Proposals);
        Assert.Equal(plan.Original, plan.Updated);
    }

    [Fact]
    public void 置き換えの書き込みが途中で失敗しても元のファイルが残り一時ファイルも残さない()
    {
        var path = WriteProgramsFile(Minimal);

        Assert.Throws<IOException>(() => ReplacingFileWriter.Replace(path, s =>
        {
            s.Write(Encoding.UTF8.GetBytes("{ \"Progr")); // 途中まで書いて切れた(NAS の切断を模す)
            throw new IOException("切断");
        }));

        Assert.Equal(Minimal, File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_dir));
    }
}
