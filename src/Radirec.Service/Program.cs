using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Radirec.Hosting;
using Radirec.Service;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "Radirec");

// 設定: 第1引数 or 実行ファイル隣の radirec.json(サービスの既定作業ディレクトリは System32 のため
// 相対パスは実行ファイル基準で解決する)。
var configArg = args.FirstOrDefault(a => !a.StartsWith('-'));
var configPath = configArg ?? "radirec.json";
if (!Path.IsPathRooted(configPath))
    configPath = Path.Combine(AppContext.BaseDirectory, configPath);

var config = ConfigLoader.LoadRequired<RadirecConfig>(configPath);

builder.Services.AddSingleton(config);
builder.Services.AddHostedService<RadioMonitorWorker>();

// コンソール無し前提: Windows イベントログへ出力(対話実行時はコンソールにも出る)。
builder.Logging.AddEventLog(settings => settings.SourceName = "Radirec");

builder.Build().Run();
