namespace Radirec.Hosting;

/// <summary>
/// 常駐取り込みワーカーの起動時オプション。設定リロード(トレイ常駐の in-process 制御。ADR-0012)で
/// **起動時と同じファイルを再読込**するため、解決済みの絶対パスを worker へ運ぶ(既定解決に逆戻りしないよう
/// raw 引数ではなく <c>ConfigPaths.Resolve</c> 済みの値を持つ)。
/// </summary>
/// <param name="ConfigPath">解決済みの設定ファイル絶対パス。</param>
public sealed record ServiceRuntimeOptions(string ConfigPath);
