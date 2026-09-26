using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Radirec.Hosting.Control;

namespace Radirec.Tests;

/// <summary>
/// 制御チャネルのパイプの ACL と所有者の確かめ(#214)。昇格した端末の CLI が、非昇格のトレイに繋がるように、
/// トークンの所有者ではなくユーザーの SID で作り・比べる。
/// </summary>
public sealed class ControlPipeSecurityTests
{
    private const string User = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string OtherUser = "S-1-5-21-1111111111-2222222222-3333333333-1002";
    private const string Administrators = "S-1-5-32-544";

    [Fact]
    public void EnsureOwnedBy_所有者がユーザーのSIDなら通す()
    {
        ControlPipeSecurity.EnsureOwnedBy(User, User);
    }

    [Fact]
    public void EnsureOwnedBy_所有者がAdministratorsなら拒む()
    {
        // .NET の CurrentUserOnly は、昇格したトレイのパイプの所有者を Administrators にする。ユーザーの SID でないものは拒む。
        Assert.Throws<UnauthorizedAccessException>(() => ControlPipeSecurity.EnsureOwnedBy(Administrators, User));
    }

    [Fact]
    public void EnsureOwnedBy_所有者が別のユーザーか不明なら拒む()
    {
        Assert.Throws<UnauthorizedAccessException>(() => ControlPipeSecurity.EnsureOwnedBy(OtherUser, User));
        Assert.Throws<UnauthorizedAccessException>(() => ControlPipeSecurity.EnsureOwnedBy(null, User));
    }

    [Fact]
    public void ForUser_ユーザーのSIDを所有者にし_全権をユーザーだけに許す()
    {
        // SecurityIdentifier は Windows 以外で作れない(PlatformNotSupportedException)。CI(Linux)では走らない。
        if (!OperatingSystem.IsWindows())
            return;

        var user = new SecurityIdentifier(User);
        var security = ControlPipeSecurity.ForUser(user);

        Assert.Equal(user, security.GetOwner(typeof(SecurityIdentifier)));
        var rule = Assert.Single(security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>());
        Assert.Equal(user, rule.IdentityReference);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.Equal(PipeAccessRights.FullControl, rule.PipeAccessRights);
    }
}
