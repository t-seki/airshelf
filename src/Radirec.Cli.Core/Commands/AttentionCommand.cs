using System.Globalization;
using Radirec.Cli.Core.Output;
using Radirec.Core.Attention;
using Radirec.Hosting;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Radirec.Cli.Core.Commands;

/// <summary>
/// 要対応(ADR-0018)を出す(#183・ADR-0020)。計算は管理画面のホームと同じ <see cref="AttentionLoader"/> を呼ぶだけで、
/// 台帳(読み取り専用)とディスクの programs.json / stations.json を読む。常駐(トレイ)を経ず、動いていなくても使える。
/// 要対応を解決する操作(統合する・分けておく)は無い(programs.json を直してから <c>reapply</c> を使う)。
/// </summary>
public sealed class AttentionCommand(IAnsiConsole console, ICliConfigSource configSource)
    : Command<CliSettings>
{
    public const string Name = "attention";

    protected override int Execute(CommandContext context, CliSettings settings, CancellationToken cancellationToken)
    {
        var config = CommandSupport.LoadConfig(configSource, console, Name, settings.Json);
        if (config is null)
            return ExitCodes.UsageOrConfig;

        AttentionItems items;
        try
        {
            items = Load(config);
        }
        catch (Exception ex) when (ex is not (IOException or UnauthorizedAccessException))
        {
            // 台帳か programs.json / stations.json を読めない(壊れた JSON など)。設定を直す話なので 1。ファイルに届かない
            // (IOException 等)は外へ出して、ほかのコマンドと同じく 5 にする(RadirecCli.HandleException)。
            return CommandSupport.Fail(console, Name, settings.Json, ExitCodes.UsageOrConfig, $"要対応を出せませんでした: {ex.Message}");
        }

        var result = AttentionJson.From(items);
        if (settings.Json)
            CliJson.Write(console, CliJson.Result(Name, ExitCodes.Success, result));
        else
            Render(items, result.Count);
        return ExitCodes.Success;
    }

    /// <summary>
    /// 読み込んだ設定(相対パスは rebase 済み)で要対応を計算する。管理画面のホームと同じ計算(<see cref="AttentionLoader.Load"/>)。
    /// </summary>
    public static AttentionItems Load(RadirecConfig config) =>
        AttentionLoader.Load(config.LedgerPath, config.StationsFile, config.ProgramsFile, config.KnownRecordingKinds);

    // 管理画面のホームの要対応と同じ見出し・同じ行の表記(docs/mockups/management-window.md の W-120〜W-135)。
    private void Render(AttentionItems items, int count)
    {
        if (count == 0)
        {
            console.MarkupLine("[grey]要対応はありません[/]");
            return;
        }

        console.MarkupLineInterpolated($"[yellow]要対応 {count} 件[/]");

        if (items.UnintegratedSiblings.Count > 0)
        {
            console.WriteLine();
            console.MarkupLine("[bold]統合設定の無い兄弟フォルダ[/]");
            foreach (var sibling in items.UnintegratedSiblings)
            {
                var folders = string.Join(" / ", sibling.Group.Folders.Select(f =>
                    sibling.FoldersWithoutSetting.Contains(f) ? $"{f}(設定なし)" : f));
                console.MarkupLineInterpolated($"  {sibling.Group.Station}: {folders}");
                if (sibling.IsKeptApartGrown)
                    console.MarkupLineInterpolated($"    [grey]分けておく組に、新しいフォルダ{Quoted(sibling.FoldersNewToKeptApart)}が加わりました[/]");
                else if (sibling.MergedAlbum is { } album)
                    console.MarkupLineInterpolated($"    [grey]統合番組『{album}』に入っていないフォルダ{Quoted(sibling.FoldersWithoutSetting)}があります[/]");
            }
        }

        if (items.UnresolvedFailures.Count > 0)
        {
            console.WriteLine();
            console.MarkupLine("[bold]解決していない取込失敗[/] [grey](録音ファイルを直すか置き直して、取り込みに成功すると消えます)[/]");
            foreach (var failure in items.UnresolvedFailures)
            {
                console.MarkupLineInterpolated($"  {FileNameOf(failure.SourcePath)}");
                console.MarkupLineInterpolated(
                    $"    {(string.IsNullOrWhiteSpace(failure.Reason) ? "(理由の記録なし)" : failure.Reason)}");
                if (failure.FailedAtUtc is { } at)
                    console.MarkupLineInterpolated(
                        $"    [grey]失敗: {at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}[/]");
                console.MarkupLineInterpolated($"    [grey]{failure.SourcePath}[/]");
            }
        }

        if (items.SettingsDrift.Count > 0)
        {
            console.WriteLine();
            console.MarkupLine("[bold]再適用が要る番組[/] [grey](radirec reapply --dry-run で確かめる)[/]");
            foreach (var drift in items.SettingsDrift)
            {
                console.MarkupLineInterpolated($"  {DriftLine(drift)}");
            }
        }
    }

    /// <summary>再適用が要る番組の 1 行(管理画面の W-135 と同じ)。パートだけが違う回は 2 つ目の形。</summary>
    public static string DriftLine(SettingsDriftGroup group)
        => group.ToAlbum == group.FromAlbum
            ? $"{group.Station}「{group.FromAlbum}」 パートの番号が変わる {group.Count} 回"
            : $"{group.Station}「{group.FromAlbum}」→「{group.ToAlbum}」 {group.Count} 回";

    private static string Quoted(IReadOnlyList<string> folders) => string.Concat(folders.Select(f => $"『{f}』"));

    // 台帳のパスは Windows の形(\ 区切り)。WSL のテストでも同じに切れるよう、両方の区切りで切る(管理画面と同じ)。
    private static string FileNameOf(string path)
    {
        var cut = path.LastIndexOfAny(['\\', '/']);
        return cut < 0 ? path : path[(cut + 1)..];
    }
}
