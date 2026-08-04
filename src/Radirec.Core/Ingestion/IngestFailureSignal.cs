using Radirec.Core.Ledger;

namespace Radirec.Core.Ingestion;

/// <summary>de-dup を通過した「新しく現れた失敗」1 件(1002 発行対象。ADR-0011)。</summary>
public sealed record NewIngestFailure
{
    public required string SourcePath { get; init; }
    public required FileFingerprint Fingerprint { get; init; }
    public required string Reason { get; init; }

    /// <summary>
    /// 表示・シグナルに載せるのはファイル名のみ(フルパスは晒さない)。ソースは Windows パス(バックスラッシュ)
    /// だが、非 Windows(WSL/CI)で処理・テストしても正しく末尾要素を取れるよう両区切りで分割する
    /// (<c>Path.GetFileName</c> は Linux でバックスラッシュを区切りと見なさない)。
    /// </summary>
    public string FileName => SourcePath[(SourcePath.LastIndexOfAny(['/', '\\']) + 1)..];
}

/// <summary>
/// 取込失敗シグナルのペイロード。1 走査で**新しく現れた失敗**を有界化して運ぶ domain 表現。
/// 成功要約(<see cref="IngestSummary"/>)とは別種の独立シグナル(ADR-0011)。提示の手段に依存しない。
/// </summary>
public sealed record IngestFailureSignal
{
    /// <summary>表示するファイル行の上限。超過分は落とし <see cref="Truncated"/> を立てる(記録は全件、表示のみ有界)。</summary>
    public const int MaxFiles = 20;

    /// <summary>
    /// 1 件の理由の文字数上限。例外メッセージは青天井になりうるため切り詰め、payload 全体を有界化する。
    /// <para>
    /// **この値の元の根拠は失効している**: 元は EventLog の ~32KB 上限で <c>WriteEntry</c> が投げて
    /// 「毎走査再失敗」に陥るのを防ぐためだった(ADR-0011)。EventLog は ADR-0012 で撤去済み。
    /// 現在の提示側は <c>IngestToastFormatter</c> が独自に本文 2 行まで絞るため、この上限は
    /// 事実上「青天井を避ける安全弁」としてのみ効いている。値と役割の見直しは #100。
    /// </para>
    /// </summary>
    public const int MaxReasonLength = 200;

    /// <summary>新しく現れた失敗の総数(キャップ前の実数)。de-dup 通過分のみを数える。</summary>
    public required int FailedCount { get; init; }

    /// <summary>キャップにより files を間引いたか。</summary>
    public required bool Truncated { get; init; }

    /// <summary>失敗したファイル(最大 <see cref="MaxFiles"/> 件)。</summary>
    public required IReadOnlyList<IngestFailureFile> Files { get; init; }

    /// <summary>
    /// 新規失敗群からシグナルを組み立てる。<see cref="FailedCount"/> は全件、<see cref="Files"/> は表示上限まで
    /// (超過は de-dup ストアには全件記録済みなので再通知されない)。出現順を保つ。
    /// </summary>
    public static IngestFailureSignal From(IReadOnlyList<NewIngestFailure> failures) => new()
    {
        FailedCount = failures.Count,
        Truncated = failures.Count > MaxFiles,
        Files = failures.Take(MaxFiles)
            .Select(f => new IngestFailureFile { Name = f.FileName, Reason = Truncate(f.Reason) })
            .ToList(),
    };

    private static string Truncate(string reason) =>
        reason.Length <= MaxReasonLength ? reason : reason[..MaxReasonLength] + "…";
}

/// <summary>失敗した 1 ファイル(ファイル名 + 理由)。フルパスは運ばない。</summary>
public sealed record IngestFailureFile
{
    public required string Name { get; init; }
    public required string Reason { get; init; }
}
