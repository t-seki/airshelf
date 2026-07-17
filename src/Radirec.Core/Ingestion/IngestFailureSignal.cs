using Radirec.Core.Model;

namespace Radirec.Core.Ingestion;

/// <summary>
/// ファイルの内容フィンガープリント(サイズ + 最終更新)。同じパスで内容が変わったかの判定に使う
/// (取込失敗の de-dup。ADR-0011)。安定判定で既に取得済みの値から作れるので追加 I/O は無い。
/// </summary>
public readonly record struct FileFingerprint(long SizeBytes, long LastWriteUtcTicks)
{
    public static FileFingerprint Of(RecordingFile file) =>
        new(file.SizeBytes, file.LastWriteTimeUtc.UtcTicks);
}

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
/// 取込失敗シグナル(EventID 1002)のペイロード。1 走査で**新しく現れた失敗**を有界化して運ぶ domain 表現。
/// 成功要約(1001 / <see cref="IngestSummary"/>)とは別種の独立シグナル(ADR-0011)。transport 非依存。
/// </summary>
public sealed record IngestFailureSignal
{
    /// <summary>表示するファイル行の上限。超過分は落とし <see cref="Truncated"/> を立てる(記録は全件、表示のみ有界)。</summary>
    public const int MaxFiles = 20;

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
            .Select(f => new IngestFailureFile { Name = f.FileName, Reason = f.Reason })
            .ToList(),
    };
}

/// <summary>失敗した 1 ファイル(ファイル名 + 理由)。フルパスは運ばない。</summary>
public sealed record IngestFailureFile
{
    public required string Name { get; init; }
    public required string Reason { get; init; }
}
