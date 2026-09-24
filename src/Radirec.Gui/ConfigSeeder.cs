using System.IO;
using System.Runtime.Versioning;
using Radirec.Hosting;

namespace Radirec.Gui;

/// <summary>
/// 初回起動時に、データルート(<c>%LOCALAPPDATA%\RadirecData\</c>)へ設定ファイルを埋め込み sample から
/// 生成する(既存は温存)。旧 <c>install.ps1</c> の seeding を per-user 移行(ADR-0013 / S5)でアプリへ移設。
/// トレイの「設定ファイルを開く」(#143)も、開くファイルが無ければ同じ sample から作る(<see cref="SeedFileIfAbsent"/>)。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ConfigSeeder
{
    // 設定ファイルの種類 → 埋め込みリソース名(csproj の LogicalName と一致させる)・データルートでの既定のファイル名。
    private static readonly (ConfigFile File, string Resource, string DefaultName)[] Map =
    [
        (ConfigFile.Radirec, "Radirec.Gui.Samples.radirec.sample.json", "radirec.json"),
        (ConfigFile.Stations, "Radirec.Gui.Samples.stations.sample.json", "stations.json"),
        (ConfigFile.Programs, "Radirec.Gui.Samples.programs.sample.json", "programs.json"),
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

        foreach (var (file, _, defaultName) in Map)
            SeedFileIfAbsent(file, Path.Combine(dataRoot, defaultName));
    }

    /// <summary>
    /// <paramref name="path"/> が無ければ <paramref name="file"/> の sample から作る。既存ファイルは上書きしない。
    /// <c>StationsFile</c> / <c>ProgramsFile</c> で既定と違う場所を指しているときもその場所に作る(親フォルダも作る)。
    /// </summary>
    /// <returns>ファイルがある(元からある or 作れた)なら true。作れなかったら false(理由は gui.log)。</returns>
    public static bool SeedFileIfAbsent(ConfigFile file, string path)
    {
        if (File.Exists(path))
            return true;

        var (_, resource, _) = Map.Single(m => m.File == file);
        var name = Path.GetFileName(path);
        try
        {
            using var stream = typeof(ConfigSeeder).Assembly.GetManifestResourceStream(resource);
            if (stream is null)
            {
                TrayLog.Warn($"設定 sample が埋め込みに見つかりません: {resource}");
                return false;
            }

            if (Path.GetDirectoryName(path) is { Length: > 0 } dir)
                Directory.CreateDirectory(dir);

            // CreateNew: 確かめてから作るまでの間に利用者が作ったファイルを切り詰めない(File.Create は上書きする)。
            using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            stream.CopyTo(target);
            TrayLog.Info($"{name} を sample から生成しました({path})");
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            return true; // 確かめた直後に誰かが作った。上書きせずそのまま使う
        }
        catch (Exception ex)
        {
            TrayLog.Warn($"{name} の生成に失敗しました({path}): {ex.Message}");
            return false;
        }
    }
}
