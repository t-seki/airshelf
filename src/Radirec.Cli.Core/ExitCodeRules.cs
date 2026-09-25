using Radirec.Cli.Core.Output;
using Radirec.Core.Ingestion;
using Radirec.Core.Reapply;

namespace Radirec.Cli.Core;

/// <summary>結果から終了コード(<see cref="ExitCodes"/>)を決める規則。コマンドとテストで同じものを使う。</summary>
public static class ExitCodeRules
{
    /// <summary>
    /// 取り込み:失敗した回があれば 4、無くて Jellyfin の更新に失敗していれば 5、それ以外は 0。
    /// 回の失敗(利用者が明細を調べる)を、更新の失敗(待って再試行すればよい)より優先する。
    /// </summary>
    public static int For(IngestReport report) =>
        Combine(report.Count(IngestOutcome.Failed) > 0, report.LibraryRefresh);

    /// <summary>再適用:取り込みと同じ規則(失敗した回 → 4、Jellyfin の更新の失敗 → 5)。対象外の回は失敗に数えない。</summary>
    public static int For(ReapplyReport report) =>
        Combine(report.Count(ReapplyAction.Failed) > 0, report.LibraryRefresh);

    /// <summary>
    /// ライブラリ更新の単発起動:起動できれば 0。未設定・設定の不正は設定を直す話なので 1、繋がらない・エラー・時間切れは 5。
    /// </summary>
    public static int For(RefreshStatus status) => status switch
    {
        RefreshStatus.Started => ExitCodes.Success,
        RefreshStatus.NotConfigured or RefreshStatus.Misconfigured => ExitCodes.UsageOrConfig,
        _ => ExitCodes.ExternalFailure,
    };

    private static int Combine(bool anyFailed, LibraryRefreshStatus refresh)
    {
        if (anyFailed)
            return ExitCodes.PartialFailure;
        return refresh == LibraryRefreshStatus.Failed ? ExitCodes.ExternalFailure : ExitCodes.Success;
    }
}
