using Airshelf.Hosting;
using Airshelf.Hosting.Control;

namespace Airshelf.Tests;

/// <summary>
/// 設定リロード配送の不変条件(ADR-0010)を WSL でも検証する。reload の「取りこぼさない」核である
/// <see cref="PendingReloadSlot{T}"/> の原子的 take・最新 reload が勝つ・起こしコールバック呼び出しを
/// 固定する(worker の再構築 atomicity 自体は Windows 手動検証項目)。
/// </summary>
public sealed class PendingReloadSlotTests
{
    [Fact]
    public void Set_で格納しTakeで取り出せる_起こしが呼ばれる()
    {
        var wakes = 0;
        var slot = new PendingReloadSlot<AirshelfConfig>(() => wakes++);
        var config = new AirshelfConfig { RadioRoot = @"I:\radio" };

        slot.Set(config);

        Assert.Equal(1, wakes);
        Assert.Same(config, slot.Take());
    }

    [Fact]
    public void Take_は一度取ったらnull_二重適用しない()
    {
        var slot = new PendingReloadSlot<AirshelfConfig>(() => { });
        slot.Set(new AirshelfConfig());

        Assert.NotNull(slot.Take());
        Assert.Null(slot.Take()); // 2 回目は保留なし = 同じ reload を重複適用しない
    }

    [Fact]
    public void 複数Set_は最新のreloadが勝つ()
    {
        var slot = new PendingReloadSlot<AirshelfConfig>(() => { });
        var older = new AirshelfConfig { RadioRoot = @"I:\old" };
        var newer = new AirshelfConfig { RadioRoot = @"I:\new" };

        slot.Set(older);
        slot.Set(newer); // ループが Take する前に 2 度 Set = 最新が勝つ(取りこぼさない)

        Assert.Same(newer, slot.Take());
    }

    [Fact]
    public void 未設定ならTakeはnull()
    {
        var slot = new PendingReloadSlot<AirshelfConfig>(() => { });
        Assert.Null(slot.Take());
    }
}
