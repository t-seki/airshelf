using Radirec.Gui.Core;
using Radirec.Hosting;

namespace Radirec.Tests;

/// <summary>
/// 受け手(Gui.Core)の EventLog 契約値が発行側(Hosting.RadirecEventLog)と一致することを保証する。
/// 層構成上 GUI は Hosting を参照しないため値を複製しているが、ここでドリフトを検出する(ADR-0009)。
/// どちらかを変えたらこのテストが赤くなる。
/// </summary>
public sealed class NotificationContractTests
{
    [Fact]
    public void ソース名が発行側と一致()
        => Assert.Equal(RadirecEventLog.SourceName, NotificationContract.SourceName);

    [Fact]
    public void EventIDが発行側と一致()
        => Assert.Equal(RadirecEventLog.IngestSummaryEventId, NotificationContract.IngestSummaryEventId);

    [Fact]
    public void schemaVersionが発行側と一致()
        => Assert.Equal(RadirecEventLog.SummarySchemaVersion, NotificationContract.SupportedSchemaVersion);
}
