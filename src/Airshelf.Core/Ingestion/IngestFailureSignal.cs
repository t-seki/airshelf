using Airshelf.Core.Ledger;

namespace Airshelf.Core.Ingestion;

/// <summary>de-dup を通過した「新しく現れた失敗」1 件(失敗シグナルの発行対象。ADR-0011)。</summary>
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
    /// <summary>
    /// 運ぶファイル行の上限。超過分は落とし <see cref="Truncated"/> を立てる(**記録は全件**、運ぶ分のみ有界)。
    /// <para>
    /// **表示のための値ではなく payload を有界にする安全弁**。提示側(<c>IngestToastFormatter</c>)は
    /// 本文を 2 行までに絞るので、この値が 3 以上なら表示は変わらない。<see cref="FailedCount"/> は
    /// 打ち切らない実数なので、表題の「取込失敗 N件」は常に正しい(#100)。
    /// </para>
    /// </summary>
    public const int MaxFiles = 20;

    /// <summary>
    /// 1 件の理由の文字数上限。例外メッセージは青天井になりうるため切り詰め、payload 全体を有界化する。
    /// <para>
    /// **こちらは表示に出る値を切る**(理由はトーストの本文に載る)ため、上の <see cref="MaxFiles"/> とは
    /// 性質が違う。元の根拠は EventLog の ~32KB 上限(ADR-0011)だったが EventLog は ADR-0012 で撤去済みで、
    /// 現在は「1 行に収まらない長文でトーストを埋めない」ための表示上の制約(#100)。
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
