namespace Airshelf.Hosting.Control;

/// <summary>
/// 再適用の確認(dry-run)を、直近の設定リロードの失敗で止めるかどうか(ADR-0016 / #154)。常駐の worker から切り出した
/// 純粋な判定(worker は Windows 専用で WSL の CI では動かないため、ここでテストする)。
/// </summary>
public static class ReapplyPlanGate
{
    /// <summary>
    /// 確認を止める理由。止めないなら null。
    /// <list type="bullet">
    /// <item><b>保存済みの設定で確かめる</b>(<paramref name="hasDraft"/> が false。<see cref="IServiceControl.PlanReapplyAsync()"/>):
    /// 直近のリロードが反映できていなければ止める。取り込みは前の設定のまま動いているので、再適用だけを新しい設定で
    /// 走らせない(今までと同じ)。</item>
    /// <item><b>保存前の下書きで確かめる</b>(管理画面の番組ページ。<see cref="IServiceControl.PlanReapplyAsync(Airshelf.Core.Configuration.ProgramMasterConfig)"/>):
    /// 止めない。番組の設定はディスクの programs.json ではなく下書きから取るので、programs.json が壊れていたせいの
    /// 古い失敗で止める理由が無い(止めると、原因を直しても確認に進めず、保存して適用からやり直せない)。stations.json を
    /// 読めなければ、確認の dry-run でその理由が出る。「リロードに失敗したら再適用しない」は、実行の段
    /// (<see cref="IServiceControl.ReloadAndApplyReapplyAsync"/>。実行の前に必ずリロードを試し直す)で守る。</item>
    /// </list>
    /// </summary>
    public static string? BlockingReloadError(bool hasDraft, string? lastReloadError)
        => hasDraft ? null : lastReloadError;
}
