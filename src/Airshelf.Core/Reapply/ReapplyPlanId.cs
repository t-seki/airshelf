using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Airshelf.Core.Reapply;

/// <summary>
/// 予定 ID(CONTEXT.md。ADR-0020 / #181)。再適用の dry-run の結果(どの回をどう動かすか)を、中身から一意に表す値。
/// CLI の <c>reapply</c> は dry-run でこれを見せ、<c>--apply &lt;予定 ID&gt;</c> のときだけ実行する。常駐は実行の直前に
/// dry-run し直してこれを計算し直し、一致したときだけ実行する(確認と実行が別々の呼び出しでも、見せた予定と違うことはしない)。
/// <para>
/// 材料は明細(<see cref="ReapplyReport.Items"/>)だけ。実行の直前の確かめ(<see cref="ReapplyReport.HasSamePlanAs"/>)と
/// 同じ項目を使う。兄弟フォルダの知らせ(<see cref="ReapplyReport.UnintegratedSiblings"/>)は実行で動かさないので入れない。
/// 明細は決まった順に並べてから計算するので、並び順に依らない。
/// </para>
/// </summary>
public static class ReapplyPlanId
{
    /// <summary>予定 ID の長さ(SHA-256 の先頭。16 進の文字数)。</summary>
    public const int Length = 16;

    /// <summary>dry-run の結果の予定 ID。変える回が無い(明細が空)ときも、空の予定の ID を返す。</summary>
    public static string Of(ReapplyReport plan) => Of(plan.Items);

    /// <summary>明細の予定 ID(16 文字の 16 進・小文字)。</summary>
    public static string Of(IEnumerable<ReapplyItem> items)
    {
        var lines = items.Select(Canonical).Order(StringComparer.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(string.Join('\n', lines));
        return Convert.ToHexStringLower(SHA256.HashData(bytes))[..Length];
    }

    /// <summary>予定 ID の形(16 文字の 16 進)か。大文字も受け付ける(比べるときは <see cref="Normalize"/> で小文字にする)。</summary>
    public static bool IsWellFormed(string? id) =>
        id is { Length: Length } && id.All(char.IsAsciiHexDigit);

    /// <summary>比べるための形(小文字)。</summary>
    public static string Normalize(string id) => id.ToLowerInvariant();

    // 1 回分を 1 行にする。値の区切りと値そのものを取り違えないよう、各値を「長さ:値」で書く(値に区切りの文字が入っても一意)。
    private static string Canonical(ReapplyItem item)
    {
        var builder = new StringBuilder();
        void Add(string? value)
        {
            if (value is null)
            {
                builder.Append("-;");
                return;
            }

            builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
        }

        Add(item.Action.ToString());
        Add(item.Station);
        Add(item.FromAlbum);
        Add(item.ToAlbum);
        Add(item.BroadcastDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(item.BroadcastTime?.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture));
        Add(item.FromPath);
        Add(item.ToPath);
        Add(item.SourcePath);
        Add(item.Reason);
        Add(item.RewritesTags ? "1" : "0");
        return builder.ToString();
    }
}
