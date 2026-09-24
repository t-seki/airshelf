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

    /// <summary>
    /// 読み込み済みの中身を、ファイルから読むときと同じ規則で解析する(programs.json への追記の前後の確かめ。#144)。
    /// </summary>
    /// <exception cref="JsonException">JSON として読めない・未知のプロパティがある。</exception>
    public static T Parse<T>(string json) where T : new()
        => JsonSerializer.Deserialize<T>(json, Options) ?? new T();

    public static T LoadOptional<T>(string path) where T : new()
    {
        if (!File.Exists(path))
            return new T();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(json, Options) ?? new T();
    }
}
