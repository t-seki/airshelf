using Radirec.Core.Configuration;
using Radirec.Core.Reapply;

namespace Radirec.Hosting.Control;

/// <summary>
/// トレイメニューと管理画面が常駐 worker へ触れる**狭い仲介面**(ADR-0010 / 0012)。実装は <c>RadioMonitorWorker</c>。
/// 呼び出し側は worker の内部実装ではなくこの interface だけに依存し、結合を最小化する。
/// DI singleton として worker と同一インスタンスを共有する。
/// <para>
/// **スレッド安全性契約**: トレイ常駐移行(ADR-0012)後は UI スレッドから呼ばれ、ワーカーは別スレッドで
/// 走る。メソッドは**別スレッドと並行に**呼ばれうる(status 読み取り中に worker が状態を更新する等)ため、
/// 実装はスレッドセーフであること。既定実装は rescan=Channel・status=volatile 参照読み・
/// reload=Interlocked/lock・再適用と走査の実行=キュー + TaskCompletionSource で満たす。
/// </para>
/// </summary>
public interface IServiceControl
{
    /// <summary>
    /// 手動再スキャンを要求する(取り込み走査を今すぐ 1 回)。既存の要求チャネルへ coalesce される
    /// 冪等操作なので「受理」まで(スキャン完了は待たない)。
    /// </summary>
    void RequestRescan();

    /// <summary>
    /// 走査の実行(CONTEXT.md。ADR-0020 / #180。CLI の <c>ingest</c>)。取り込み走査を 1 回行い、**終わるまで待って結果を返す**。
    /// 直列ループの上で走るので、取り込みの走査・再適用と同時には走らない(取り込み中に頼んだら、終わるのを待ってから走る)。
    /// 結果は頼んだ側に返るので新規取込のトーストは出さない。取込失敗のトーストは出し、失敗のストアにも記録する
    /// (<see cref="ScanRunSummaryGate"/>)。<see cref="RequestRescan"/>(受理で返る)とは別の指示。
    /// </summary>
    Task<ScanRunResult> RunScanAsync();

    /// <summary>ライブ状態のスナップショットを返す(在中スキャン中でも即応)。</summary>
    ServiceStatusSnapshot GetStatus();

    /// <summary>
    /// 設定リロードを要求する。新 config の読込+構文検証だけ**同期実行**し、妥当なら再構築を要求して
    /// 「受理」を返す(実グラフ差し替えは在中スキャン完了後に直列ループ上で。ADR-0010)。不正なら旧構成を
    /// 維持して失敗を返す。**受理 ≠ 反映完了**: 再構築(SQLite 再オープン等)は後追いで、失敗時は旧構成を
    /// 維持し <c>gui.log</c> に残す。
    /// </summary>
    ReloadResult RequestReload();

    /// <summary>
    /// 統合設定の再適用の確認(ADR-0016 / #126)。まず設定リロードを行い(<see cref="RequestReload"/> と同じ検証・
    /// 反映)、反映できたら再適用を dry-run で実行した結果を返す。どちらも直列ループの上で行うので、取り込みと
    /// 同時には走らない(在中スキャンの完了を待つ)。設定の読み込み・反映に失敗したら dry-run に進まず失敗を返す。
    /// </summary>
    Task<ReapplyResult> PlanReapplyAsync();

    /// <summary>
    /// 保存前の番組の設定で再適用を確かめる(管理画面の番組ページの「確認」。#154)。ファイルから設定リロードはせず、
    /// 今の設定(radirec.json)とディスクの <c>stations.json</c>、渡した <paramref name="programs"/> で dry-run する。
    /// 直列ループの上で行う(取り込み中は終わるのを待つ)。直近の設定リロードの反映の失敗では止めない
    /// (<see cref="ReapplyPlanGate"/>。番組の設定は下書きから取るので、原因を直した後にやり直せるように)。stations.json を
    /// 読めなければ、その理由で失敗を返す。実行は、保存してから <see cref="ReloadAndApplyReapplyAsync"/> で行う
    /// (実行の前に必ずリロードを試し直し、失敗なら実行しない)。
    /// </summary>
    Task<ReapplyResult> PlanReapplyAsync(ProgramMasterConfig programs);

