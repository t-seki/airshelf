namespace Radirec.Gui.Core;

/// <summary>
/// 取込失敗をトースト表示するための presentation view model(ADR-0011/0012)。トレイ常駐移行で
/// 取込通知は in-process 化したため、Core の <c>IngestFailureSignal</c> を publisher 側で本型へ写して
/// <see cref="IngestToastFormatter"/> に渡す。
/// </summary>
public sealed record IngestFailurePayload
{
    /// <summary>新しく現れた失敗の総数(de-dup 済み。キャップ前の実数)。</summary>
    public int FailedCount { get; init; }

    /// <summary>表示上限により files を間引いたか。</summary>
    public bool Truncated { get; init; }

    public IReadOnlyList<IngestFailureFilePayload> Files { get; init; } = [];
}

/// <summary>失敗した 1 ファイル(ファイル名 + 理由)。フルパスは運ばれない。</summary>
public sealed record IngestFailureFilePayload
{
    public string Name { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}
