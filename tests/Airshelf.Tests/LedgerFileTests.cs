using Airshelf.Hosting;

namespace Airshelf.Tests;

/// <summary>
/// 取り込み(トレイ)のために台帳を書き込み用に開く(<see cref="LedgerFile.OpenWritable"/>。#228)。
/// 開けなければ <see cref="LedgerUnreachableException"/> にし、設定の反映の失敗が台帳のせいかを型で見分けられるようにする。
/// </summary>
public sealed class LedgerFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"radirec-ledgerfile-{Guid.NewGuid():N}");

    public LedgerFileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void フォルダがまだ無いだけなら_作って開く()
    {
        var path = Path.Combine(_root, "new", "ledger.db");

        using (LedgerFile.OpenWritable(path))
        {
        }

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void フォルダを作れなければ_届かない例外に元の例外を包む()
    {
        // フォルダを作るはずの場所にファイルがあると、フォルダを作れず IOException になる(届かない NAS の代わり)。
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "");
        var path = Path.Combine(blocker, "sub", "ledger.db");

        var ex = Assert.Throws<LedgerUnreachableException>(() => LedgerFile.OpenWritable(path));

        Assert.IsAssignableFrom<IOException>(ex.InnerException);
        Assert.StartsWith($"台帳のあるフォルダに届きません: {Path.Combine(blocker, "sub")}(", ex.Message);
        // CLI では IOException として 5 になる(#216)。
        Assert.IsAssignableFrom<IOException>(ex);
    }
}
