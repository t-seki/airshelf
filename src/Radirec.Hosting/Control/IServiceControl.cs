namespace Radirec.Hosting.Control;

/// <summary>
/// listener が常駐 worker へ触れる**狭い仲介面**(ADR-0010)。実装は <c>RadioMonitorWorker</c>。
/// listener は worker の内部実装ではなくこの interface だけに依存し、結合を最小化する。
/// DI singleton として worker と同一インスタンスを共有する。
/// </summary>
public interface IServiceControl
{
    /// <summary>
    /// 手動再スキャンを要求する(取り込み走査を今すぐ 1 回)。既存の要求チャネルへ coalesce される
    /// 冪等操作なので「受理」まで(スキャン完了は待たない)。
    /// </summary>
    void RequestRescan();

    /// <summary>ライブ状態のスナップショットを返す(在中スキャン中でも即応)。</summary>
    ServiceStatusSnapshot GetStatus();
}
