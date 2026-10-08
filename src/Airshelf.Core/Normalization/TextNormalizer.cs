using System.Globalization;
using System.Text;

namespace Airshelf.Core.Normalization;

/// <summary>
/// 放送局・番組名の「照合キー」を作る正規化。表示名は変えず、同一性判定・alias 照合に使う
/// 安定した比較キーだけを生成する。
/// </summary>
public static class TextNormalizer
{
    /// <summary>
    /// 照合キー化: Unicode NFKC(全角→半角・互換文字統一)→ 空白除去 → 英字を不変文化で大文字化。
    /// 例: <c>"LOGISTEED RADIONOMICS"</c> と <c>"SONY SONPO..."/"SONYSONPO..."</c> の空白揺れ、
    /// 全角英数(<c>ＴＯＫＹＯ ＦＭ</c>)を吸収する。らじれこのフォルダ名(空白除去)とも整合する。
    /// </summary>
    public static string ToMatchKey(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var nfkc = value.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(nfkc.Length);
        foreach (var ch in nfkc)
        {
            if (char.IsWhiteSpace(ch))
                continue;
            sb.Append(char.ToUpperInvariant(ch));
        }

        return sb.ToString();
    }
}
