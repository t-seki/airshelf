namespace Radirec.Core.Ingestion;

/// <summary>
/// 保留シグナル(CONTEXT.md。ADR-0021)のペイロード。1 走査で**初めて保留した**録音ファイルを、(フォルダ, ファイル名の番組名)
/// ごとにまとめて運ぶ。取込要約シグナル・取込失敗シグナルとは別種の独立シグナル。提示の手段に依存しない。
/// </summary>
public sealed record IngestHeldSignal
{
    /// <summary>運ぶまとまりの上限。超えた分は落とし <see cref="Truncated"/> を立てる(payload を有界にする安全弁)。</summary>
    public const int MaxGroups = 20;

    /// <summary>初めて保留した録音ファイルの数(打ち切らない実数)。</summary>
    public required int HeldCount { get; init; }

    /// <summary>上限により <see cref="Groups"/> を間引いたか。</summary>
    public required bool Truncated { get; init; }

    /// <summary>(局, フォルダ, ファイル名の番組名) ごとのまとまり(出現順。最大 <see cref="MaxGroups"/>)。</summary>
    public required IReadOnlyList<IngestHeldGroup> Groups { get; init; }

    /// <summary>初めて保留した録音ファイル群からシグナルを組み立てる。</summary>
    public static IngestHeldSignal From(IReadOnlyList<NewHeldRecording> held)
    {
        var groups = held
            .GroupBy(h => (h.Station, h.Folder, h.FilenameProgram))
            .Select(g => new IngestHeldGroup(g.Key.Station, g.Key.Folder, g.Key.FilenameProgram, g.Count()))
            .ToList();
        return new IngestHeldSignal
        {
            HeldCount = held.Count,
            Truncated = groups.Count > MaxGroups,
            Groups = groups.Take(MaxGroups).ToList(),
        };
    }
}

/// <summary>保留シグナルの 1 まとまり。フルパスは運ばない。</summary>
/// <param name="Count">このまとまりで初めて保留した録音ファイルの数。</param>
public sealed record IngestHeldGroup(string Station, string Folder, string FilenameProgram, int Count);

/// <summary>1 走査で初めて保留した録音ファイル 1 件(保留シグナルの元)。</summary>
public sealed record NewHeldRecording(string SourcePath, string Station, string Folder, string FilenameProgram);

/// <summary>
/// 保留シグナルを発行する(ADR-0021)。提示の手段は実装側に隠す。発行失敗は呼び出し側で握られる。
/// </summary>
public interface IIngestHeldPublisher
{
    /// <summary>保留シグナルを 1 件発行する。</summary>
    Task PublishAsync(IngestHeldSignal signal, CancellationToken cancellationToken = default);
}

/// <summary>何もしない実装(dry-run / publisher を注入しない経路)。</summary>
public sealed class NullIngestHeldPublisher : IIngestHeldPublisher
{
    public Task PublishAsync(IngestHeldSignal signal, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
