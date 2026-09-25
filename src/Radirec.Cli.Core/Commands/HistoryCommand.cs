using System.ComponentModel;
using System.Globalization;
using Radirec.Cli.Core.Output;
using Radirec.Core.Ledger;
using Radirec.Hosting;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Radirec.Cli.Core.Commands;

/// <summary>
/// 取込履歴を台帳から読んで出す(#182・ADR-0020)。台帳は読み取り専用で開くので、常駐(トレイ)を経ず、動いていなくても使える。
/// </summary>
public sealed class HistoryCommand(IAnsiConsole console, ICliConfigSource configSource)
    : Command<HistoryCommand.Settings>
{
    public const string Name = "history";

    /// <summary><c>--limit</c> の既定。管理画面のホームの取込履歴と同じ件数(<c>HomeViewModel.RecentCount</c>)。</summary>
    public const int DefaultLimit = 5;

    public sealed class Settings : CliSettings
    {
        [CommandOption("--limit <N>")]
        [Description("新しい方から何件出すか。既定は 5(管理画面のホームと同じ)。0 は全件")]
        public int Limit { get; init; } = DefaultLimit;

        [CommandOption("--since <日時>")]
        [Description("取込日時がこれ以降のものだけ(例 2026-09-01 / 2026-09-01T12:00)。時差の無い値はローカル時刻として読む")]
        public string? Since { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (settings.Limit < 0)
            return CommandSupport.Fail(console, Name, settings.Json, ExitCodes.UsageOrConfig,
                $"--limit は 0 以上で指定してください({settings.Limit})");

        DateTimeOffset? since = null;
        if (settings.Since is { } raw)
        {
            if (!TryParseSince(raw, out var parsed))
                return CommandSupport.Fail(console, Name, settings.Json, ExitCodes.UsageOrConfig,
                    $"--since の日時を読めません({raw})。例 2026-09-01 / 2026-09-01T12:00");
            since = parsed;
        }

        var config = CommandSupport.LoadConfig(configSource, console, Name, settings.Json);
        if (config is null)
            return ExitCodes.UsageOrConfig;

        var result = Select(IngestHistoryReader.Load(config.LedgerPath), settings.Limit, since);
        if (settings.Json)
            CliJson.Write(console, CliJson.Result(Name, ExitCodes.Success, result));
        else
            Render(result);
        return ExitCodes.Success;
    }

    /// <summary>
    /// <c>--since</c> の値を読む。時差の無い値(<c>2026-09-01</c> / <c>2026-09-01T12:00</c>)はローカル時刻、時差つきはそのまま。
    /// </summary>
    public static bool TryParseSince(string value, out DateTimeOffset since) =>
        TryParseSince(value, TimeZoneInfo.Local, out since);

    /// <summary>
    /// <see cref="TryParseSince(string, out DateTimeOffset)"/> の、時差の無い値を読むタイムゾーンを渡せる形
    /// (テストで UTC 以外のゾーンを確かめるため。CI のローカルは UTC)。
    /// </summary>
    public static bool TryParseSince(string value, TimeZoneInfo zone, out DateTimeOffset since)
    {
        since = default;
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return false;

        // 時差を書いていなければ、その壁時計の時刻を zone の時刻として読み直す(TryParse は時差が無いとき
        // マシンのローカルの時差を付けるので、書いてあったかどうかは文字列で判断する)。
        since = HasOffset(value)
            ? parsed
            : new DateTimeOffset(parsed.DateTime, zone.GetUtcOffset(DateTime.SpecifyKind(parsed.DateTime, DateTimeKind.Unspecified)));
        return true;
    }

    // 末尾に Z か ±hh:mm / ±hhmm があるか(日付の「-」と取り違えないよう、時刻の後ろだけを見る)。
    private static bool HasOffset(string value)
    {
        var t = value.IndexOf('T');
        if (t < 0)
            t = value.IndexOf(' ');
        if (t < 0)
            return false; // 日付だけ
        var time = value[(t + 1)..];
        return time.EndsWith('Z') || time.EndsWith('z') || time.Contains('+') || time.Contains('-');
    }

    /// <summary>
    /// 新しい順の記録を、取込日時が <paramref name="since"/> 以降のものに絞り、<paramref name="limit"/> 件まで返す
    /// (0 は全件)。表示はしない(テストはここを確かめる)。
    /// </summary>
    public static HistoryJson Select(IReadOnlyList<LedgerEntry> entries, int limit, DateTimeOffset? since)
    {
        IEnumerable<LedgerEntry> selected = entries;
        if (since is { } s)
            selected = selected.Where(e => e.ImportedAtUtc >= s);
        if (limit > 0)
            selected = selected.Take(limit);
        return new HistoryJson(selected.Select(HistoryEntryJson.From).ToList());
    }

    // 管理画面の取込履歴と同じ列・同じ表記(放送は 2026-06-12 08:30 / (2)、ファイルは出力先のファイル名)。
    private void Render(HistoryJson result)
    {
        if (result.Entries.Count == 0)
        {
            console.MarkupLine("[grey]取込履歴はありません[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("番組");
        table.AddColumn("放送局");
        table.AddColumn("放送");
        table.AddColumn("取込日時");
        table.AddColumn("ファイル");

        foreach (var entry in result.Entries)
        {
            table.AddRow(
                Markup.Escape(entry.Program),
                Markup.Escape(entry.Station),
                Markup.Escape(BroadcastLabel.Format(entry.BroadcastDate, entry.BroadcastTime, entry.Part)),
                Markup.Escape(entry.ImportedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
                Markup.Escape(Path.GetFileName(entry.OutputPath)));
        }

        console.Write(table);
        console.MarkupLine($"新しい順に {result.Entries.Count} 件");
    }
}
