using System.Text;
using Airshelf.Hosting;

namespace Airshelf.Tests;

/// <summary>
/// 設定ファイルを見本から作るときの書き方(#143)。既存は温存し、途中で切れても壊れたファイルを置かない。
/// </summary>
public sealed class NewFileWriterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("radirec-newfile-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static Action<Stream> Writes(string text) => s => s.Write(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void 無ければ親フォルダごと作って中身を置く()
    {
        var path = Path.Combine(_dir, "sub", "programs.json");

        Assert.True(NewFileWriter.CreateIfAbsent(path, Writes("{}")));

        Assert.Equal("{}", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(Path.Combine(_dir, "sub"))); // 一時ファイルが残らない
    }

    [Fact]
    public void 既にあれば書かずに温存する()
    {
        var path = Path.Combine(_dir, "programs.json");
        File.WriteAllText(path, "edited");
        var called = false;

        Assert.False(NewFileWriter.CreateIfAbsent(path, _ => called = true));

        Assert.False(called);
        Assert.Equal("edited", File.ReadAllText(path));
    }

    [Fact]
    public void 書き込みの途中で失敗したら置かずに投げ一時ファイルも残さない()
    {
        var path = Path.Combine(_dir, "programs.json");

        Assert.Throws<IOException>(() => NewFileWriter.CreateIfAbsent(path, s =>
        {
            s.Write(Encoding.UTF8.GetBytes("{ \"Progr")); // 途中まで書いて切れた(NAS の切断を模す)
            throw new IOException("切断");
        }));

        Assert.False(File.Exists(path)); // 壊れた JSON を置かない = 次に開くときに作り直せる
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void 置く直前に誰かが作っていたら上書きせず温存する()
    {
        var path = Path.Combine(_dir, "programs.json");

        var created = NewFileWriter.CreateIfAbsent(path, s =>
        {
            s.Write(Encoding.UTF8.GetBytes("sample"));
            File.WriteAllText(path, "edited"); // 確かめた後・置く前に利用者が作った
        });

        Assert.False(created);
        Assert.Equal("edited", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_dir)); // 一時ファイルは消える
    }
}
