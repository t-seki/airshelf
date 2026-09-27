using System.Globalization;
using System.Runtime.Versioning;
using Radirec.Core.Ingestion;
using Radirec.Gui.Core;

namespace Radirec.Gui;

/// <summary>
/// 取込通知を in-process でトースト表示する publisher(ADR-0012)。取り込みワーカーが同一プロセスで
/// 走るため、跨プロセスの契約を経ず Core の <see cref="IngestSummary"/> / <see cref="IngestFailureSignal"/> / <see cref="IngestHeldSignal"/> を
/// 直接受け、提示側の view model(<see cref="IngestSummaryPayload"/> 等)へ写して
/// <see cref="ToastPresenter"/> に渡す。成功要約・失敗・保留(ADR-0021)の
/// インタフェースを 1 インスタンスで実装し、ワーカーには同じインスタンスをそれぞれに注入する。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ToastNotificationPublisher(ToastPresenter presenter)
    : IIngestSummaryPublisher, IIngestFailurePublisher, IIngestHeldPublisher
{
    public Task<string?> PublishAsync(IngestSummary summary, CancellationToken cancellationToken = default)
    {
        presenter.Show(ToPayload(summary));
        return Task.FromResult<string?>(null);
    }

    public Task PublishAsync(IngestFailureSignal signal, CancellationToken cancellationToken = default)
    {
        presenter.ShowFailure(ToPayload(signal));
        return Task.CompletedTask;
    }

    public Task PublishAsync(IngestHeldSignal signal, CancellationToken cancellationToken = default)
    {
        presenter.ShowHeld(ToPayload(signal));
        return Task.CompletedTask;
    }

    // Core の domain 要約 → 提示側 view model。放送日は yyyy-MM-dd、放送時刻は HH:mm(取得不能なら null)。
    // formatter が期待する既存の文字列形式に合わせる(InvariantCulture: ':' が time separator に化けない)。
    private static IngestSummaryPayload ToPayload(IngestSummary summary) => new()
    {
        TotalImported = summary.TotalImported,
        Truncated = summary.Truncated,
        Albums = summary.Albums.Select(album => new IngestSummaryAlbumPayload
        {
            Station = album.Station,
            Program = album.Program,
            EpisodeCount = album.EpisodeCount,
            FirstEpisode = new IngestSummaryEpisodePayload
            {
                BroadcastDate = album.FirstEpisode.BroadcastDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                BroadcastTime = album.FirstEpisode.BroadcastTime?.ToString("HH:mm", CultureInfo.InvariantCulture),
                PartIndex = album.FirstEpisode.PartIndex,
            },
        }).ToList(),
    };

    private static IngestFailurePayload ToPayload(IngestFailureSignal signal) => new()
    {
        FailedCount = signal.FailedCount,
        Truncated = signal.Truncated,
        Files = signal.Files.Select(file => new IngestFailureFilePayload
        {
            Name = file.Name,
            Reason = file.Reason,
        }).ToList(),
    };

    private static IngestHeldPayload ToPayload(IngestHeldSignal signal) => new()
    {
        HeldCount = signal.HeldCount,
        Truncated = signal.Truncated,
        Groups = signal.Groups.Select(group => new IngestHeldGroupPayload
        {
            Folder = group.Folder,
            FilenameProgram = group.FilenameProgram,
            Count = group.Count,
        }).ToList(),
    };
}
