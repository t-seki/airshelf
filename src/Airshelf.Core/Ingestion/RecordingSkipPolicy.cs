using Airshelf.Core.Model;

namespace Airshelf.Core.Ingestion;

/// <summary>
/// ジャンク録音(例 NHK の非提供アナウンス)をファイル名の部分一致で除外する。
/// パターンは config 由来。
/// </summary>
public sealed class RecordingSkipPolicy(IEnumerable<string> patterns)
{
    private readonly string[] _patterns = patterns
        .Where(p => !string.IsNullOrWhiteSpace(p))
        .ToArray();

    public bool ShouldSkip(RecordingFile file) =>
        _patterns.Any(p => file.FileName.Contains(p, StringComparison.Ordinal));
}
