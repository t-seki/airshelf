namespace Radirec.Cli.Core;

/// <summary>
/// radirec.exe の終了コード(ADR-0020)。エージェントやスクリプトが、終了コードだけで次にすることを決められるように、
/// 意味をここ 1 か所で決める。値を変えると呼び出し側のスクリプトが黙って壊れるので、変えない。
/// </summary>
public static class ExitCodes
{
    /// <summary>成功(失敗した回は 0 件)。</summary>
    public const int Success = 0;

    /// <summary>
    /// 使い方の誤り(未知のオプション・コマンド)・設定を読めない・版の不一致。引数か設定を直す。
    /// 想定外の例外(内部エラー)もこれで終える(専用の番号は無い。ADR-0020)。そのときは出力(<c>--json</c> なら <c>error</c>)に理由が出る。
    /// </summary>
    public const int UsageOrConfig = 1;

    /// <summary>常駐が動いていない(制御チャネルに繋がらない・取り込みが止まっている。#179 から使う)。</summary>
    public const int ResidentNotRunning = 2;

    /// <summary>確かめた予定が変わった(再適用の予定 ID が一致しない。#181 から使う)。dry-run からやり直す。</summary>
    public const int PlanChanged = 3;

    /// <summary>完了したが、一部の回が失敗した。明細の失敗を調べる。</summary>
    public const int PartialFailure = 4;

    /// <summary>外部との接続に失敗した(Jellyfin・NAS)。時間をおいて再試行する。</summary>
    public const int ExternalFailure = 5;
}
