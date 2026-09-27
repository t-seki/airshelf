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
            Render(console, items);
        return ExitCodes.Success;
    }

    /// <summary>
    /// 読み込んだ設定(相対パスは rebase 済み)で要対応を計算する。管理画面のホームと同じ計算(<see cref="AttentionLoader.Load"/>)。
    /// </summary>
    public static AttentionItems Load(RadirecConfig config) =>
        AttentionLoader.Load(config.LedgerPath, config.StationsFile, config.ProgramsFile, config.KnownRecordingKinds);

    /// <summary>
    /// 表の形で出す。管理画面のホームの要対応と同じ見出し・同じ行の表記(docs/mockups/management-window.md の W-120〜W-135)。
    /// フィクスチャで作りにくい行(理由・日時の記録が無い失敗など)も、テストが要対応を直接渡して確かめられるように公開する。
    /// </summary>
    public static void Render(IAnsiConsole console, AttentionItems items)
    {
        var count = AttentionJson.From(items).Count;
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
                // ファイル名・理由・取込元は空白の無い長い塊になりやすいので、自分で切る(#218)。
                var width = console.Profile.Width;
                foreach (var line in FileNameLines(failure.SourcePath, width))
                    console.MarkupLineInterpolated($"{line}");
                foreach (var line in ReasonLines(failure.Reason, width))
                    console.MarkupLineInterpolated($"{line}");
                if (failure.FailedAtUtc is { } at)
                    console.MarkupLineInterpolated(
                        $"{FailureBodyIndent}[grey]失敗: {at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}[/]");
                foreach (var line in SourceLines(failure.SourcePath, width))
                    console.MarkupLineInterpolated($"[grey]{line}[/]");
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

    // 取込失敗の 1 件の行の下げ(#218)。塊の見出し(ファイル名)は 2 桁、中身(理由・日時・取込元)は 4 桁。
    // 折り返した続きの行は、どちらとも違う 6 桁に下げる(続きの行が次の失敗の見出しや次の中身の行に見えないように)。
    // 取込元の続きの行は、ラベルの後のパスの頭(12 桁)にそろえる。
    private const string FailureHeadIndent = "  ";
    private const string FailureBodyIndent = "    ";
    private const string ContinuationIndent = "      ";

    /// <summary>取込失敗のファイル名(塊の見出し)の行。長ければ折り返し、続きの行は 6 桁下げる(#218)。</summary>
    public static IReadOnlyList<string> FileNameLines(string sourcePath, int width) =>
        Wrap(FailureHeadIndent, FileNameOf(sourcePath), width, ContinuationIndent);

    /// <summary>取込失敗の理由の行。理由にもファイル名などの空白の無い長い塊が入るので、同じく折り返す(#218)。</summary>
    public static IReadOnlyList<string> ReasonLines(string? reason, int width) =>
        Wrap(FailureBodyIndent, string.IsNullOrWhiteSpace(reason) ? "(理由の記録なし)" : reason, width, ContinuationIndent);

    /// <summary>
    /// 取込失敗の取込元のパスを、<paramref name="width"/> の幅に収まる行に切る。1 行目は <c>取込元: </c> の後にパスを続け、
    /// 続きの行はパスの頭にそろえて下げる(#218)。
    /// </summary>
    public static IReadOnlyList<string> SourceLines(string path, int width)
    {
        const string lead = FailureBodyIndent + "取込元: ";
        return Wrap(lead, path, width, new string(' ', lead.GetCellWidth()));
    }

    /// <summary>
    /// <paramref name="text"/> を <paramref name="lead"/> の後に続け、<paramref name="width"/> の幅に収まる行に切る。続きの行は
    /// <paramref name="continuation"/> の後に続ける。
    /// <para>
    /// パスやファイル名には空白が無いことが多く、Spectre.Console に任せると、下げた分(とラベル)だけの行を作ってから、
    /// 本体を行頭から始めて折り返す(失敗の塊から離れて見える。#218)。そこで語の切れ目ではなく文字の幅で自分で切る。
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> Wrap(string lead, string text, int width, string continuation)
    {
        var lines = new List<string>();
        var chunk = new System.Text.StringBuilder();
        var chunkWidth = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = (string)elements.Current;
            var cells = element.GetCellWidth();
            if (chunkWidth + cells > Room(lines.Count == 0 ? lead : continuation) && chunk.Length > 0)
            {
                lines.Add((lines.Count == 0 ? lead : continuation) + chunk);
                chunk.Clear();
                chunkWidth = 0;
            }
            chunk.Append(element);
            chunkWidth += cells;
        }
        lines.Add((lines.Count == 0 ? lead : continuation) + chunk);
        return lines;

        // 幅が極端に狭くても(幅が取れない出力先でも)、1 行に最低これだけは載せる(無限に行を増やさない)。
        int Room(string prefix) => Math.Max(width - prefix.GetCellWidth(), 10);
    }

    private static string Quoted(IReadOnlyList<string> folders) => string.Concat(folders.Select(f => $"『{f}』"));

    // 台帳のパスは Windows の形(\ 区切り)。WSL のテストでも同じに切れるよう、両方の区切りで切る(管理画面と同じ)。
    private static string FileNameOf(string path)
    {
        var cut = path.LastIndexOfAny(['\\', '/']);
        return cut < 0 ? path : path[(cut + 1)..];
    }
}
