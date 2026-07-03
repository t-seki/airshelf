using System.Text.Json;
using System.Text.Json.Serialization;

namespace Radirec.Hosting;

/// <summary>JSON 設定の読み込み(コメント・trailing comma 許容)。</summary>
public static class ConfigLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        // 未知プロパティ(タイポや旧スキーマの Keys 等)を黙って無視せず失敗させる。
        // サイレントに設定が失効するのを防ぐ。
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static T LoadRequired<T>(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(json, Options)
               ?? throw new InvalidOperationException($"設定を解析できません: {path}");
    }

    public static T LoadOptional<T>(string path) where T : new()
    {
        if (!File.Exists(path))
            return new T();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(json, Options) ?? new T();
    }
}
