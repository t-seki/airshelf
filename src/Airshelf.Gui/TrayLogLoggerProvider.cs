using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Airshelf.Gui;

/// <summary>
/// 取り込みワーカーの <see cref="ILogger"/> 出力を <see cref="TrayLog"/>
/// (<c>%LOCALAPPDATA%\RadirecData\gui.log</c>)へ橋渡しする最小プロバイダ(ADR-0012)。
/// ユーザーセッション常駐はコンソールも EventLog も持たないため、動作ログをファイルに残す。
/// Information 未満は捨てる(既定 Warning より詳しく、かつ冗長にしすぎない)。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrayLogLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new TrayLogger(categoryName);

    public void Dispose() { }

    private sealed class TrayLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var shortCategory = category[(category.LastIndexOf('.') + 1)..];
            var message = formatter(state, exception);
            if (exception is not null)
                message = $"{message} :: {exception}";
            var line = $"[{shortCategory}] {message}";

            switch (logLevel)
            {
                case LogLevel.Error:
                case LogLevel.Critical:
                    TrayLog.Error(line);
                    break;
                case LogLevel.Warning:
                    TrayLog.Warn(line);
                    break;
                default:
                    TrayLog.Info(line);
                    break;
            }
        }
    }
}
