namespace Airshelf.Core.Model;

/// <summary>正規化ルールの版。台帳に記録し、将来ルール変更時の再適用判断に使う。</summary>
public static class NormalizationRuleVersion
{
    // m7-1: 番組の同一性をファイル名からフォルダ名へ変更(ADR-0001)。
    public const string Current = "m7-1";
}
