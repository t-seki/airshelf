using Radirec.Core.Configuration;

namespace Radirec.Hosting;

/// <summary>
/// 再適用(ADR-0016)が使う放送局・番組の設定。確認(dry-run)と実行で同じものを使えるよう、読み込みと
/// 組み立て(<see cref="CompositionRoot.BuildReapply(RadirecConfig, bool, ReapplySettings)"/>)を分ける。
/// </summary>
public sealed record ReapplySettings(StationMasterConfig Stations, ProgramMasterConfig Programs);
