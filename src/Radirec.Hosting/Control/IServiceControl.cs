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

    /// <summary>
    /// 設定リロードを要求する。新 config の読込+構文検証だけ**同期実行**し、妥当なら再構築を要求して
    /// 「受理」を返す(実グラフ差し替えは在中スキャン完了後に直列ループ上で。ADR-0010)。不正なら旧構成を
    /// 維持して失敗を返す。**受理 ≠ 反映完了**: 再構築(SQLite 再オープン等)は後追いで、失敗時は旧構成を
    /// 維持し EventLog に残す。
    /// </summary>
    ReloadResult RequestReload();
}
