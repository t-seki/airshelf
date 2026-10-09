using System.IO.Abstractions;
using Airshelf.Core.Ingestion;

namespace Airshelf.Tests;

/// <summary>
/// 取込元の列挙(<see cref="FileSystemRecordingSource"/>)の実ファイルシステムでの振る舞い。
/// </summary>
public sealed class FileSystemRecordingSourceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("airshelf-source-").FullName;

    public void Dispose()
    {
        var b = Path.Combine(_root, "B");
        if (Directory.Exists(b))
        {
            if (OperatingSystem.IsWindows())
                SetListDirectoryDeny(b, deny: false);
            else
                File.SetUnixFileMode(b, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Directory.Delete(_root, recursive: true);
    }

    // Windows 用。現在のユーザーへの ListDirectory の Deny ACE を付ける / 外す(一時フォルダの中だけを書き換える)。
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void SetListDirectoryDeny(string path, bool deny)
    {
        var info = new DirectoryInfo(path);
        var rule = new System.Security.AccessControl.FileSystemAccessRule(
            System.Security.Principal.WindowsIdentity.GetCurrent().User!,
            System.Security.AccessControl.FileSystemRights.ListDirectory,
            System.Security.AccessControl.AccessControlType.Deny);
        var acl = info.GetAccessControl();
        if (deny)
            acl.AddAccessRule(rule);
        else
            acl.RemoveAccessRule(rule);
        info.SetAccessControl(acl);
    }

    [Fact]
    public void Unreadable_subfolder_makes_the_enumeration_throw_instead_of_skipping_it()
    {
        // 取込元の中の 1 つのフォルダだけが読めないとき、列挙は黙って飛ばさずに投げる。走査はそこで失敗し、
        // 取込元から消えた失敗の記録を消す処理(#172)まで進まない。飛ばされると、読めないフォルダの失敗を
        // 「取込元から消えた」と取り違えて消してしまう。Windows では ACL(ListDirectory の
        // Deny)、Unix ではアクセス権を外して確かめる(Linux の root では権限が効かず確かめられない)。
        if (!OperatingSystem.IsWindows() && Environment.UserName == "root")
            return;
        Directory.CreateDirectory(Path.Combine(_root, "A", "p1"));
        Directory.CreateDirectory(Path.Combine(_root, "B", "p2"));
        File.WriteAllText(Path.Combine(_root, "A", "p1", "a.m4a"), "");
        File.WriteAllText(Path.Combine(_root, "B", "p2", "b.m4a"), "");
        if (OperatingSystem.IsWindows())
            SetListDirectoryDeny(Path.Combine(_root, "B"), deny: true);
        else
            File.SetUnixFileMode(Path.Combine(_root, "B"), UnixFileMode.None);

        var source = new FileSystemRecordingSource(new FileSystem(), _root);

        Assert.Throws<UnauthorizedAccessException>(() => source.Enumerate().ToList());
    }
}
