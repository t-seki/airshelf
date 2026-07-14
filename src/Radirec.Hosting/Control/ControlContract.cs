using System.Text.Json;
using System.Text.Json.Serialization;

namespace Radirec.Hosting.Control;

/// <summary>
/// GUI/CLI → サービスの制御チャネル(ADR-0010)の跨プロセス契約。pipe 名・コマンド名・schemaVersion・
/// wire JSON 設定を 1 箇所に集約する。CLI クライアントは <c>radirec.exe</c> と同一バイナリ(ADR-0005)
/// なのでこの型を直接共有する。別 exe の GUI(#5)は跨プロセス型を共有せず、この JSON 契約に自前 DTO で
/// 追従する(<c>NotificationContract</c> と同じ流儀)。
/// </summary>
public static class ControlContract
{
    /// <summary>制御チャネルの named pipe 名(ローカル・単一インスタンス前提)。</summary>
    public const string PipeName = "radirec-control";

    /// <summary>request/response envelope のスキーマ版(前方進化用)。</summary>
    public const int SchemaVersion = 1;

    /// <summary>1 メッセージの最大バイト数(フレーム長の上限。DoS/破損フレーム対策)。</summary>
    public const int MaxMessageBytes = 1 << 20; // 1 MiB

    /// <summary>制御コマンド名(CONTEXT.md「制御コマンド」)。</summary>
    public static class Commands
    {
        /// <summary>手動再スキャン(取り込み走査を今すぐ 1 回要求)。</summary>
        public const string Rescan = "rescan";

        /// <summary>状態取得(ライブ状態のスナップショット)。</summary>
        public const string Status = "status";

        /// <summary>設定リロード(#33 PR-B で実装。契約枠だけ予約)。</summary>
        public const string Reload = "reload";
    }

    /// <summary>
    /// wire の JSON 設定。camelCase(ADR-0008 の EventLog ペイロードと一貫)・null は書かない。
    /// CJK を <c>\uXXXX</c> へ escape しないことでメッセージサイズを抑える。
    /// </summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
