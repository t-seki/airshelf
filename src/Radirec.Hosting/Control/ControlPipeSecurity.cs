using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Radirec.Hosting.Control;

/// <summary>
/// 制御チャネルのパイプを同じユーザーだけに絞る(ADR-0020 / #214)。受け口(<see cref="ControlPipeServer"/>)と
/// CLI(<see cref="ControlPipeClient"/>)の両方がこれでパイプを作る。
/// <para>
/// Windows では <see cref="PipeOptions.CurrentUserOnly"/> を使わない。.NET の <c>CurrentUserOnly</c> は、ユーザーの SID
/// (<see cref="WindowsIdentity.User"/>)ではなく**トークンの所有者**(<see cref="WindowsIdentity.Owner"/>)で ACL を作り、
/// 相手のパイプの所有者と比べる(dotnet/runtime の <c>NamedPipeServerStream.Windows.cs</c> の <c>Create</c> と
/// <c>NamedPipeClientStream.Windows.cs</c> の <c>ValidateRemotePipeUser</c>)。UAC で昇格したトークンでは所有者が
/// <c>BUILTIN\Administrators</c> になるので、管理者の端末の CLI は非昇格のトレイに繋がらず、「常駐が動いていません」になっていた。
/// ここでは昇格の有無に関係なく、許可も所有者もユーザーの SID で作り、CLI は繋いだ後に所有者がユーザーの SID かを確かめる
/// (別のプロセスが同じ名前で待ち受けるなりすましを防ぐ。<c>CurrentUserOnly</c> の役目を保つ)。
/// </para>
/// <para>
/// Windows 以外(テスト・CI の Linux)では今までどおり <c>CurrentUserOnly</c> に任せる(UNIX ドメインソケットの相手の uid で比べ、
/// 昇格の問題は無い)。
/// </para>
/// </summary>
public static class ControlPipeSecurity
{
    /// <summary>受け口のパイプを 1 つ作る(接続はまだ待たない)。</summary>
    public static NamedPipeServerStream CreateServer(string pipeName)
    {
        if (!OperatingSystem.IsWindows())
            return new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        return NamedPipeServerStreamAcl.Create(
            pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, inBufferSize: 0, outBufferSize: 0, ForUser(CurrentUser()));
    }

    /// <summary>CLI 側のパイプを作る。繋いだ後に <see cref="VerifyServerOwner"/> を呼ぶこと(Windows)。</summary>
    public static NamedPipeClientStream CreateClient(string pipeName) =>
        new(".", pipeName, PipeDirection.InOut,
            OperatingSystem.IsWindows() ? PipeOptions.Asynchronous : PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    /// <summary>
    /// 繋いだ相手のパイプの所有者が、このプロセスのユーザーの SID かを確かめる。違えば、または確かめられなければ
    /// <see cref="UnauthorizedAccessException"/>(呼び出し側は繋がらないのと同じに扱う)。
    /// Windows 以外では何もしない(<c>CurrentUserOnly</c> が接続のときに確かめている)。
    /// </summary>
    public static void VerifyServerOwner(NamedPipeClientStream pipe)
    {
        if (OperatingSystem.IsWindows())
            VerifyServerOwnerOnWindows(pipe);
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyServerOwnerOnWindows(NamedPipeClientStream pipe)
    {
        VerifyOwner(
            () => pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier))?.Value,
            () => CurrentUser().Value);
    }
    /// <summary>
    /// 所有者の SID とユーザーの SID を読んで比べる(<see cref="EnsureOwnedBy"/>)。読むときの失敗(ユーザーの SID が取れない・
    /// ACL を読めないなど)も <see cref="UnauthorizedAccessException"/> に包む。確かめられないときは繋がない(fail-closed)。
    /// </summary>
    public static void VerifyOwner(Func<string?> readOwnerSid, Func<string?> readUserSid)
    {
        string? ownerSid;
        string? userSid;
        try
        {
            ownerSid = readOwnerSid();
            userSid = readUserSid();
        }
        catch (Exception ex) when (ex is not UnauthorizedAccessException)
        {
            throw new UnauthorizedAccessException($"制御チャネルのパイプの所有者を確かめられません: {ex.Message}", ex);
        }
        if (userSid is null)
            throw new UnauthorizedAccessException("制御チャネルのパイプの所有者を確かめられません(このユーザーの SID が取れません)");
        EnsureOwnedBy(ownerSid, userSid);
    }
    /// <summary>ユーザーの SID だけに読み書きとインスタンスの作成を許し、所有者もユーザーの SID にしたパイプの ACL。</summary>
    [SupportedOSPlatform("windows")]
    public static PipeSecurity ForUser(SecurityIdentifier user)
    {
        // 最小の権限にする(ADR-0010 の時代の実装と同じ)。CreateNewInstance は、受け口が接続ごとに同じ名前のパイプを作り直すため。
        // 読みには ReadPermissions が含まれ、CLI が所有者を確かめる(GetAccessControl)のに足りる。
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        security.SetOwner(user);
        return security;
    }

    /// <summary>
    /// 所有者の SID(<paramref name="ownerSid"/>)がユーザーの SID(<paramref name="userSid"/>)でなければ
    /// <see cref="UnauthorizedAccessException"/>。どちらも SDDL の文字列(<see cref="SecurityIdentifier.Value"/>。
    /// <c>SecurityIdentifier</c> は Windows 以外で作れないので、比べるところは文字列にしてテストできるようにした)。
    /// </summary>
    public static void EnsureOwnedBy(string? ownerSid, string userSid)
    {
        if (!string.Equals(ownerSid, userSid, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException(
                $"制御チャネルのパイプの所有者が、このユーザーではありません(所有者 {ownerSid ?? "不明"})");
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new InvalidOperationException("このプロセスのユーザーの SID を取れません");
    }
}
