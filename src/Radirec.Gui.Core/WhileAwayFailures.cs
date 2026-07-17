namespace Radirec.Gui.Core;

/// <summary>
/// 不在中サマリの**失敗側**集約(ADR-0011 / 軸A A3)。受け手停止中に発行された複数の取込失敗シグナル(1002)を
/// 1 件の <see cref="IngestFailurePayload"/> へマージする。成功側(<see cref="WhileAwaySummary"/>)と対で、
/// 不在中は成功 1 トースト + 失敗 1 トーストになる。マージ結果は <see cref="IngestToastFormatter"/> でトースト化できる。
/// </summary>
public static class WhileAwayFailures
{
    /// <summary>表示・保持するファイル行の上限(マージ後。超過は落として Truncated を立てる)。</summary>
    public const int MaxFiles = 20;

    /// <summary>複数の失敗シグナルを 1 件へマージする。空入力なら <see cref="IngestFailurePayload.FailedCount"/>=0。</summary>
    public static IngestFailurePayload Merge(IReadOnlyList<IngestFailurePayload> signals)
    {
        var total = 0;
        var truncated = false;
        var files = new List<IngestFailureFilePayload>();

        foreach (var signal in signals)
        {
            total += signal.FailedCount;
            truncated |= signal.Truncated; // どれかが表示上限に達していたら truncated。

            foreach (var file in signal.Files)
            {
                if (files.Count >= MaxFiles)
                {
                    truncated = true; // マージ後の上限超過も truncated。
                    break;
                }
                files.Add(file);
            }
        }

        return new IngestFailurePayload
        {
            SchemaVersion = NotificationContract.SupportedFailureSchemaVersion,
            FailedCount = total,
            Truncated = truncated,
            Files = files,
        };
    }
}
