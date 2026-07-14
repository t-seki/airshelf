using System.Text;
using System.Text.Json;
using Radirec.Hosting.Control;

namespace Radirec.Tests;

/// <summary>
/// 制御チャネル(ADR-0010)の wire フォーマットを**バイト単位で凍結**する golden テスト。
/// round-trip テスト(<see cref="ControlChannelTests"/>)は同じ options で serialize/deserialize
/// するため naming policy 変更を検出できない。ここは #5 の自前 DTO(型を共有しない)が依存する
/// 実際の JSON を固定し、camelCase・フィールド名・schemaVersion がドリフトしたら赤くする。
/// </summary>
public sealed class ControlContractTests
{
    [Fact]
    public void 要求JSONは凍結フォーマット_camelCase()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new ControlRequest { Command = ControlContract.Commands.Rescan }, ControlContract.Json);

        Assert.Equal(
            """{"schemaVersion":1,"command":"rescan"}""",
            Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void 状態応答JSONは凍結フォーマット_camelCase_CJK非escape()
    {
        var snapshot = new ServiceStatusSnapshot
        {
            Version = "1.2.3",
            StartedUtc = new DateTimeOffset(2026, 7, 14, 1, 2, 3, TimeSpan.Zero),
            RadioRoot = @"I:\radio",
            Watching = true,
            LastScanCompletedUtc = new DateTimeOffset(2026, 7, 14, 4, 5, 6, TimeSpan.Zero),
            LastScanTrigger = "手動",
            LastImported = 3,
            LastFailed = 1,
        };

        var json = JsonSerializer.Serialize(ControlResponse.Success(snapshot), ControlContract.Json);

        Assert.Equal(
            """
            {"schemaVersion":1,"ok":true,"result":{"version":"1.2.3","startedUtc":"2026-07-14T01:02:03+00:00","radioRoot":"I:\\radio","watching":true,"lastScanCompletedUtc":"2026-07-14T04:05:06+00:00","lastScanTrigger":"手動","lastImported":3,"lastFailed":1}}
            """,
            json);
    }

    [Fact]
    public void 失敗応答はerrorを含みresultを省く()
    {
        var json = JsonSerializer.Serialize(ControlResponse.Failure("未知のコマンド: bogus"), ControlContract.Json);

        Assert.Equal(
            """{"schemaVersion":1,"ok":false,"error":"未知のコマンド: bogus"}""",
            json);
    }

    [Fact]
    public void 他プロセスのcamelCase要求を受理できる_5送出側の向き()
    {
        // #5(型を共有しない別 exe)が手書き JSON で送ってくる想定。サービスが解釈できること。
        const string foreign = """{"schemaVersion":1,"command":"status"}""";

        var request = JsonSerializer.Deserialize<ControlRequest>(foreign, ControlContract.Json);

        Assert.NotNull(request);
        Assert.Equal(ControlContract.SchemaVersion, request!.SchemaVersion);
        Assert.Equal(ControlContract.Commands.Status, request.Command);
    }

    [Fact]
    public void 凍結した状態応答JSONを型付きで解析できる_5受け手の向き()
    {
        // #5(受け手)がサービスの応答を自前 DTO で読む想定を、共有型で近似検証する。
        const string frozen = """
            {"schemaVersion":1,"ok":true,"result":{"version":"9.9.9","startedUtc":"2026-07-14T00:00:00+00:00","radioRoot":"Z:\\radio","watching":false,"lastImported":0,"lastFailed":0}}
            """;

        var response = JsonSerializer.Deserialize<ControlResponse>(frozen, ControlContract.Json);

        Assert.NotNull(response);
        Assert.True(response!.Ok);
        var status = response.ResultAs<ServiceStatusSnapshot>();
        Assert.NotNull(status);
        Assert.Equal("9.9.9", status!.Version);
        Assert.Equal(@"Z:\radio", status.RadioRoot);
        Assert.False(status.Watching);
        Assert.Null(status.LastScanCompletedUtc); // 省略フィールドは null に戻る
    }
}
