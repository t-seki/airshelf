using System.Text;
using System.Text.Json;
using Radirec.Core.Configuration;
using Radirec.Hosting;

namespace Radirec.Tests;

/// <summary>
/// programs.json の書き手(#153 / ADR-0017)。全体を書き直し、ConfigLoader で読み直すと同じ内容になること。
/// 読み込んだ後に変わったファイルを上書きしないこと。書き直すと消えるコメントの件数を数えられること。
/// </summary>
public sealed class ProgramsFileWriterTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 25, 21, 5, 9);

    private readonly string _dir = Directory.CreateTempSubdirectory("radirec-writer-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string ProgramsPath => Path.Combine(_dir, "programs.json");

    private string WriteProgramsFile(string content)
    {
        File.WriteAllText(ProgramsPath, content);
        return ProgramsPath;
    }

    private static ProgramMasterConfig FullConfig() => new()
    {
        Defaults = new ProgramDefaults { Genre = "ラジオ" },
        Programs =
        [
            new ProgramAlias
            {
                Match = new ProgramMatch { Station = "TBSラジオ", Folders = ["パンサー向井のふらっと(1)", "パンサー向井のふらっと(2)"] },
                Canonical = new ProgramCanonical { Album = "パンサー向井のふらっと" },
                Note = "8 時台と 9 時台で分かれて録音される // 引用符 \" も入れる",
            },
            new ProgramAlias
            {
                Match = new ProgramMatch { Station = "J-WAVE", Folders = ["LOGISTEEDRADIONOMICS"] },
                Canonical = new ProgramCanonical { Album = "LOGISTEED RADIONOMICS" },
            },
        ],
        KeepSeparate =
        [
            new KeptApartSiblings
            {
                Match = new ProgramMatch { Station = "文化放送", Folders = ["おとなりさん 8時台", "おとなりさん 11時12時"] },
                Note = "時間帯で内容が違う",
            },
            new KeptApartSiblings
            {
                Match = new ProgramMatch { Station = "ニッポン放送", Folders = ["A (1)", "A (2)"] },
            },
        ],
    };

    private static string Text(byte[] content) => Encoding.UTF8.GetString(content);

    // --- 書き出し ------------------------------------------------------------------------

    [Fact]
    public void 書いたファイルをConfigLoaderで読めて読み直すと同じ内容になる()
    {
        var path = WriteProgramsFile("{ \"Programs\": [] }");
        var config = FullConfig();

        ProgramsFileWriter.Write(ProgramsFileWriter.Read(path), config, Now);

        var loaded = ConfigLoader.LoadRequired<ProgramMasterConfig>(path);
        Assert.Equal(JsonSerializer.Serialize(config), JsonSerializer.Serialize(loaded));
        Assert.Equal("8 時台と 9 時台で分かれて録音される // 引用符 \" も入れる", loaded.Programs[0].Note);
        Assert.Null(loaded.Programs[1].Note);
        Assert.Equal(["おとなりさん 8時台", "おとなりさん 11時12時"], loaded.KeepSeparate[0].Match.Folders);
        Assert.Equal("時間帯で内容が違う", loaded.KeepSeparate[0].Note);
        Assert.Null(loaded.KeepSeparate[1].Note);

        // 読み直したものをもう一度書き出すと、バイト単位で同じ。
        Assert.Equal(File.ReadAllBytes(path), ProgramsFileWriter.Serialize(loaded));
    }

    [Fact]
    public void Noteがnullのエントリと空のKeepSeparateはキーごと書かない()
    {
        var config = new ProgramMasterConfig
        {
            Programs =
            [
                new ProgramAlias
                {
                    Match = new ProgramMatch { Station = "J-WAVE", Folders = ["LOGISTEEDRADIONOMICS"] },
                    Canonical = new ProgramCanonical { Album = "LOGISTEED RADIONOMICS" },
                },
            ],
        };

        var text = Text(ProgramsFileWriter.Serialize(config));

        Assert.DoesNotContain("\"Note\"", text);
        Assert.DoesNotContain("\"KeepSeparate\"", text);
        Assert.Contains("\"Programs\"", text);
    }

    [Fact]
    public void KeepSeparateがnullでもキーごと書かない()
    {
        var text = Text(ProgramsFileWriter.Serialize(new ProgramMasterConfig { KeepSeparate = null! }));

        Assert.DoesNotContain("\"KeepSeparate\"", text);
    }

    [Fact]
    public void KeepSeparateの1件はMatchとNoteの形で書く()
    {
        var text = Text(ProgramsFileWriter.Serialize(FullConfig()));

        using var doc = JsonDocument.Parse(text);
        var first = doc.RootElement.GetProperty("KeepSeparate")[0];
        Assert.Equal(["Match", "Note"], first.EnumerateObject().Select(p => p.Name));
        Assert.Equal("文化放送", first.GetProperty("Match").GetProperty("Station").GetString());
        Assert.Equal(2, first.GetProperty("Match").GetProperty("Folders").GetArrayLength());
    }

    [Fact]
    public void 字下げありで日本語をエスケープせずBOMなしで書く()
    {
        var content = ProgramsFileWriter.Serialize(FullConfig());
        var text = Text(content);

        Assert.False(content.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.Contains("パンサー向井のふらっと", text);
        Assert.DoesNotContain("\\u", text);
        Assert.Contains("\n  \"Programs\": [", text.Replace("\r\n", "\n"));
    }

    [Fact]
    public void 書く前に元の中身のバックアップを作る()
    {
        const string original = "{ \"Programs\": [] } // メモ";
        var path = WriteProgramsFile(original);

        var backup = ProgramsFileWriter.Write(ProgramsFileWriter.Read(path), FullConfig(), Now);

        Assert.Equal(path + ".bak-20260925-210509", backup);
        Assert.Equal(original, File.ReadAllText(backup!));
        Assert.Equal(2, Directory.GetFiles(_dir).Length); // 本体とバックアップだけ(一時ファイルが残らない)
    }

    [Fact]
    public void 同じ秒のバックアップが既にあれば別名にする()
    {
        var path = WriteProgramsFile("{}");
        File.WriteAllText(path + ".bak-20260925-210509", "前のバックアップ");

        var backup = ProgramsFileWriter.Write(ProgramsFileWriter.Read(path), FullConfig(), Now);

        Assert.Equal(path + ".bak-20260925-210509-2", backup);
        Assert.Equal("前のバックアップ", File.ReadAllText(path + ".bak-20260925-210509"));
        Assert.Equal("{}", File.ReadAllText(backup!));
    }

    [Fact]
    public void ファイルが無ければバックアップ無しで新しく作る()
    {
        var snapshot = ProgramsFileWriter.Read(ProgramsPath);
        Assert.Null(snapshot.Original);
        Assert.Empty(snapshot.Config.Programs);
        Assert.Equal(0, snapshot.CommentCount);

        var backup = ProgramsFileWriter.Write(snapshot, FullConfig(), Now);

        Assert.Null(backup);
        Assert.Equal(2, ConfigLoader.LoadRequired<ProgramMasterConfig>(ProgramsPath).Programs.Count);
        Assert.Single(Directory.GetFiles(_dir));
    }

    // --- 読み込んだ後に変わったファイル --------------------------------------------------------

    [Fact]
    public void 読み込んだ後に変更されたファイルを上書きしない()
    {
        var path = WriteProgramsFile("{ \"Programs\": [] }");
        var snapshot = ProgramsFileWriter.Read(path);
        File.WriteAllText(path, "{ \"Programs\": [] }\n// エディタで直した"); // 利用者がエディタで保存した

        Assert.Throws<ProgramsFileChangedException>(() => ProgramsFileWriter.Write(snapshot, FullConfig(), Now));

        Assert.Equal("{ \"Programs\": [] }\n// エディタで直した", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_dir)); // バックアップも一時ファイルも作らない
    }

    [Fact]
    public void 読み込んだときに無かったファイルが書く前に作られていたら上書きしない()
    {
        var snapshot = ProgramsFileWriter.Read(ProgramsPath);
        WriteProgramsFile("{}");

        Assert.Throws<ProgramsFileChangedException>(() => ProgramsFileWriter.Write(snapshot, FullConfig(), Now));

        Assert.Equal("{}", File.ReadAllText(ProgramsPath));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void 読み込んだ後にファイルが消されていたら作らない()
    {
        var path = WriteProgramsFile("{}");
        var snapshot = ProgramsFileWriter.Read(path);
        File.Delete(path);

        Assert.Throws<ProgramsFileChangedException>(() => ProgramsFileWriter.Write(snapshot, FullConfig(), Now));

        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void 壊れたファイルは読めない理由を返す()
    {
        var path = WriteProgramsFile("{ \"Programs\": [ ");

        var ex = Assert.Throws<ProgramsFileException>(() => ProgramsFileWriter.Read(path));
        Assert.IsNotType<ProgramsFileChangedException>(ex);
    }

    // --- コメントの件数 ----------------------------------------------------------------------

    [Fact]
    public void コメントの件数を数える()
    {
        const string json = """
            {
              // 行コメント 1
              "Programs": [
                /* ブロック
                   コメント */
                { "Match": { "Station": "J-WAVE", "Folders": ["X"] }, "Canonical": { "Album": "X" } }, // 行末 3
              ],
              // 4
            }
            """;
        var path = WriteProgramsFile(json);

        Assert.Equal(4, ProgramsFileWriter.CountComments(Encoding.UTF8.GetBytes(json)));
        Assert.Equal(4, ProgramsFileWriter.Read(path).CommentCount); // 末尾カンマがあっても読める
    }

    [Fact]
    public void 文字列の中のスラッシュ2つはコメントとして数えない()
    {
        const string json = """
            {
              "Programs": [
                { "Match": { "Station": "http://example.com/", "Folders": ["A // B", "/* C */"] },
                  "Canonical": { "Album": "A // B" } }
              ]
            }
            """;

        Assert.Equal(0, ProgramsFileWriter.CountComments(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void BOM付きのファイルでもコメントを数えられる()
    {
        var content = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("{ // 1\n \"Programs\": [] }")).ToArray();
        File.WriteAllBytes(ProgramsPath, content);

        Assert.Equal(1, ProgramsFileWriter.CountComments(content));
        var snapshot = ProgramsFileWriter.Read(ProgramsPath);
        Assert.Equal(1, snapshot.CommentCount);
        Assert.Equal(content, snapshot.Original);
    }

    [Fact]
    public void 書き直したファイルにはコメントが残らない()
    {
        var path = WriteProgramsFile("{ // メモ\n \"Programs\": [] }");
        Assert.Equal(1, ProgramsFileWriter.Read(path).CommentCount);

        ProgramsFileWriter.Write(ProgramsFileWriter.Read(path), FullConfig(), Now);

        Assert.Equal(0, ProgramsFileWriter.Read(path).CommentCount);
    }

    [Fact]
    public void 見本のprogramsjsonにはコメントが無い()
    {
        // 初期配置された programs.json を管理画面で初めて保存するとき、「コメントが消えます」の確認を出さないため
        var sample = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Samples", "programs.sample.json"));

        Assert.Equal(0, ProgramsFileWriter.CountComments(sample));
    }
}
