namespace Radirec.Core.Configuration;

/// <summary>
/// <c>programs.json</c> は JSON として読めたが、中身が使えない(「別の番組として取り込む」の判断にアルバム名が無いなど。ADR-0021)。
/// JSON の壊れ(<c>JsonException</c>)と同じく設定の誤りとして扱う(CLI は終了コード 1、常駐は取り込みの起動・設定の反映に失敗する)。
/// </summary>
public sealed class ProgramsConfigException(string message) : Exception(message);
