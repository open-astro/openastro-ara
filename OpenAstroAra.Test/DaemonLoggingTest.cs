using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using OpenAstroAra.Server;
using OpenAstroAra.Server.Services;
using CoreLogger = OpenAstroAra.Core.Utility.Logger;


namespace OpenAstroAra.Test;

/// <summary>
/// #1591: the Core logger's static constructor replaced the daemon's Serilog logger on first use,
/// so Polar Align and every solve went to NINA/Logs, which the bug report never bundled.
/// </summary>
[TestFixture]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1848", Justification = "One test line")]
public class DaemonLoggingTest {
    [Test]
    public void Core_and_host_loggers_both_reach_the_daemon_log() {
        var dir = Path.Combine(Path.GetTempPath(), "ara-logging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var previous = Environment.GetEnvironmentVariable("OPENASTROARA_PROFILE_DIR");
        Environment.SetEnvironmentVariable("OPENASTROARA_PROFILE_DIR", dir);
        try {
            var app = Program.BuildApp(Array.Empty<string>());
            CoreLogger.Info("core-marker");
            app.Services.GetRequiredService<ILogger<PolarAlignService>>()
                .Log(LogLevel.Information, "host-marker");
            CoreLogger.CloseAndFlush();

            var text = string.Concat(Directory.GetFiles(Path.Combine(dir, "logs"), "openastroara-*.log")
                .Select(File.ReadAllText));
            Assert.That(text, Does.Contain("core-marker").And.Contain("host-marker"));
        } finally {
            Environment.SetEnvironmentVariable("OPENASTROARA_PROFILE_DIR", previous);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
