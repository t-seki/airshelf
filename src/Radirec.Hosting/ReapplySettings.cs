using System.Text.Json;
using Radirec.Core.Configuration;

namespace Radirec.Hosting;

/// <summary>
/// 再適用(ADR-0016)が使う放送局・番組の設定。確認(dry-run)と実行で同じものを使えるよう、読み込みと
/// 組み立て(<see cref="CompositionRoot.BuildReapply(RadirecConfig, bool, ReapplySettings)"/>)を分ける。
/// </summary>
public sealed record ReapplySettings(StationMasterConfig Stations, ProgramMasterConfig Programs)
{
    /// <summary>
    /// 中身(値)が同じか。設定のクラスは参照で比べると、同じ内容を読み直しても別物になるので、JSON に書き出して比べる。
    /// 管理画面の「保存して適用」(#154)が、確かめたときの下書きと、保存して読み直した設定が同じかを判定するのに使う。
    /// </summary>
    public bool HasSameValuesAs(ReapplySettings other)
        => ProgramsFileWriter.Serialize(Programs).AsSpan().SequenceEqual(ProgramsFileWriter.Serialize(other.Programs))
           && JsonSerializer.Serialize(Stations) == JsonSerializer.Serialize(other.Stations);
}
