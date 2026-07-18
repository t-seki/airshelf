namespace Radirec.Hosting;

/// <summary>
/// 設定の読み込み・相対パス解決を 1 箇所に集約する(ingest/refresh/常駐トレイで共有)。
/// これにより「相対パスの rebase を忘れる」「不正 JSON を握らず raw クラッシュする」といった
/// 経路間のドリフトを構造的に防ぐ。コンソール出力は持たない(呼び出し側の関心)。
/// </summary>
public static class ConfigLoading
{
    /// <summary>
    /// <paramref name="configPath"/>(解決済み絶対パス)から読み込み・rebase する。コンソールに書かず
    /// 失敗理由を <paramref name="error"/> で返す。設定リロード(トレイ常駐の in-process 制御。ADR-0012)の
    /// ように対話コンソールが無い経路でも使えるようにする。
    /// </summary>
    public static bool TryLoad(string configPath, out RadirecConfig? config, out string? error)
    {
        config = null;
        if (!File.Exists(configPath))
        {
            error = $"設定ファイルが見つかりません: {configPath}";
            return false;
        }

        try
        {
            var loaded = ConfigLoader.LoadRequired<RadirecConfig>(configPath);
            ConfigPaths.RebaseAgainst(loaded, configPath);
            config = loaded;
            error = null;
            return true;
        }
        // JsonException / IOException に限らず、UnauthorizedAccessException(権限)・
        // InvalidOperationException(内容が null 等)含むあらゆる読込失敗を「クリーンな失敗」に正規化する。
        // reload はこの戻り値を ReloadResult.Failed に変換するため、ここで throw を漏らすと制御が
        // 応答を返せず誤って「応答なし」になる(ADR-0010/0012)。
        catch (Exception ex)
        {
            error = $"設定の読み込みに失敗しました: {ex.Message}";
            return false;
        }
    }
}
