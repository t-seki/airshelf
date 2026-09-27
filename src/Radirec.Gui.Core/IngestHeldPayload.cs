namespace Radirec.Gui.Core;

/// <summary>
/// 保留シグナル(ADR-0021)をトースト表示するための presentation view model。Core の <c>IngestHeldSignal</c> を publisher 側で
/// 本型へ写して <see cref="IngestToastFormatter"/> に渡す。
/// </summary>
public sealed record IngestHeldPayload
{
    /// <summary>初めて保留した録音ファイルの数(打ち切らない実数)。</summary>
    public int HeldCount { get; init; }

    /// <summary>上限により groups を間引いたか。</summary>
    public bool Truncated { get; init; }

    public IReadOnlyList<IngestHeldGroupPayload> Groups { get; init; } = [];
}

/// <summary>保留した録音ファイルの 1 まとまり(フォルダ + ファイル名の番組名)。フルパスは運ばれない。</summary>
public sealed record IngestHeldGroupPayload
{
    public string Folder { get; init; } = string.Empty;
    public string FilenameProgram { get; init; } = string.Empty;
    public int Count { get; init; }
}
