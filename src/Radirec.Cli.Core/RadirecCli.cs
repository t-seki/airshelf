using Radirec.Cli.Core.Commands;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Radirec.Cli.Core;

/// <summary>
/// radirec.exe の <see cref="CommandApp"/> を組み立てて走らせる。exe(Radirec.Cli)の Program.cs はこれを呼ぶだけ。
/// テストは <paramref name="console"/> と <paramref name="configSource"/> を差し替えて、解析・終了コード・JSON を確かめる。
/// </summary>
public static class RadirecCli
{
    /// <param name="args">コマンドライン引数。</param>
    /// <param name="version">表示する版(exe のアセンブリから <see cref="VersionInfo.Of"/> で得る)。</param>
    /// <param name="console">出力先。null なら標準出力(<see cref="AnsiConsole.Console"/>)。</param>
    /// <param name="configSource">設定を読む口。null なら既定の radirec.json(<see cref="DefaultCliConfigSource"/>)。</param>
    public static Task<int> RunAsync(
        string[] args, string version, IAnsiConsole? console = null, ICliConfigSource? configSource = null)
    {
        var app = new CommandApp();
        app.Configure(config => Configure(
            config, version, console ?? AnsiConsole.Console, configSource ?? new DefaultCliConfigSource(),
            json: args.Contains("--json", StringComparer.Ordinal), command: CommandOf(args)));
        return app.RunAsync(args);
    }

    /// <summary>
    /// 引数の先頭の位置引数が既知のコマンドならその名前、そうでなければ null(未知のコマンド・コマンド無し)。例外を JSON に
    /// するときの <c>command</c> に使う。コマンドが決まっていれば、解析の誤りでも実行中の例外でもその名前を入れる。
    /// </summary>
    public static string? CommandOf(string[] args)
    {
        var first = args.FirstOrDefault(a => !a.StartsWith('-'));
        return first is IngestCommand.Name or ReapplyCommand.Name or RefreshCommand.Name ? first : null;
    }

    private static void Configure(
        IConfigurator config, string version, IAnsiConsole console, ICliConfigSource configSource, bool json, string? command)
    {
        config.SetApplicationName("radirec");
        config.SetApplicationVersion(version);
        config.ConfigureConsole(console);
        config.Settings.Registrar.RegisterInstance(console);
        config.Settings.Registrar.RegisterInstance(configSource);

        // 知らないオプションはエラーにする(既定では黙って読み捨てる)。削った --config / --root を付けた古いスクリプトが、
        // 気づかれないまま既定の設定・台帳で動いてしまわないように(ADR-0020)。
        config.UseStrictParsing();

        // 解析の誤り(未知のオプション・コマンド)は Spectre 既定の -1(= 255)ではなく、使い方の誤り(1)で終える。
        config.SetExceptionHandler((ex, _) => HandleException(console, ex, json, command));

        config.AddCommand<IngestCommand>(IngestCommand.Name)
            .WithDescription("らじれこ録音を正規化して取り込む(--dry-run で予定表示)")
            .WithExample(IngestCommand.Name, "--dry-run");
        config.AddCommand<ReapplyCommand>(ReapplyCommand.Name)
            .WithDescription("今の番組の設定(統合・正規名)を取り込み済みの回へ再適用する(--dry-run で予定表示)")
            .WithExample(ReapplyCommand.Name, "--dry-run");
        config.AddCommand<RefreshCommand>(RefreshCommand.Name)
            .WithDescription("Jellyfin のライブラリ更新を手動で起動する")
            .WithExample(RefreshCommand.Name);
        // 常駐(service)と制御 verb(rescan/status/reload)は撤去した。常駐はトレイ(Radirec.Gui)が
        // 担い、制御はトレイメニューから in-process で行う(ADR-0012)。radirec.exe は dev/batch 用の
        // 単発 CLI(ingest / reapply / refresh)に縮小。一回きりの移行のための ingest --root と organize-source、
        // 設定ファイルを指す --config は削った(ADR-0020)。設定は常にトレイと同じ既定の radirec.json を使う。
    }

    /// <summary>
    /// コマンドの外へ出た例外を終了コードにする。解析の誤りは 1。ファイル・ネットワークの失敗(NAS に届かない等)は
    /// 外部との接続の失敗として 5。それ以外の想定外の例外も 1 にする(ADR-0020 に内部エラーの番号は無い)。
    /// <paramref name="command"/> は引数から決まったコマンド名(決まらなければ null)。
    /// </summary>
    private static int HandleException(IAnsiConsole console, Exception ex, bool json, string? command)
    {
        var exitCode = ex is IOException or UnauthorizedAccessException or HttpRequestException
            ? ExitCodes.ExternalFailure
            : ExitCodes.UsageOrConfig;

        if (json)
        {
            CliJson.Write(console, CliJson.Failure(command, exitCode, ex.Message));
            return exitCode;
        }

        if (ex is CommandAppException { Pretty: { } pretty })
            console.Write(pretty);
        else if (ex is CommandAppException)
            console.MarkupLineInterpolated($"[red]Error:[/] {ex.Message}");
        else
            console.WriteException(ex);
        return exitCode;
    }
}
