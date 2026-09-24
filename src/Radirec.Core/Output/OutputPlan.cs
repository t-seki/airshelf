namespace Radirec.Core.Output;

/// <summary>
/// 配置先の算出結果(<see cref="IOutputTarget.Plan"/>)。出力先の同名衝突をどう解いたかも運ぶ(ADR-0015)。
/// </summary>
public sealed record OutputPlan
{
    /// <summary>
    /// 書き込み先のパス。<see cref="AlreadyPresent"/> のときは、同じ内容の既存ファイルのパス。
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// 出力先に同じ内容(タグ以外の音声部分が一致)のファイルが既にある。書き込まずに取込済みとして扱う。
    /// </summary>
    public bool AlreadyPresent { get; init; }

    /// <summary>
    /// 内容の違う(または判定できない)同名ファイルを避けて別名にしたとき、その既存ファイルのパス。
    /// 衝突が無ければ null。
    /// </summary>
    public string? CollidedWith { get; init; }
}
