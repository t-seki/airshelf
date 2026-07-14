namespace Radirec.Cli;

/// <summary>
/// 常駐サービスの起動時オプション。設定リロード(制御チャネル。ADR-0010)で**起動時と同じファイルを
/// 再読込**するため、解決済みの絶対パスを worker へ運ぶ(既定解決に逆戻りしないよう raw 引数ではなく
/// <c>ConfigPaths.Resolve</c> 済みの値を持つ)。
/// </summary>
/// <param name="ConfigPath">解決済みの設定ファイル絶対パス。</param>
public sealed record ServiceRuntimeOptions(string ConfigPath);
