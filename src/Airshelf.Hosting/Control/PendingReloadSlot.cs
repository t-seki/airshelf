namespace Airshelf.Hosting.Control;

/// <summary>
/// 設定リロードを常駐 worker の単一直列ループへ**取りこぼさず**渡す仲介(ADR-0010)。
/// scan トリガの cap-1・DropWrite チャネル(冪等 coalesce 用)には reload を乗せられない
/// (冪等でない reload が黙殺され「受理」を返す事故になる)ため、reload は本スロットで運ぶ。
///
/// 動作: <see cref="Set"/> は最新の config を原子的に格納し(古いものは上書き = 最新 reload が勝つ)、
/// 起こしコールバックを呼ぶ。ループは各反復の先頭で <see cref="Take"/> し、非 null なら再構築する。
/// 「set 後に必ずループが 1 回は起きる」ことを起こしコールバック側(トリガチャネルへの書き込み)が保証し、
/// ループは起床時に必ず再 <see cref="Take"/> するので、reload が永久に取りこぼされることはない。
/// 両側で <see cref="Interlocked.Exchange{T}(ref T, T)"/> を使い、volatile キーワード無しでフェンスを得る。
/// </summary>
/// <typeparam name="T">運ぶ設定の型(参照型)。</typeparam>
public sealed class PendingReloadSlot<T>(Action wake) where T : class
{
    private T? _pending;

    /// <summary>最新の reload 設定を格納して直列ループを起こす(listener スレッドから呼ぶ)。</summary>
    public void Set(T value)
    {
        Interlocked.Exchange(ref _pending, value);
        wake();
    }

    /// <summary>保留中の reload 設定を原子的に取り出す(直列ループから呼ぶ)。無ければ null。</summary>
    public T? Take() => Interlocked.Exchange(ref _pending, null);
}
