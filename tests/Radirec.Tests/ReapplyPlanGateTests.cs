using Radirec.Hosting.Control;

namespace Radirec.Tests;

// 再適用の確認を、直近のリロードの失敗で止めるか(#154 のレビュー 2 周目)。
public sealed class ReapplyPlanGateTests
{
    [Fact]
    public void Saved_settings_plan_is_blocked_by_last_reload_error_as_before()
        => Assert.Equal("stations.json を読めません", ReapplyPlanGate.BlockingReloadError(hasDraft: false, "stations.json を読めません"));

    [Fact]
    public void Draft_plan_is_not_blocked_by_stale_reload_error()
        // 「保存して適用」がリロードの失敗で中止された後、原因を直して番組ページの「確認」を押しても、古い理由で止まらない
        => Assert.Null(ReapplyPlanGate.BlockingReloadError(hasDraft: true, "stations.json を読めません"));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void No_reload_error_never_blocks(bool hasDraft)
        => Assert.Null(ReapplyPlanGate.BlockingReloadError(hasDraft, lastReloadError: null));
}
