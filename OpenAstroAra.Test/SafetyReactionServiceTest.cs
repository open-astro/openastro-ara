#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Moq;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §35.4 — <see cref="SafetyReactionService"/>: the safe⇄unsafe transition
    /// classifier, the per-policy reaction (pause/abort + stop-guiding + park,
    /// events-before-action ordering), the no-re-fire-while-unsafe guard, and
    /// the auto-resume-when-safe countdown (armed, cancelled by a relapse,
    /// disabled by policy). All timer work is driven through
    /// <c>TickAsync()</c> directly; delays compress through the internal knobs.
    /// </summary>
    [TestFixture]
    public class SafetyReactionServiceTest {

        private Mock<ISafetyMonitorService> monitor = null!;
        private Mock<IProfileStore> profiles = null!;
        private Mock<ISequencerService> sequencer = null!;
        private Mock<IGuiderService> guider = null!;
        private Mock<ITelescopeService> telescope = null!;
        private Mock<INotificationService> notifications = null!;
        private Mock<IWsBroadcaster> ws = null!;
        private List<string> publishedEvents = null!;
        private List<NotificationDto> postedNotifications = null!;
        private SafetyReactionService service = null!;

        private static readonly OperationAcceptedDto Accepted =
            new(Guid.NewGuid(), "test", DateTimeOffset.UtcNow, null);

        [SetUp]
        public void SetUp() {
            monitor = new Mock<ISafetyMonitorService>();
            profiles = new Mock<IProfileStore>();
            sequencer = new Mock<ISequencerService>();
            guider = new Mock<IGuiderService>();
            telescope = new Mock<ITelescopeService>();
            notifications = new Mock<INotificationService>();
            ws = new Mock<IWsBroadcaster>();
            publishedEvents = new List<string>();
            postedNotifications = new List<NotificationDto>();

            ws.Setup(w => w.PublishAsync(It.IsAny<string>(), It.IsAny<System.Text.Json.JsonElement>(), It.IsAny<CancellationToken>()))
                .Callback<string, System.Text.Json.JsonElement, CancellationToken>((t, _, _) => { lock (publishedEvents) { publishedEvents.Add(t); } })
                .Returns(Task.CompletedTask);
            notifications.Setup(n => n.CreateAsync(It.IsAny<NotificationDto>(), It.IsAny<CancellationToken>()))
                .Callback<NotificationDto, CancellationToken>((n, _) => { lock (postedNotifications) { postedNotifications.Add(n); } })
                .Returns(Task.CompletedTask);
            guider.Setup(g => g.StopGuidingAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Accepted);
            telescope.Setup(t => t.ParkAsync(It.IsAny<ParkRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Accepted);
            telescope.Setup(t => t.UnparkAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Accepted);
            telescope.Setup(t => t.SetTrackingAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            telescope.Setup(t => t.GetAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TelescopeDto("t1", "Mount", EquipmentConnectionState.Connected, null,
                    new TelescopeStateDto("tracking", null, null, Tracking: true, Parked: false, AtHome: false)));
            sequencer.Setup(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<Guid>());
            sequencer.Setup(s => s.ResumeRunsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(0);
            sequencer.Setup(s => s.AbortActiveRunsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
            SetPolicy("pause_and_park", autoResume: true, delayMin: 1);

            service = new SafetyReactionService(
                monitor.Object, profiles.Object, () => sequencer.Object,
                guider.Object, telescope.Object, notifications.Object, ws.Object) {
                ResumeDelayFromMinutes = _ => TimeSpan.FromMilliseconds(30),
                UnparkPollInterval = TimeSpan.FromMilliseconds(5),
                UnparkTimeout = TimeSpan.FromMilliseconds(500),
            };
        }

        [TearDown]
        public void TearDown() => service.Dispose();

        private void SetPolicy(string onUnsafe, bool autoResume, int delayMin) =>
            profiles.Setup(p => p.GetSafetyPolicies()).Returns(new SafetyPoliciesDto(
                OnUnsafe: onUnsafe, AutoResumeWhenSafe: autoResume, ResumeDelayMin: delayMin,
                MeridianFlipAuto: true, MeridianPauseMin: 2, MeridianRecenter: true, MeridianRecalGuider: false,
                OnAltitudeLimit: "pause", ParkIfNoMoreTargets: true, OnGuiderLost: "pause",
                GuiderRetryTimeoutSec: 60, SkipTargetIfRecoveryFails: false));

        private void SetMonitor(bool? safe, bool connected = true) =>
            monitor.Setup(m => m.GetAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(safe is null && !connected
                    ? null
                    : new SafetyMonitorDto("sm1", "Cloudwatcher",
                        connected ? EquipmentConnectionState.Connected : EquipmentConnectionState.Disconnected,
                        safe ?? false, DateTimeOffset.UtcNow.ToString("O")));

        private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000) {
            var sw = Stopwatch.StartNew();
            while (!condition() && sw.ElapsedMilliseconds < timeoutMs) {
                await Task.Delay(10);
            }
            Assert.That(condition(), Is.True, "condition not reached within timeout");
        }

        // ─── pure classifier + token mapping ───

        [Test]
        public void ClassifyTransition_covers_the_full_matrix() {
            // (prev, now) → transition. Enum types are internal, so the matrix
            // asserts inline rather than through public TestCase parameters.
            Assert.Multiple(() => {
                Assert.That(SafetyReactionService.ClassifyTransition(null, null), Is.EqualTo(SafetyReactionService.Transition.None));
                Assert.That(SafetyReactionService.ClassifyTransition(null, true), Is.EqualTo(SafetyReactionService.Transition.None));
                Assert.That(SafetyReactionService.ClassifyTransition(null, false), Is.EqualTo(SafetyReactionService.Transition.BecameUnsafe));
                Assert.That(SafetyReactionService.ClassifyTransition(true, true), Is.EqualTo(SafetyReactionService.Transition.None));
                Assert.That(SafetyReactionService.ClassifyTransition(true, false), Is.EqualTo(SafetyReactionService.Transition.BecameUnsafe));
                Assert.That(SafetyReactionService.ClassifyTransition(true, null), Is.EqualTo(SafetyReactionService.Transition.None));
                Assert.That(SafetyReactionService.ClassifyTransition(false, true), Is.EqualTo(SafetyReactionService.Transition.BecameSafe));
                Assert.That(SafetyReactionService.ClassifyTransition(false, false), Is.EqualTo(SafetyReactionService.Transition.None));
                Assert.That(SafetyReactionService.ClassifyTransition(false, null), Is.EqualTo(SafetyReactionService.Transition.None));
            });
        }

        [Test]
        public void ParseAction_maps_tokens_with_protective_fallback() {
            Assert.Multiple(() => {
                Assert.That(SafetyReactionService.ParseAction("ignore"), Is.EqualTo(SafetyReactionService.UnsafeAction.Ignore));
                Assert.That(SafetyReactionService.ParseAction("park_only"), Is.EqualTo(SafetyReactionService.UnsafeAction.ParkOnly));
                Assert.That(SafetyReactionService.ParseAction("pause_and_park"), Is.EqualTo(SafetyReactionService.UnsafeAction.PauseAndPark));
                Assert.That(SafetyReactionService.ParseAction("abort_and_park"), Is.EqualTo(SafetyReactionService.UnsafeAction.AbortAndPark));
                Assert.That(SafetyReactionService.ParseAction("garbage"), Is.EqualTo(SafetyReactionService.UnsafeAction.PauseAndPark));
                Assert.That(SafetyReactionService.ParseAction(null), Is.EqualTo(SafetyReactionService.UnsafeAction.PauseAndPark));
            });
        }

        // ─── unsafe reactions per policy ───

        [Test]
        public async Task Unsafe_with_pause_and_park_pauses_stops_guiding_and_parks() {
            var runId = Guid.NewGuid();
            sequencer.Setup(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<Guid> { runId });
            SetMonitor(safe: false);

            await service.TickAsync();

            sequencer.Verify(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Once);
            sequencer.Verify(s => s.AbortActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Never);
            guider.Verify(g => g.StopGuidingAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
            telescope.Verify(t => t.ParkAsync(It.IsAny<ParkRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
            Assert.That(publishedEvents, Does.Contain("safety.unsafe"));
            Assert.That(publishedEvents, Does.Contain("safety.action_taken"));
            Assert.That(publishedEvents.IndexOf("safety.unsafe"), Is.LessThan(publishedEvents.IndexOf("safety.action_taken")),
                "safety.unsafe must fire before the action per §35.4");
            Assert.That(postedNotifications, Has.Count.EqualTo(1));
            Assert.That(postedNotifications[0].Severity, Is.EqualTo(NotificationSeverity.Critical));
        }

        [Test]
        public async Task Unsafe_with_ignore_notifies_but_touches_nothing() {
            SetPolicy("ignore", autoResume: true, delayMin: 1);
            SetMonitor(safe: false);

            await service.TickAsync();

            sequencer.Verify(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Never);
            sequencer.Verify(s => s.AbortActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Never);
            guider.Verify(g => g.StopGuidingAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
            telescope.Verify(t => t.ParkAsync(It.IsAny<ParkRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
            Assert.That(publishedEvents, Does.Contain("safety.unsafe"));
            Assert.That(postedNotifications, Has.Count.EqualTo(1));
            Assert.That(postedNotifications[0].Severity, Is.EqualTo(NotificationSeverity.Warning));
        }

        [Test]
        public async Task Unsafe_with_abort_and_park_aborts_instead_of_pausing() {
            SetPolicy("abort_and_park", autoResume: true, delayMin: 1);
            sequencer.Setup(s => s.AbortActiveRunsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
            SetMonitor(safe: false);

            await service.TickAsync();

            sequencer.Verify(s => s.AbortActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Once);
            sequencer.Verify(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Never);
            telescope.Verify(t => t.ParkAsync(It.IsAny<ParkRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task Persistent_unsafe_reacts_once_not_per_tick() {
            SetMonitor(safe: false);

            await service.TickAsync();
            await service.TickAsync();
            await service.TickAsync();

            sequencer.Verify(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Once);
            Assert.That(publishedEvents.FindAll(e => e == "safety.unsafe"), Has.Count.EqualTo(1));
        }

        [Test]
        public async Task Disconnected_monitor_is_unknown_not_a_transition() {
            SetMonitor(safe: null, connected: false);
            await service.TickAsync();

            sequencer.Verify(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Never);
            Assert.That(publishedEvents, Is.Empty);

            // A monitor that CONNECTS reporting unsafe must trigger (unknown→unsafe).
            SetMonitor(safe: false);
            await service.TickAsync();
            sequencer.Verify(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task Safe_first_observation_takes_no_action() {
            SetMonitor(safe: true);
            await service.TickAsync();

            Assert.That(publishedEvents, Is.Empty);
            Assert.That(postedNotifications, Is.Empty);
        }

        // ─── auto-resume ───

        [Test]
        public async Task Auto_resume_unparks_waits_and_releases_the_paused_runs() {
            var runId = Guid.NewGuid();
            sequencer.Setup(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<Guid> { runId });
            IReadOnlyCollection<Guid>? resumedIds = null;
            sequencer.Setup(s => s.ResumeRunsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
                .Callback<IReadOnlyCollection<Guid>, CancellationToken>((ids, _) => resumedIds = ids)
                .ReturnsAsync(1);
            SetMonitor(safe: false);
            await service.TickAsync();

            SetMonitor(safe: true);
            await service.TickAsync();

            await WaitUntilAsync(() => resumedIds is not null);
            Assert.That(resumedIds, Is.EquivalentTo(new[] { runId }));
            telescope.Verify(t => t.UnparkAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
            telescope.Verify(t => t.SetTrackingAsync(true, It.IsAny<CancellationToken>()), Times.Once);
            await WaitUntilAsync(() => { lock (publishedEvents) { return publishedEvents.Contains("safety.safe"); } });
            await WaitUntilAsync(() => { lock (postedNotifications) { return postedNotifications.Count == 2; } });
        }

        [Test]
        public async Task Relapse_to_unsafe_cancels_the_pending_resume() {
            sequencer.Setup(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<Guid> { Guid.NewGuid() });
            service.ResumeDelayFromMinutes = _ => TimeSpan.FromMinutes(10); // long — must never fire

            SetMonitor(safe: false);
            await service.TickAsync();
            SetMonitor(safe: true);
            await service.TickAsync();
            SetMonitor(safe: false);
            await service.TickAsync();

            // The second unsafe reacts again (its own pause/park) and kills the countdown.
            sequencer.Verify(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
            await Task.Delay(100);
            sequencer.Verify(s => s.ResumeRunsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task Auto_resume_disabled_leaves_the_run_paused() {
            SetPolicy("pause_and_park", autoResume: false, delayMin: 1);
            sequencer.Setup(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<Guid> { Guid.NewGuid() });

            SetMonitor(safe: false);
            await service.TickAsync();
            SetMonitor(safe: true);
            await service.TickAsync();

            await Task.Delay(120);
            sequencer.Verify(s => s.ResumeRunsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
            telescope.Verify(t => t.UnparkAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
            Assert.That(publishedEvents, Does.Contain("safety.safe"));
        }

        [Test]
        public async Task Park_only_leaves_the_sequencer_untouched() {
            SetPolicy("park_only", autoResume: true, delayMin: 1);
            SetMonitor(safe: false);

            await service.TickAsync();

            sequencer.Verify(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Never);
            sequencer.Verify(s => s.AbortActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Never);
            guider.Verify(g => g.StopGuidingAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
            telescope.Verify(t => t.ParkAsync(It.IsAny<ParkRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task Equipment_faults_do_not_stop_the_remaining_rungs() {
            guider.Setup(g => g.StopGuidingAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("guider not connected"));
            SetMonitor(safe: false);

            await service.TickAsync();

            // Park still requested + notification still posted despite the guider fault.
            telescope.Verify(t => t.ParkAsync(It.IsAny<ParkRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
            Assert.That(postedNotifications, Has.Count.EqualTo(1));
        }
    }
}
