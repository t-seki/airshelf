namespace Airshelf.Core.Model;

/// <summary>
/// 放送回を録音したディスク上の物理ファイル(物理担体)。
/// </summary>
public sealed record RecordingFile
{
    /// <summary>ファイルの絶対パス。</summary>
    public required string FilePath { get; init; }

    /// <summary>ファイル名(ディレクトリを含まない)。</summary>
    public required string FileName { get; init; }

    /// <summary>親の放送局フォルダ名(<c>{放送局}/{番組}/file</c> の放送局階層)。</summary>
    public required string StationFolder { get; init; }

    /// <summary>親の番組フォルダ名(<c>{放送局}/{番組}/file</c> の番組階層)。</summary>
    public required string ProgramFolder { get; init; }

    /// <summary>拡張子(先頭ドット付き・小文字、例 <c>.m4a</c>)。</summary>
    public required string Extension { get; init; }

    /// <summary>ファイルサイズ(バイト)。安定判定に用いる。</summary>
    public long SizeBytes { get; init; }

    /// <summary>最終更新時刻(UTC)。安定判定に用いる。</summary>
    public DateTimeOffset LastWriteTimeUtc { get; init; }
}