    /// <summary>
    /// 保存した番組の設定で、**設定リロード → 再適用の実行**を 1 つの続きとして行う(管理画面の番組ページの「保存して適用」。
    /// #154・ADR-0016)。直列ループの 1 つのジョブの中で続けて行い、間に取り込みの走査を挟まない。
    /// <list type="number">
    /// <item>設定をファイルから読み直して反映する(<see cref="RequestReload"/> と同じ検証・組み直し)。読めない・組み直せない
    /// なら再適用は実行せず、理由を返す(取り込みは前の設定のまま動き続ける)。</item>
    /// <item>読み直した番組・放送局の設定が、<paramref name="plan"/> を確かめたとき(<see cref="PlanReapplyAsync(ProgramMasterConfig)"/>)
    /// の設定と**値で**同じかを確かめる。違えば <see cref="ReapplyResult.IsStale"/> の失敗を返す。</item>
    /// <item>読み直した設定で dry-run し直し、予定が <paramref name="plan"/> と同じときだけ実行する(違えば IsStale)。</item>
    /// </list>
    /// 失敗・中止した後は、もう一度確かめてから呼び直せる(設定リロードも試し直す)。
    /// </summary>
    Task<ReapplyResult> ReloadAndApplyReapplyAsync(ReapplyReport plan);

    /// <summary>
    /// 確認した再適用を実行する(直列ループの上で)。<paramref name="plan"/> は直前の <see cref="PlanReapplyAsync()"/>
    /// (保存済みの設定で、設定リロードを反映してから確かめたもの)が返した結果で、その後に設定・状態が変わっていれば
    /// 実行せず、<see cref="ReapplyResult.IsStale"/> の失敗を返す。確かめたときの番組・放送局の設定で実行する
    /// (ディスクを読み直さない)。保存前の設定で確かめた結果は <see cref="ReloadAndApplyReapplyAsync"/> で実行する。
    /// </summary>
    Task<ReapplyResult> ApplyReapplyAsync(ReapplyReport plan);

    /// <summary>
    /// CLI の <c>reapply</c> の確認(#181・ADR-0020)。設定リロードはせず、今の設定(radirec.json)とディスクの <c>programs.json</c> /
    /// <c>stations.json</c> で dry-run し、予定と予定 ID(<see cref="ReapplyResult.PlanId"/>)を返す。直列ループの上で行う。
    /// 常駐の状態(設定・管理画面の直前の確認)は変えないので、管理画面で開いている確認を古くしない。
    /// 取り込みが止まっていれば <see cref="ReapplyResult.IsStopped"/> の失敗を返す。台帳のある場所(NAS など)に届かなければ
    /// <see cref="ReapplyResult.IsUnreachable"/> の失敗を返す(#223)。
    /// </summary>
    Task<ReapplyResult> PlanReapplyForCliAsync();

    /// <summary>
    /// CLI の <c>reapply --apply &lt;予定 ID&gt;</c>(#181・ADR-0020)。直列ループの 1 つのジョブの中で、設定リロード → 読み直した設定で
    /// dry-run → 予定 ID が <paramref name="planId"/> と一致したときだけ実行する(間に取り込みの走査を挟まない)。一致しなければ実行せず、
    /// dry-run し直した新しい予定と予定 ID を <see cref="ReapplyResult.IsStale"/> で返す。リロードで読めない・反映できなければ実行しない。
    /// 台帳のある場所(NAS など)に届かないときは、反映の失敗・再適用の組み立ての失敗のどちらで止まっても
    /// <see cref="ReapplyResult.IsUnreachable"/> の失敗を返す(#223)。管理画面の直前の確認には触れない。
    /// </summary>
    Task<ReapplyResult> ApplyReapplyPlanAsync(string planId);
}
