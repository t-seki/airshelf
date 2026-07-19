namespace Radirec.Core.Organization;

/// <summary>1 ファイルの仕分け結果の種別(#81)。</summary>
public enum OrganizeOutcome
{
    /// <summary>移動した(dry-run では「移動予定」)。</summary>
    Moved,

    /// <summary>コピーした(<c>--copy</c>。dry-run では「コピー予定」)。</summary>
    Copied,

    /// <summary>移動先に同一内容(サイズ+最終更新)が既存 → スキップ(移動元は残す)。</summary>
    DuplicateSkipped,

    /// <summary>移動先に別内容の同名が既存 → 未処理・要確認(上書きせず移動元も残す)。</summary>
    Conflict,

    /// <summary>既に移動先=現在地(何もしない)。</summary>
    AlreadyInPlace,

    /// <summary>スキップパターン一致(ジャンク)→ アーカイブに入れない。</summary>
    SkippedJunk,

    /// <summary>どの importer も扱えない(拡張子/命名が想定外)。</summary>
    Unsupported,

    /// <summary>パース or 移動に失敗(移動元は残す)。</summary>
    Failed,
}

/// <summary>1 ファイルの仕分け結果。</summary>
public sealed record OrganizeItemResult
{
    public required OrganizeOutcome Outcome { get; init; }
    public required string SourcePath { get; init; }

    /// <summary>算出した移動先(対象外/ジャンク等で無い場合 null)。</summary>
    public string? TargetPath { get; init; }

    /// <summary>失敗理由など(無ければ null)。</summary>
    public string? Message { get; init; }
}

/// <summary>仕分け実行全体の結果。</summary>
public sealed record OrganizeReport
{
    public required IReadOnlyList<OrganizeItemResult> Items { get; init; }
    public required bool DryRun { get; init; }
    public required bool Copy { get; init; }

    public int Count(OrganizeOutcome outcome) => Items.Count(i => i.Outcome == outcome);

    /// <summary>移動されず移動元に残り、人手の確認が要る件数(別内容衝突 + 失敗)。</summary>
    public int NeedsAttention =>
        Count(OrganizeOutcome.Conflict) + Count(OrganizeOutcome.Failed);
}
