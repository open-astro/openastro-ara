#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §63.3 guider-d crash-recovery decision tree. The supervisor (systemctl I/O) is faked and the
    /// backoff delay is a no-op, so the whole tree runs instantly and deterministically.
    /// </summary>
    [TestFixture]
    public class GuiderRecoveryCoordinatorTest {

        // A scripted supervisor: returns the queued statuses in order (repeating the last one), and
        // counts restart requests.
        private sealed class FakeSupervisor : IGuiderProcessSupervisor {
            private readonly Queue<GuiderProcessStatus> _statuses;
            private GuiderProcessStatus _last;
            public int RestartCount { get; private set; }

            public FakeSupervisor(params GuiderProcessStatus[] statuses) {
                _statuses = new Queue<GuiderProcessStatus>(statuses);
                _last = statuses.Length > 0 ? statuses[^1] : GuiderProcessStatus.Unknown;
            }

            public Task<GuiderProcessStatus> QueryStatusAsync(CancellationToken ct) {
                if (_statuses.Count > 0) {
                    _last = _statuses.Dequeue();
                }
                return Task.FromResult(_last);
            }

            public void RequestRestart() => RestartCount++;
        }

        private static readonly IReadOnlyList<TimeSpan> FastBackoff = new[] {
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero,
        };

        private static GuiderRecoveryCoordinator NewCoordinator(
            IGuiderProcessSupervisor supervisor,
            out Mock<INotificationService> notifications,
            out Mock<IDiagnosticsService> diagnostics) {
            notifications = new Mock<INotificationService>();
            notifications.Setup(n => n.CreateAsync(It.IsAny<NotificationDto>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            diagnostics = new Mock<IDiagnosticsService>();
            diagnostics.Setup(d => d.CreateEventAsync(It.IsAny<DiagnosticEventDto>(), It.IsAny<string?>(),
                    It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            return new GuiderRecoveryCoordinator(
                supervisor, notifications.Object, diagnostics.Object,
                NullLogger<GuiderRecoveryCoordinator>.Instance,
                FastBackoff,
                static (_, _) => Task.CompletedTask);
        }

        [Test]
        public async Task Active_on_first_poll_recovers_without_restart() {
            var supervisor = new FakeSupervisor(GuiderProcessStatus.Active);
            var coordinator = NewCoordinator(supervisor, out var notifications, out _);

            var outcome = await coordinator.RecoverAsync(CancellationToken.None);

            Assert.That(outcome, Is.EqualTo(GuiderRecoveryOutcome.Recovered));
            Assert.That(supervisor.RestartCount, Is.Zero, "no restart needed when systemd already brought it back");
            // A "lost" Warning and a "recovered" Info notification were emitted.
            notifications.Verify(n => n.CreateAsync(
                It.Is<NotificationDto>(x => x.Severity == NotificationSeverity.Info), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Test]
        public async Task Activating_then_active_recovers() {
            var supervisor = new FakeSupervisor(GuiderProcessStatus.Activating, GuiderProcessStatus.Active);
            var coordinator = NewCoordinator(supervisor, out _, out _);

            var outcome = await coordinator.RecoverAsync(CancellationToken.None);

            Assert.That(outcome, Is.EqualTo(GuiderRecoveryOutcome.Recovered));
            Assert.That(supervisor.RestartCount, Is.Zero, "activating means systemd is already restarting it");
        }

        [Test]
        public async Task Failed_nudges_restart_once_then_recovers_when_it_comes_up() {
            var supervisor = new FakeSupervisor(
                GuiderProcessStatus.Failed, GuiderProcessStatus.Activating, GuiderProcessStatus.Active);
            var coordinator = NewCoordinator(supervisor, out _, out _);

            var outcome = await coordinator.RecoverAsync(CancellationToken.None);

            Assert.That(outcome, Is.EqualTo(GuiderRecoveryOutcome.Recovered));
            Assert.That(supervisor.RestartCount, Is.EqualTo(1), "a failed unit is nudged exactly once");
        }

        [Test]
        public async Task Persistent_failure_reports_failed_with_critical_notification_and_red_diagnostic() {
            // Always failed → nudge once, never recovers, exhaust backoff.
            var supervisor = new FakeSupervisor(GuiderProcessStatus.Failed);
            var coordinator = NewCoordinator(supervisor, out var notifications, out var diagnostics);

            var outcome = await coordinator.RecoverAsync(CancellationToken.None);

            Assert.That(outcome, Is.EqualTo(GuiderRecoveryOutcome.Failed));
            Assert.That(supervisor.RestartCount, Is.EqualTo(1), "restart is nudged once, not on every poll");
            notifications.Verify(n => n.CreateAsync(
                It.Is<NotificationDto>(x => x.Severity == NotificationSeverity.Critical), It.IsAny<CancellationToken>()),
                Times.Once);
            diagnostics.Verify(d => d.CreateEventAsync(
                It.Is<DiagnosticEventDto>(x => x.Severity == DiagnosticHealth.Red && x.EventType == "guider.process.failed"),
                It.IsAny<string?>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Test]
        public async Task Unknown_status_is_unsupervised_and_never_restarts() {
            var supervisor = new FakeSupervisor(GuiderProcessStatus.Unknown);
            var coordinator = NewCoordinator(supervisor, out var notifications, out var diagnostics);

            var outcome = await coordinator.RecoverAsync(CancellationToken.None);

            Assert.That(outcome, Is.EqualTo(GuiderRecoveryOutcome.Unsupervised));
            Assert.That(supervisor.RestartCount, Is.Zero, "no systemd host → never shells out");
            // No Critical/failed notification on a dev box without systemd.
            notifications.Verify(n => n.CreateAsync(
                It.Is<NotificationDto>(x => x.Severity == NotificationSeverity.Critical), It.IsAny<CancellationToken>()),
                Times.Never);
            diagnostics.Verify(d => d.CreateEventAsync(It.IsAny<DiagnosticEventDto>(), It.IsAny<string?>(),
                It.IsAny<bool?>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public void Cancellation_propagates_out_of_recovery() {
            var supervisor = new FakeSupervisor(GuiderProcessStatus.Activating);
            // Delay that throws on a cancelled token so the loop observes cancellation.
            var coordinator = new GuiderRecoveryCoordinator(
                supervisor, Mock.Of<INotificationService>(), Mock.Of<IDiagnosticsService>(),
                NullLogger<GuiderRecoveryCoordinator>.Instance,
                FastBackoff,
                static (_, ct) => Task.FromCanceled(ct));
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.ThrowsAsync<TaskCanceledException>(() => coordinator.RecoverAsync(cts.Token));
        }
    }
}
