using Microsoft.Extensions.Logging;
using NUnit.Framework;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test;

[TestFixture]
public class SystemctlGuiderProcessSupervisorTest {
    private sealed class TestLogger : ILogger<SystemctlGuiderProcessSupervisor> {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    [TestCase("start")]
    [TestCase("restart")]
    public async Task Controls_canonical_unit_without_interactive_authentication(string verb) {
        var logger = new TestLogger();
        var supervisor = new SystemctlGuiderProcessSupervisor(logger) {
            CommandRunner = (start, ct) => {
                Assert.That(start.FileName, Is.EqualTo("systemctl"));
                Assert.That(start.ArgumentList, Is.EqualTo(new[] { "--no-ask-password", verb, "openastro-guider" }));
                Assert.That(start.RedirectStandardError && start.RedirectStandardOutput, Is.True);
                Assert.That(ct.CanBeCanceled, Is.True);
                return Task.FromResult((0, "", ""));
            },
        };
        await supervisor.RequestVerbAsync(verb);
        Assert.That(logger.Entries.Count, Is.EqualTo(1));
        Assert.That(logger.Entries[0].Level, Is.EqualTo(LogLevel.Information));
    }

    [Test]
    public async Task Failed_start_records_exit_code_and_error_not_success() {
        var logger = new TestLogger();
        var supervisor = new SystemctlGuiderProcessSupervisor(logger) {
            CommandRunner = (_, _) => Task.FromResult((1, "", "Access denied")),
        };
        await supervisor.RequestVerbAsync("start");
        Assert.That(logger.Entries.Count, Is.EqualTo(1));
        Assert.That(logger.Entries[0].Level, Is.EqualTo(LogLevel.Warning));
        Assert.That(logger.Entries[0].Message, Does.Contain("exit 1").And.Contain("Access denied"));
    }

    [Test]
    public async Task Hung_command_is_cancelled_and_logged() {
        var logger = new TestLogger();
        var supervisor = new SystemctlGuiderProcessSupervisor(logger) {
            CommandTimeout = TimeSpan.FromMilliseconds(30),
            CommandRunner = async (_, ct) => {
                await Task.Delay(Timeout.Infinite, ct);
                return (0, "", "");
            },
        };
        await supervisor.RequestVerbAsync("restart").WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(logger.Entries[0].Message, Does.Contain("timed out"));
    }

    [TestCase("active", 0, GuiderProcessStatus.Active)]
    [TestCase("inactive", 3, GuiderProcessStatus.Inactive)]
    [TestCase("failed", 3, GuiderProcessStatus.Failed)]
    [TestCase("activating", 3, GuiderProcessStatus.Activating)]
    [TestCase("unknown", 4, GuiderProcessStatus.Unknown)]
    public async Task Classifies_status_even_with_nonzero_exit(string output, int exit, GuiderProcessStatus expected) {
        var supervisor = new SystemctlGuiderProcessSupervisor(new TestLogger()) {
            CommandRunner = (_, _) => Task.FromResult((exit, output, "")),
        };
        Assert.That(await supervisor.QueryStatusAsync(CancellationToken.None), Is.EqualTo(expected));
    }

    [Test]
    public async Task Missing_systemctl_degrades_without_unobserved_exception() {
        var logger = new TestLogger();
        var supervisor = new SystemctlGuiderProcessSupervisor(logger) {
            CommandRunner = (_, _) => throw new Win32Exception("missing"),
        };
        await supervisor.RequestVerbAsync("start");
        Assert.That(await supervisor.QueryStatusAsync(CancellationToken.None), Is.EqualTo(GuiderProcessStatus.Unknown));
        Assert.That(logger.Entries, Has.Count.EqualTo(2));
    }
}
