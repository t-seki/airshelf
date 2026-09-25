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
    public void 任意の設定ファイルが壊れていれば_ファイル名を付けて投げる()
    {
        // 取り込みを起動できなかった(W-104)ときに、画面でどのファイルが原因か分かるように(#168)。
        var dir = Path.Combine(Path.GetTempPath(), $"radirec-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "programs.json");
        File.WriteAllText(path, """{ "Programs": [ }""");
        try
        {
            var ex = Assert.Throws<System.Text.Json.JsonException>(
                () => ConfigLoader.LoadOptional<Radirec.Core.Configuration.ProgramMasterConfig>(path));

            Assert.StartsWith("programs.json を読めません(JSON が壊れています): ", ex.Message);
            Assert.IsAssignableFrom<System.Text.Json.JsonException>(ex.InnerException);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 通知キーfalseなら無効化できる()
    {
        var config = LoadJson("""{ "RadioRoot": "I:\\radio\\tmp", "NotificationsEnabled": false }""");

        Assert.False(config.NotificationsEnabled);
    }
}
