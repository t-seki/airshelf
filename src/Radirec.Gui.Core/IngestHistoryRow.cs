using System.Globalization;
using Radirec.Core.Ledger;

namespace Radirec.Gui.Core;

/// <summary>
/// 取込履歴の 1 行(台帳エントリの表示用整形。S4 / #66)。WPF DataGrid にバインドする読み取り専用の
/// presentation view model。整形は純粋なのでテスト可能(Gui.Core)。
/// </summary>
public sealed record IngestHistoryRow
{
    public required string Program { get; init; }
    public required string Station { get; init; }

    /// <summary>放送日(+時刻・part)。例 <c>2026-06-12 08:30</c> / <c>2026-06-12 (2)</c> / <c>2026-06-12</c>。</summary>
    public required string Broadcast { get; init; }

    /// <summary>取込日時(ローカル時刻)。例 <c>2026-07-10 12:00</c>。</summary>
    public required string ImportedAt { get; init; }

    /// <summary>出力先のフルパス。グリッドには出さず、ToolTip / 詳細ペイン / エクスプローラー起動で使う。</summary>
    public required string OutputPath { get; init; }

    /// <summary>
    /// 取込元のフルパス。グリッドには出さず、詳細ペイン / エクスプローラー起動で使う(#87)。
    /// 取込後に消えている/移動していることがあるため、存在は保証しない。
    /// </summary>
    public required string SourcePath { get; init; }

    /// <summary>
    /// グリッドに出す出力先の表示値 = ファイル名のみ(#87)。放送局・番組・放送日は既に別列にあるため、
    /// フルパスを列に入れても情報は増えず横幅だけを食う。全文は ToolTip と詳細ペインで見せる。
    /// </summary>
    public string FileName => PathReveal.FileName(OutputPath);

    public static IngestHistoryRow From(LedgerEntry entry) => new()
    {
        Program = entry.Program,
        Station = entry.Station,
        Broadcast = FormatBroadcast(entry.BroadcastDate, entry.BroadcastTime, entry.PartIndex),
        ImportedAt = entry.ImportedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        OutputPath = entry.OutputPath,
        SourcePath = entry.SourcePath,
    };

    /// <summary>
    /// 放送日 + (時刻があれば <c>HH:mm</c>)+ (part&gt;0 なら <c>(N)</c>)。時刻なし(radikool 等)は日付のみ。
    /// 同日別放送の区別(時刻 or part)を1列で読めるようにする。
    /// </summary>
    public static string FormatBroadcast(DateOnly date, TimeOnly? time, int partIndex)
    {
        var text = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (time is { } t)
            text += " " + t.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (partIndex > 0)
            text += $" ({partIndex})";
        return text;
    }
}
