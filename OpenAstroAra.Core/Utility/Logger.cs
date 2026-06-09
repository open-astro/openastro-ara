#region "copyright"

/*
    Copyright � 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Enums;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.File;
using System;
using System.Globalization;
using System.IO;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenAstroAra.Core.Utility {

    public static class Logger {
        private static readonly LoggingLevelSwitch levelSwitch = new LoggingLevelSwitch { MinimumLevel = LogEventLevel.Information };

        static Logger() {
            var logDate = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var logDir = Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "Logs");
            var processId = Environment.ProcessId;
            var logFilePath = Path.Combine(logDir, $"{logDate}-{CoreUtil.Version}.{processId}-.log");

            if (!Directory.Exists(logDir)) {
                Directory.CreateDirectory(logDir);
            } else {
                CoreUtil.DirectoryCleanup(logDir, TimeSpan.FromDays(-90));
            }

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.ControlledBy(levelSwitch)
                .Enrich.With<LegacyLogLevelMappingEnricher>()
                .WriteTo.Console(
                    formatProvider: CultureInfo.InvariantCulture,
                    outputTemplate: "{Timestamp:yyyy-MM-ddTHH:mm:ss.ffff}|{LegacyLogLevel}|{Message:lj}{NewLine}{Exception}")
                .WriteTo.File(logFilePath,
                    rollOnFileSizeLimit: true,
                    rollingInterval: RollingInterval.Month,
                    formatProvider: CultureInfo.InvariantCulture,
                    outputTemplate: "{Timestamp:yyyy-MM-ddTHH:mm:ss.ffff}|{LegacyLogLevel}|{Message:lj}{NewLine}{Exception}",
                    shared: false,
                    buffered: false,
                    hooks: new HeaderWriter(GenerateHeader),
                    flushToDiskInterval: TimeSpan.FromSeconds(1),
                    retainedFileCountLimit: null)
                .CreateLogger();
        }

        private static string GenerateHeader() {
            /* Initial log of App Version, OS Info, Ascom Version, .NET Version */
            var sb = new StringBuilder();
            sb.AppendLine(PadBoth("", 70, '-'));
            sb.AppendLine(PadBoth(CoreUtil.Title, 70, '-'));
            sb.AppendLine(PadBoth(string.Format(CultureInfo.InvariantCulture, "Version {0}", CoreUtil.Version), 70, '-'));
            sb.AppendLine(PadBoth(DateTime.Now.ToString("s"), 70, '-'));
            sb.AppendLine(PadBoth("", 70, '-'));
            try {
                sb.AppendLine(PadBoth("{0}", 70, '-', RuntimeInformation.OSDescription));
                sb.AppendLine(PadBoth("OS Architecture {0}", 70, '-', RuntimeInformation.OSArchitecture.ToString()));
                sb.AppendLine(PadBoth("Process Architecture {0}", 70, '-', RuntimeInformation.ProcessArchitecture.ToString()));
                sb.AppendLine(PadBoth("{0}", 70, '-', RuntimeInformation.FrameworkDescription));
                sb.AppendLine(PadBoth("", 70, '-'));
                sb.AppendLine(PadBoth("Processor Count {0}", 70, '-', Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture)));
            } catch (PlatformNotSupportedException) {
                sb.AppendLine(PadBoth("Unable to determine OS information", 70, '-'));
            }

            try {
                sb.AppendLine(PadBoth("Total Physical Memory {0} GB", 70, '-', Math.Round(GetTotalPhysicalMemory() / 1024d / 1024d / 1024d, 2).ToString(CultureInfo.InvariantCulture)));
            } catch (InvalidOperationException) {
                sb.AppendLine(PadBoth("Unable to determine Physical Memory", 70, '-'));
            }

            sb.AppendLine(PadBoth("", 70, '-'));
            sb.Append("DATE|LEVEL|SOURCE|MEMBER|LINE|MESSAGE");
            return sb.ToString();
        }

        private static long GetTotalPhysicalMemory() =>
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

        private static string PadBoth(string msg, int length, char paddingChar, params string[] msgParams) {
            var source = string.Format(CultureInfo.InvariantCulture, msg, msgParams);
            int spaces = length - source.Length;
            int padLeft = spaces / 2 + source.Length;
            return source.PadLeft(padLeft, paddingChar).PadRight(length, paddingChar);
        }

        public static void SetLogLevel(LogLevel logLevel) {
            levelSwitch.MinimumLevel = logLevel switch {
                LogLevel.TRACE => LogEventLevel.Verbose,
                LogLevel.DEBUG => LogEventLevel.Debug,
                LogLevel.INFO => LogEventLevel.Information,
                LogLevel.WARNING => LogEventLevel.Warning,
                LogLevel.ERROR => LogEventLevel.Error,
                _ => LogEventLevel.Information,
            };
        }

        public static bool IsEnabled(LogLevel level) {
            var serilogLevel = level switch {
                LogLevel.TRACE => LogEventLevel.Verbose,
                LogLevel.DEBUG => LogEventLevel.Debug,
                LogLevel.INFO => LogEventLevel.Information,
                LogLevel.WARNING => LogEventLevel.Warning,
                LogLevel.ERROR => LogEventLevel.Error,
                _ => LogEventLevel.Information,
            };

            return Log.IsEnabled(serilogLevel);
        }

        public static void CloseAndFlush() {
            Log.CloseAndFlush();
        }

        private const string ErrorTemplate = "{source}|{member}|{line}";
        private const string MessageTemplate = "{source}|{member}|{line}|{message}";

        public static void Error(
                Exception ex,
                [CallerMemberName] string memberName = "",
                [CallerFilePath] string sourceFilePath = "",
                [CallerLineNumber] int lineNumber = 0) {
            Log.Error(ex, ErrorTemplate, Path.GetFileName(sourceFilePath), memberName, lineNumber);
        }

        public static void Error(
                string customMsg,
                Exception ex,
                [CallerMemberName] string memberName = "",
                [CallerFilePath] string sourceFilePath = "",
                [CallerLineNumber] int lineNumber = 0) {
            Log.Error(ex, MessageTemplate, Path.GetFileName(sourceFilePath), memberName, lineNumber, customMsg);
        }

        public static void Error(string message,
                [CallerMemberName] string memberName = "",
                [CallerFilePath] string sourceFilePath = "",
                [CallerLineNumber] int lineNumber = 0) {
            Log.Error(MessageTemplate, Path.GetFileName(sourceFilePath), memberName, lineNumber, message);
        }

        public static void Warning(string message,
                [CallerMemberName] string memberName = "",
                [CallerFilePath] string sourceFilePath = "",
                [CallerLineNumber] int lineNumber = 0) {
            Log.Warning(MessageTemplate, Path.GetFileName(sourceFilePath), memberName, lineNumber, message);
        }

        public static void Info(string message,
                [CallerMemberName] string memberName = "",
                [CallerFilePath] string sourceFilePath = "",
                [CallerLineNumber] int lineNumber = 0) {
            Log.Information(MessageTemplate, Path.GetFileName(sourceFilePath), memberName, lineNumber, message);
        }

        public static void Debug(string message,
                [CallerMemberName] string memberName = "",
                [CallerFilePath] string sourceFilePath = "",
                [CallerLineNumber] int lineNumber = 0) {
            Log.Debug(MessageTemplate, Path.GetFileName(sourceFilePath), memberName, lineNumber, message);
        }

        public static void Trace(string message,
                [CallerMemberName] string memberName = "",
                [CallerFilePath] string sourceFilePath = "",
                [CallerLineNumber] int lineNumber = 0) {
            Log.Verbose(MessageTemplate, Path.GetFileName(sourceFilePath), memberName, lineNumber, message);
        }

        private sealed class HeaderWriter : FileLifecycleHooks {

            // Factory method to generate the file header
            private readonly Func<string> headerFactory;

            public HeaderWriter(Func<string> headerFactory) {
                this.headerFactory = headerFactory;
            }

            public override Stream OnFileOpened(Stream underlyingStream, Encoding encoding) {
                using (var writer = new StreamWriter(underlyingStream, encoding, 1024, true)) {
                    var header = this.headerFactory();

                    writer.WriteLine(header);
                    writer.Flush();
                    underlyingStream.Flush();
                }

                return base.OnFileOpened(underlyingStream, encoding);
            }
        }

        private sealed class LegacyLogLevelMappingEnricher : ILogEventEnricher {
            private static readonly string LEGACYLOGLEVELPROPERTY = "LegacyLogLevel";
            private static readonly string TRACE = LogLevel.TRACE.ToString();
            private static readonly string DEBUG = LogLevel.DEBUG.ToString();
            private static readonly string INFO = LogLevel.INFO.ToString();
            private static readonly string WARNING = LogLevel.WARNING.ToString();
            private static readonly string ERROR = LogLevel.ERROR.ToString();
            private static readonly string FATAL = "FATAL";
            private static readonly string UNKNOWN = "UNKNOWN";

            public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory) {

                var legacyLogLevel = logEvent.Level switch {
                    LogEventLevel.Verbose => TRACE,
                    LogEventLevel.Debug => DEBUG,
                    LogEventLevel.Information => INFO,
                    LogEventLevel.Warning => WARNING,
                    LogEventLevel.Error => ERROR,
                    LogEventLevel.Fatal => FATAL,
                    _ => UNKNOWN
                };
                logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(LEGACYLOGLEVELPROPERTY, legacyLogLevel));
            }
        }
    }
}