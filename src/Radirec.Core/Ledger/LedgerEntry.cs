namespace Radirec.Core.Ledger;

/// <summary>取り込み済みの放送回 1 件の記録。</summary>
public sealed record LedgerEntry
{
    public required string IdentityKey { get; init; }
    public required string Station { get; init; }
    public required string Program { get; init; }
    public required DateOnly BroadcastDate { get; init; }
    public required string SourcePath { get; init; }
    public required string OutputPath { get; init; }
    public required string RuleVersion { get; init; }
    public required bool UsedFallback { get; init; }
    public required DateTimeOffset ImportedAtUtc { get; init; }
}
