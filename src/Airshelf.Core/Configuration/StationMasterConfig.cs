namespace Airshelf.Core.Configuration;

/// <summary>放送局マスタ(stations.json)。局 ID コード・表記ゆれ → 正規表示名。</summary>
public sealed class StationMasterConfig
{
    public List<StationAlias> Stations { get; set; } = [];
}

public sealed class StationAlias
{
    /// <summary>正規表示名(例 "ニッポン放送")。</summary>
    public string Canonical { get; set; } = string.Empty;

    /// <summary>別名・局 ID コード(例 "LFR")。照合キー化して一致判定する。</summary>
    public List<string> Aliases { get; set; } = [];
}
