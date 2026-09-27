namespace Radirec.Core.Ledger;

/// <summary>
/// 保留のストアの 1 件(CONTEXT.md「保留」。ADR-0021)。差し替え放送の疑いがあり、取り込まずにいる録音ファイル。
/// </summary>
/// <param name="SourcePath">取込元のパス。</param>
/// <param name="Station">正規放送局。</param>
/// <param name="Folder">らじれこの番組フォルダ名。</param>
/// <param name="FilenameProgram">ファイル名の番組名(CONTEXT.md)。</param>
/// <param name="HeldAtUtc">初めて保留した時刻。</param>
public sealed record HeldRecording(
    string SourcePath, string Station, string Folder, string FilenameProgram, DateTimeOffset HeldAtUtc);

/// <summary>
/// 保留のストア(ADR-0021)。走査で保留した録音ファイルを記録し、要対応(「差し替え放送の疑い」)と保留シグナル(初めて保留した
/// ものだけ)の元にする。取り込んだ・「取り込まない」と判断された・取込元から消えたら消す(取込失敗のストアと同じ消え方。#172)。
/// 永続実装は <see cref="SqliteLedger"/>(台帳と同じ DB の別テーブル)。
/// </summary>
public interface IHeldRecordingStore
{
    /// <summary>このパスを既に保留しているか。</summary>
    bool IsHeld(string sourcePath);

    /// <summary>保留を記録する(既にあるパスは置き換える)。</summary>
    void Record(HeldRecording held);

    /// <summary>そのパスの保留を消す。</summary>
    void Clear(string sourcePath);

    /// <summary>保留している全件(順序は決めない)。</summary>
    IReadOnlyList<HeldRecording> List();
}

/// <summary>何もしないストア(dry-run・通知しない経路。常に保留していない扱いで、記録も消去もしない)。</summary>
public sealed class NullHeldRecordingStore : IHeldRecordingStore
{
    public bool IsHeld(string sourcePath) => false;
    public void Record(HeldRecording held) { }
    public void Clear(string sourcePath) { }
    public IReadOnlyList<HeldRecording> List() => [];
}
