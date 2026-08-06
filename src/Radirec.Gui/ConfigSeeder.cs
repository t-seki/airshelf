using System.IO;
using System.Runtime.Versioning;

namespace Radirec.Gui;

/// <summary>
/// 初回起動時に、データルート(<c>%LOCALAPPDATA%\RadirecData\</c>)へ設定ファイルを埋め込み sample から
/// 生成する(既存は温存)。旧 <c>install.ps1</c> の seeding を per-user 移行(ADR-0013 / S5)でアプリへ移設。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ConfigSeeder
{
    // 埋め込みリソース名 → 生成先ファイル名(csproj の LogicalName と一致させる)。
    private static readonly (string Resource, string Target)[] Map =
    [
        ("Radirec.Gui.Samples.radirec.sample.json", "radirec.json"),
        ("Radirec.Gui.Samples.stations.sample.json", "stations.json"),
        ("Radirec.Gui.Samples.programs.sample.json", "programs.json"),
    ];

    /// <summary>DataRoot 配下に各設定が無ければ sample から生成する。既存ファイルは上書きしない。</summary>
    public static void SeedIfAbsent(string dataRoot)
    {
        try
        {
            Directory.CreateDirectory(dataRoot);
        }
        catch (Exception ex)
        {
            TrayLog.Warn($"データディレクトリを作成できませんでした({dataRoot}): {ex.Message}");
            return;
        }

        foreach (var (resource, target) in Map)
        {
            var path = Path.Combine(dataRoot, target);
            if (File.Exists(path))
                continue;

            try
            {
                using var stream = typeof(ConfigSeeder).Assembly.GetManifestResourceStream(resource);
                if (stream is null)
                {
                    TrayLog.Warn($"設定 sample が埋め込みに見つかりません: {resource}");
                    continue;
                }

                using var file = File.Create(path);
                stream.CopyTo(file);
                TrayLog.Info($"{target} を sample から初回生成しました");
            }
            catch (Exception ex)
            {
                TrayLog.Warn($"{target} の初回生成に失敗しました: {ex.Message}");
            }
        }
    }
}
