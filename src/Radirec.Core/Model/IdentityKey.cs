using Radirec.Core.Normalization;

namespace Radirec.Core.Model;

/// <summary>
/// 放送回を一意に識別する値オブジェクト = <c>(正規放送局, 正規番組名, 放送日=日単位)</c>。
/// 等価性・ハッシュは照合キー(<see cref="TextNormalizer.ToMatchKey"/>)化した値で判定するため、
/// 空白揺れや全角半角の違いを吸収して重複排除できる。時刻は同一性に関与しない。
/// </summary>
public sealed class IdentityKey : IEquatable<IdentityKey>
{
    public IdentityKey(string canonicalStation, string canonicalProgram, DateOnly broadcastDate)
    {
        CanonicalStation = canonicalStation;
        CanonicalProgram = canonicalProgram;
        BroadcastDate = broadcastDate;
        Value = string.Join(
            '|',
            TextNormalizer.ToMatchKey(canonicalStation),
            TextNormalizer.ToMatchKey(canonicalProgram),
            broadcastDate.ToString("yyyy-MM-dd"));
    }

    /// <summary>正規放送局名(表示用)。</summary>
    public string CanonicalStation { get; }

    /// <summary>正規番組名(表示用)。</summary>
    public string CanonicalProgram { get; }

    /// <summary>放送日(日単位)。</summary>
    public DateOnly BroadcastDate { get; }

    /// <summary>台帳の主キーになる安定文字列(照合キー化済み)。</summary>
    public string Value { get; }

    public bool Equals(IdentityKey? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as IdentityKey);

    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => Value;
}
