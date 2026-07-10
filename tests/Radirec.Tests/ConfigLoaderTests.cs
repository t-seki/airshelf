using Radirec.Hosting;

namespace Radirec.Tests;

public sealed class ConfigLoaderTests
{
    private static RadirecConfig LoadJson(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"radirec-cfg-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        try
        {
            return ConfigLoader.LoadRequired<RadirecConfig>(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void 通知キー不在なら既定で有効_既存configは更新後も光る()
    {
        // 既存デプロイの radirec.json は NotificationsEnabled キーを持たない。
        var config = LoadJson("""{ "RadioRoot": "I:\\radio\\tmp" }""");

        Assert.True(config.NotificationsEnabled);
    }

    [Fact]
    public void 通知キーfalseなら無効化できる()
    {
        var config = LoadJson("""{ "RadioRoot": "I:\\radio\\tmp", "NotificationsEnabled": false }""");

        Assert.False(config.NotificationsEnabled);
    }
}
