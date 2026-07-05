using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
using Radirec.Hosting;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Radirec.Cli.Commands;

/// <summary>
/// Windows サービスとして常駐し、録音を検知して自動取り込みする(M3。ADR-0003 / ADR-0005)。
/// SCM から <c>radirec.exe service</c> として起動され、<see cref="RadioMonitorWorker"/> が監視・取込を担う。
/// </summary>
public sealed class ServiceCommand : AsyncCommand<ServiceCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-c|--config <PATH>")]
        [Description(@"設定ファイル(radirec.json)のパス。省略時は %ProgramData%\radirec\radirec.json")]
        public string? Config { get; init; }
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var configPath = ConfigPaths.Resolve(settings.Config);
        if (!File.Exists(configPath))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]設定ファイルが見つかりません:[/] {configPath}");
            return 1;
        }

        var config = ConfigLoader.LoadRequired<RadirecConfig>(configPath);
        // 設定内の相対パス(台帳・マスタ)を設定ファイルのディレクトリ基準へ寄せる(ADR-0006)。
        // サービスの既定 CWD は System32 のため、CWD 基準では相対指定が破綻する。
        ConfigPaths.RebaseAgainst(config, configPath);

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddWindowsService(options => options.ServiceName = "Radirec");
        builder.Services.AddSingleton(config);
        builder.Services.AddHostedService<RadioMonitorWorker>();

        // コンソール無し前提: Windows イベントログへ出力(対話実行時はコンソールにも出る)。
        builder.Logging.AddEventLog(eventLog => eventLog.SourceName = "Radirec");
        // EventLog プロバイダの既定は Warning 以上。サービスの動作(取込件数等)を Information で残す。
        builder.Logging.AddFilter<EventLogLoggerProvider>(null, LogLevel.Information);

        await builder.Build().RunAsync(cancellationToken);
        return 0;
    }
}
