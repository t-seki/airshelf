using System.IO.Abstractions;
using Airshelf.Core.Ingestion;

namespace Airshelf.Tests;

/// <summary>
/// 取込元の列挙(<see cref="FileSystemRecordingSource"/>)の実ファイルシステムでの振る舞い。
/// </summary>
public sealed class FileSystemRecordingSourceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("radirec-source-").FullName;

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows() && Directory.Exists(Path.Combine(_root, "B")))
            File.SetUnixFileMode(Path.Combine(_root, "B"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Unreadable_subfolder_makes_the_enumeration_throw_instead_of_skipping_it()
    {
        // 取込元の中の 1 つのフォルダだけが読めないとき、列挙は黙って飛ばさずに投げる。走査はそこで失敗し、
        // 取込元から消えた失敗の記録を消す処理(#172)まで進まない。飛ばされると、読めないフォルダの失敗を
        // 「取込元から消えた」と取り違えて消してしまう。Linux では権限を外して確かめる(root では確かめられない)。
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
            return;
        Directory.CreateDirectory(Path.Combine(_root, "A", "p1"));
        Directory.CreateDirectory(Path.Combine(_root, "B", "p2"));
        File.WriteAllText(Path.Combine(_root, "A", "p1", "a.m4a"), "");
        File.WriteAllText(Path.Combine(_root, "B", "p2", "b.m4a"), "");
        File.SetUnixFileMode(Path.Combine(_root, "B"), UnixFileMode.None);

        var source = new FileSystemRecordingSource(new FileSystem(), _root);

        Assert.Throws<UnauthorizedAccessException>(() => source.Enumerate().ToList());
    }
}
