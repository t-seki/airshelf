namespace Airshelf.Hosting.Control;

/// <summary>
/// 設定リロード要求の同期結果(ADR-0010)。**「受理」は新 config の構文検証通過 + 再構築キュー投入まで**を
/// 意味し、実際のランタイム再構築(在中スキャン完了後・後追い)の成否は含まない。再構築失敗時は旧構成を
/// 維持し <c>gui.log</c> に残す。
/// </summary>
public sealed record ReloadResult
{
    /// <summary>検証を通過し再構築を受理したか。</summary>
    public required bool Accepted { get; init; }

    /// <summary>拒否理由(config 検証失敗等。受理時 null)。</summary>
    public string? Error { get; init; }

    public static ReloadResult Ok() => new() { Accepted = true };
    public static ReloadResult Failed(string error) => new() { Accepted = false, Error = error };
}
