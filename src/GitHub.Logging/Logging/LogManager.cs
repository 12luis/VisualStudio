using System;
using System.IO;
using GitHub.Info;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace GitHub.Logging
{
    public static class LogManager
    {
#if DEBUG
        private static LogEventLevel DefaultLoggingLevel = LogEventLevel.Debug;
#else
        private static LogEventLevel DefaultLoggingLevel = LogEventLevel.Information;
#endif

        private static LoggingLevelSwitch LoggingLevelSwitch = new LoggingLevelSwitch(DefaultLoggingLevel);

        static Logger CreateLogger()
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ApplicationInfo.ApplicationName,
                "extension.log");

            const string outputTemplate =
                "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u4} [{ThreadId:00}] {ShortSourceContext,-25} {Message:lj}{NewLine}{Exception}";

            return new LoggerConfiguration()
                .Enrich.WithThreadId()
                .MinimumLevel.ControlledBy(LoggingLevelSwitch)
                .WriteTo.File(logPath,
                    fileSizeLimitBytes: null,
                    outputTemplate: outputTemplate)
                .CreateLogger();
        }

        public static void EnableTraceLogging(bool enable)
        {
            var logEventLevel = enable ? LogEventLevel.Verbose : DefaultLoggingLevel;
            if(LoggingLevelSwitch.MinimumLevel != logEventLevel)
            { 
                ForContext(typeof(LogManager)).Information("Set Logging Level: {LogEventLevel}", logEventLevel);
                LoggingLevelSwitch.MinimumLevel = logEventLevel;
            }
        }

        static Lazy<Logger> Logger { get; } = new Lazy<Logger>(CreateLogger);

        //Violates CA1004 - Generic methods should provide type parameter
#pragma warning disable CA1004
        public static ILogger ForContext<T>() => ForContext(typeof(T));
#pragma warning restore CA1004

        public static ILogger ForContext(Type type) => Logger.Value.ForContext(type).ForContext("ShortSourceContext", type.Name);
    }
}