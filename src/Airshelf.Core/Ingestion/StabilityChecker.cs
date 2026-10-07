using Airshelf.Core.Model;

namespace Airshelf.Core.Ingestion;

public interface IStabilityChecker
{
    bool IsStable(RecordingFile file);
}

/// <summary>
/// らじれこ書き込み中のファイルを掴まないためのガード。最終更新から一定時間経過し、かつ
/// サイズが 0 でないものだけを安定とみなす(M3 で FileSystemWatcher ベースへ強化)。
/// </summary>
public sealed class StabilityChecker(TimeProvider clock, TimeSpan minimumQuietPeriod) : IStabilityChecker
{
    public bool IsStable(RecordingFile file)
    {
        if (file.SizeBytes <= 0)
            return false;

        var age = clock.GetUtcNow() - file.LastWriteTimeUtc;
        return age >= minimumQuietPeriod;
    }
}
