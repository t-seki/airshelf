using System.Text.Json;
using System.Text.Json.Serialization;

namespace Radirec.Gui.Core.Control;

/// <summary>
/// 制御チャネル(ADR-0010)を利用者側 GUI から叩くための跨プロセス契約の**受け手側複製**。
/// 発行側の <c>Radirec.Hosting.Control.ControlContract</c> と一致している必要があり、一致は契約テストで
/// 保証する(層構成上 GUI は Hosting を参照しないため、値は複製しテストでドリフトを検出する。
/// <c>NotificationContract</c> と同じ流儀)。named pipe を「バージョン付き JSON を運ぶトランスポート」として
/// 扱い、型は共有しない。
/// </summary>
public static class ControlContract
{
    /// <summary>制御チャネルの named pipe 名(ローカル・単一インスタンス前提)。</summary>
    public const string PipeName = "radirec-control";

    /// <summary>request/response envelope のスキーマ版。これと一致しない版は未対応として扱う。</summary>
    public const int SchemaVersion = 1;

    /// <summary>1 メッセージの最大バイト数(フレーム長の上限。破損フレーム対策)。</summary>
    public const int MaxMessageBytes = 1 << 20; // 1 MiB

    /// <summary>制御コマンド名(CONTEXT.md「制御コマンド」)。</summary>
    public static class Commands
    {
        /// <summary>手動再スキャン(取り込み走査を今すぐ 1 回要求)。</summary>
        public const string Rescan = "rescan";

        /// <summary>状態取得(ライブ状態のスナップショット)。</summary>
        public const string Status = "status";

        /// <summary>設定リロード(サービス実質再起動相当)。</summary>
        public const string Reload = "reload";
    }

    /// <summary>
    /// wire の JSON 設定。発行側と同一(camelCase・null は書かない・CJK を <c>\uXXXX</c> へ escape しない)。
    /// 送出する要求 JSON が発行側の凍結フォーマットとバイト一致することを契約テストで固定する。
    /// </summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
