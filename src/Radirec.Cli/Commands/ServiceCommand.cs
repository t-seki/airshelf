using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
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
        // 位置引数でも設定パスを受ける。旧 Radirec.Service.exe は config を位置引数で受けており、
        // 既存の登録形式 `"...\radirec.exe" service "C:\...\radirec.json"` を黙殺しないため。
        [CommandArgument(0, "[CONFIG]")]
        [Description("設定ファイルのパス(位置引数。--config でも指定可)")]
        public string? ConfigArgument { get; init; }

        [CommandOption("-c|--config <PATH>")]
        [Description(@"設定ファイル(radirec.json)のパス。省略時は %ProgramData%\radirec\radirec.json")]
        public string? Config { get; init; }

        /// <summary>--config を優先し、無ければ位置引数を使う。</summary>
        public string? EffectiveConfig => Config ?? ConfigArgument;
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        // 設定の発見・読み込み・相対パス rebase(ADR-0006)は共通ヘルパに集約。
        // サービスの既定 CWD は System32 のため、相対指定の解決基準を誤らないことが要。
        var config = ConfigLoading.Load(settings.EffectiveConfig);
        if (config is null)
            return 1;

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
