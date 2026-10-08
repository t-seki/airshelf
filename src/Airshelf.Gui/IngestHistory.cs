using System.Runtime.Versioning;
using Airshelf.Gui.Core;
using Airshelf.Hosting;

namespace Airshelf.Gui;

/// <summary>
/// 管理画面の取込履歴(S4 / #66)。台帳を読むのは CLI の <c>airshelf history</c> と共有する
/// <see cref="IngestHistoryReader"/>(#182)で、ここは管理画面の行(<see cref="IngestHistoryRow"/>)へ整形するだけ。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class IngestHistory
{
    public static IReadOnlyList<IngestHistoryRow> Load(string ledgerPath) =>
        IngestHistoryReader.Load(ledgerPath).Select(IngestHistoryRow.From).ToList();
}
