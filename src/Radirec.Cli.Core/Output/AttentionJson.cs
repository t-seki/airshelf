using Radirec.Core.Attention;
using Radirec.Core.Ledger;

namespace Radirec.Cli.Core.Output;

/// <summary>
/// <c>attention --json</c> の <c>result</c>(#183)。管理画面のホームの要対応と同じもの(<see cref="AttentionItems"/>)を、
/// 表の見た目に整形する前の値で出す。
/// </summary>
/// <param name="Count">
/// 要対応の件数。兄弟フォルダの組・失敗・再適用が要る番組のまとまり・差し替え放送の疑いの組・出力先から消す予定のまとまりを
/// 1 件ずつ数えた合計(ホームの見出しの件数と同じ)。
/// </param>
/// <param name="UnintegratedSiblings">統合設定の無い兄弟フォルダの組。programs.json を使わない設定では空。</param>
/// <param name="UnresolvedFailures">解決していない取込失敗(記録した時刻の新しい順。時刻を持たない失敗は後ろ)。</param>
/// <param name="SettingsDrift">番組の設定と食い違ったままの回のまとまり(再適用すれば消える)。</param>
/// <param name="SubstituteSuspects">
/// 差し替え放送の疑い(ADR-0021)。(局, フォルダ, ファイル名の番組名) の組ごとに、保留した回と取り込み済みの判断待ちの回の数。
/// programs.json の <c>FilenamePrograms</c> に判断を書けば消える(前方一致・部分一致〈<c>Match.Mode</c>。#240〉の判断なら、当たる組がまとめて消える)。
/// </param>
/// <param name="PendingRemovals">「取り込まない」と判断された取り込み済みの回のまとまり(再適用で出力先から消す。ADR-0021)。</param>
public sealed record AttentionJson(
    int Count,
    IReadOnlyList<UnintegratedSiblingJson> UnintegratedSiblings,
    IReadOnlyList<UnresolvedFailureJson> UnresolvedFailures,
    IReadOnlyList<SettingsDriftGroup> SettingsDrift,
    IReadOnlyList<SubstituteSuspectGroup> SubstituteSuspects,
    IReadOnlyList<PendingRemovalGroup> PendingRemovals)
{
    public static AttentionJson From(AttentionItems items) => new(
        items.Count,
        items.UnintegratedSiblings.Select(UnintegratedSiblingJson.From).ToList(),
        items.UnresolvedFailures.Select(UnresolvedFailureJson.From).ToList(),
        items.SettingsDrift,
        items.SubstituteSuspects,
        items.PendingRemovals);
}

/// <summary>解決していない取込失敗の 1 件。</summary>
/// <param name="SourcePath">取込元のフルパス。</param>
/// <param name="Reason">失敗の理由(トーストと同じ文言)。理由を持つ前(#172 の移行前)に記録された失敗は null。</param>
/// <param name="FailedAt">記録した時刻(ISO 8601。ローカルの時差つき)。時刻を持つ前に記録された失敗は null。</param>
public sealed record UnresolvedFailureJson(string SourcePath, string? Reason, DateTimeOffset? FailedAt)
{
    public static UnresolvedFailureJson From(IngestFailureRecord failure) =>
        new(failure.SourcePath, failure.Reason, failure.FailedAtUtc?.ToLocalTime());
}
