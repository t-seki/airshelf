namespace Radirec.Core.Ledger;

/// <summary>取り込み済みの放送回 1 件の記録。</summary>
public sealed record LedgerEntry
{
    public required string IdentityKey { get; init; }
    public required string Station { get; init; }
    public required string Program { get; init; }
    public required DateOnly BroadcastDate { get; init; }

    /// <summary>放送時刻(同日・別放送の区別に使う)。時刻なし(radikool 等)は null。</summary>
    public TimeOnly? BroadcastTime { get; init; }

    /// <summary>
    /// 同日重複の判別子(パート序数)。時刻が同じ(または無い)同日別放送を区別する。
    /// 無ければ 0。ADR-0004。
    /// </summary>
    public int PartIndex { get; init; }
    public required string SourcePath { get; init; }
    public required string OutputPath { get; init; }
    public required string RuleVersion { get; init; }
    public required bool UsedFallback { get; init; }
    public required DateTimeOffset ImportedAtUtc { get; init; }

    /// <summary>
    /// ソースの内容フィンガープリント(サイズ + 最終更新)。取込元パス/番組フォルダ名に非依存で
    /// 「同一内容は取込済み」と判定する content ゲートに使う(#79 / ADR-0014)。既定 (0,0) は
    /// 「不明」(旧スキーマからの移送行)を表し、実ファイル(サイズ > 0)には一致しない。
    /// </summary>
    public FileFingerprint Fingerprint { get; init; }
}
