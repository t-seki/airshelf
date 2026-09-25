using System.Globalization;

namespace Radirec.Core.Ledger;

/// <summary>
/// 取込履歴に出す「放送」の 1 列の表記。管理画面(<c>IngestHistoryRow</c>)と CLI の <c>radirec history</c>(#182)で共有する。
/// </summary>
public static class BroadcastLabel
{
    /// <summary>
    /// 放送日 + (時刻があれば <c>HH:mm</c>)+ (part&gt;0 なら <c>(N)</c>)。時刻なし(radikool 等)は日付のみ。
    /// 同日別放送の区別(時刻 or part)を1列で読めるようにする。
    /// </summary>
    public static string Format(DateOnly date, TimeOnly? time, int partIndex)
    {
        var text = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (time is { } t)
            text += " " + t.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (partIndex > 0)
            text += $" ({partIndex})";
        return text;
    }
}
