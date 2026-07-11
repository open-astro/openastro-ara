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
        private Mock<IObservingConditionsService> weather = null!;
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
            weather = new Mock<IObservingConditionsService>();
            // Default: no weather device connected — §35.1 tests opt in.
            weather.Setup(w => w.GetAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync((ObservingConditionsDto?)null);
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
            // Default: any tracked run still reads back Paused, so the countdown's
            // liveness filter keeps it (stale-run tests override this per-id).
            sequencer.Setup(s => s.GetRunStateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid id, CancellationToken _) => MakeRunState(id, SequenceRunState.Paused));
            SetPolicy("pause_and_park", autoResume: true, delayMin: 1);

            service = new SafetyReactionService(
                monitor.Object, weather.Object, profiles.Object, () => sequencer.Object,
                guider.Object, telescope.Object, notifications.Object, ws.Object) {
                ResumeDelayFromMinutes = _ => TimeSpan.FromMilliseconds(30),
                UnparkPollInterval = TimeSpan.FromMilliseconds(5),
                UnparkTimeout = TimeSpan.FromMilliseconds(500),
            };
        }

        [TearDown]
        public void TearDown() => service.Dispose();

        private void SetPolicy(string onUnsafe, bool autoResume, int delayMin,
                bool weatherTriggers = false, int maxWindKmh = 36, int maxHumidityPct = 85, double minDewDeltaC = 2.0) =>
            profiles.Setup(p => p.GetSafetyPolicies()).Returns(new SafetyPoliciesDto(
                OnUnsafe: onUnsafe, AutoResumeWhenSafe: autoResume, ResumeDelayMin: delayMin,
                MeridianFlipAuto: true, MeridianPauseMin: 2, MeridianRecenter: true, MeridianRecalGuider: false,
                OnAltitudeLimit: "pause", ParkIfNoMoreTargets: true, OnGuiderLost: "pause",
                GuiderRetryTimeoutSec: 60, SkipTargetIfRecoveryFails: false,
                WeatherTriggersEnabled: weatherTriggers, MaxWindKmh: maxWindKmh,
                MaxHumidityPct: maxHumidityPct, MinDewDeltaC: minDewDeltaC));

        private void SetWeather(double? windMs = null, double? gustMs = null, double? humidityPct = null,
                double? temperatureC = null, double? dewPointC = null, bool connected = true) =>
            weather.Setup(w => w.GetAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ObservingConditionsDto(
                    "w1", "Weather", connected ? EquipmentConnectionState.Connected : EquipmentConnectionState.Disconnected,
                    temperatureC, humidityPct, dewPointC, null, null, windMs, gustMs, null, null,
                    Safe: true, CapturedAt: "2026-01-01T00:00:00Z"));

        private void SetMonitor(bool? safe, bool connected = true) =>
            monitor.Setup(m => m.GetAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(safe is null && !connected
                    ? null
                    : new SafetyMonitorDto("sm1", "Cloudwatcher",
                        connected ? EquipmentConnectionState.Connected : EquipmentConnectionState.Disconnected,
                        safe ?? false, DateTimeOffset.UtcNow.ToString("O")));

        private static SequenceRunStateDto MakeRunState(Guid id, SequenceRunState state) =>
            new(id, Guid.NewGuid(), state, null, null, DateTimeOffset.UtcNow, null, 0, 1, null);

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
        public async Task Read_hiccup_on_a_persistently_unsafe_monitor_does_not_refire_the_reaction() {
            // unsafe → reaction fires and latches. A transient GetAsync fault
            // resets state to unknown; the next unsafe reading (unknown→unsafe)
            // must NOT re-park/re-notify — only a genuine safe reading clears
            // the latch (#731 round-2 debounce).
            SetMonitor(safe: false);
            await service.TickAsync();
            sequencer.Verify(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Once);

            monitor.Setup(m => m.GetAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new TimeoutException("driver hiccup"));
            await service.TickAsync();
            SetMonitor(safe: false);
            await service.TickAsync();

            sequencer.Verify(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Once);
            Assert.That(postedNotifications, Has.Count.EqualTo(1), "no notification spam per read hiccup");

            // A genuine safe reading clears the latch — the next unsafe re-fires.
            SetMonitor(safe: true);
            await service.TickAsync();
            SetMonitor(safe: false);
            await service.TickAsync();
            sequencer.Verify(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
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
        public async Task Relapse_to_unsafe_cancels_the_pending_resume_and_tracking_survives_the_flap() {
            // Mirrors the REAL bulk-pause contract: the run pauses on the first
            // unsafe; the second unsafe's bulk pause finds it already Paused and
            // returns EMPTY (the #731 review blind spot) — tracking must survive
            // the flap so a later durable safe window still resumes the run.
            var runId = Guid.NewGuid();
            var pauseCalls = 0;
            sequencer.Setup(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => ++pauseCalls == 1 ? new List<Guid> { runId } : new List<Guid>());
            IReadOnlyCollection<Guid>? resumedIds = null;
            sequencer.Setup(s => s.ResumeRunsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
                .Callback<IReadOnlyCollection<Guid>, CancellationToken>((ids, _) => resumedIds = ids)
                .ReturnsAsync(1);
            service.ResumeDelayFromMinutes = _ => TimeSpan.FromMinutes(10); // long — must never fire during the flap

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

            // Conditions clear durably: the resume must fire with the ORIGINAL run
            // id — the flap's empty bulk-pause result must not have orphaned it.
            service.ResumeDelayFromMinutes = _ => TimeSpan.FromMilliseconds(30);
            SetMonitor(safe: true);
            await service.TickAsync();
            await WaitUntilAsync(() => resumedIds is not null);
            Assert.That(resumedIds, Is.EquivalentTo(new[] { runId }));
        }

        [Test]
        public async Task Relapse_during_the_unpark_wait_aborts_the_resume_and_keeps_tracking() {
            // The countdown elapses and the unpark wait begins (mount keeps
            // reporting Parked so the read-back loop spins); conditions relapse
            // to unsafe MID-WAIT. The resume must abort — no run released into
            // unsafe conditions (#731 review) — and the ids must survive for the
            // next durable safe window.
            var runId = Guid.NewGuid();
            var pauseCalls = 0;
            sequencer.Setup(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => ++pauseCalls == 1 ? new List<Guid> { runId } : new List<Guid>());
            IReadOnlyCollection<Guid>? resumedIds = null;
            sequencer.Setup(s => s.ResumeRunsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
                .Callback<IReadOnlyCollection<Guid>, CancellationToken>((ids, _) => resumedIds = ids)
                .ReturnsAsync(1);
            var stillParked = true;
            telescope.Setup(t => t.GetAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new TelescopeDto("t1", "Mount", EquipmentConnectionState.Connected, null,
                    new TelescopeStateDto(stillParked ? "parked" : "tracking", null, null, Tracking: !stillParked, Parked: stillParked, AtHome: false)));
            var unparkRequested = 0;
            telescope.Setup(t => t.UnparkAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Callback(() => Interlocked.Increment(ref unparkRequested))
                .ReturnsAsync(Accepted);
            service.UnparkTimeout = TimeSpan.FromSeconds(5); // long enough that the relapse always lands mid-wait

            SetMonitor(safe: false);
            await service.TickAsync();
            SetMonitor(safe: true);
            await service.TickAsync();
            await WaitUntilAsync(() => Volatile.Read(ref unparkRequested) >= 1);

            // Relapse mid-unpark-wait: the countdown's cts cancels; the resume must not proceed.
            SetMonitor(safe: false);
            await service.TickAsync();
            await Task.Delay(150);
            sequencer.Verify(s => s.ResumeRunsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()), Times.Never);

            // Durable safe window afterwards: the retained id resumes for real.
            stillParked = false;
            SetMonitor(safe: true);
            await service.TickAsync();
            await WaitUntilAsync(() => resumedIds is not null);
            Assert.That(resumedIds, Is.EquivalentTo(new[] { runId }));
        }

        [Test]
        public async Task A_run_that_ended_during_the_countdown_never_triggers_an_unpark() {
            // The engine pauses run R; conditions clear; during the resume delay
            // the user aborts R. The countdown's liveness filter must drop the
            // stale id and leave the mount PARKED — no unpark, no tracking-on,
            // no "resumed" notification (#731 round-3).
            var runId = Guid.NewGuid();
            sequencer.Setup(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<Guid> { runId });
            sequencer.Setup(s => s.GetRunStateAsync(runId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(MakeRunState(runId, SequenceRunState.Stopped));

            SetMonitor(safe: false);
            await service.TickAsync();
            SetMonitor(safe: true);
            await service.TickAsync();

            await Task.Delay(200);
            telescope.Verify(t => t.UnparkAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
            telescope.Verify(t => t.SetTrackingAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
            sequencer.Verify(s => s.ResumeRunsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
            Assert.That(postedNotifications, Has.Count.EqualTo(1), "only the original unsafe notification — no spurious resume message");
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

        // ─── §35.1 weather thresholds ───

        private static SafetyPoliciesDto WeatherPolicy(int maxWindKmh = 36, int maxHumidityPct = 85, double minDewDeltaC = 2.0) =>
            new(OnUnsafe: "pause_and_park", AutoResumeWhenSafe: true, ResumeDelayMin: 10,
                MeridianFlipAuto: true, MeridianPauseMin: 2, MeridianRecenter: true, MeridianRecalGuider: false,
                OnAltitudeLimit: "pause", ParkIfNoMoreTargets: true, OnGuiderLost: "pause",
                GuiderRetryTimeoutSec: 60, SkipTargetIfRecoveryFails: false,
                WeatherTriggersEnabled: true, MaxWindKmh: maxWindKmh, MaxHumidityPct: maxHumidityPct, MinDewDeltaC: minDewDeltaC);

        private static ObservingConditionsDto Conditions(double? windMs = null, double? gustMs = null,
                double? humidityPct = null, double? temperatureC = null, double? dewPointC = null) =>
            new("w1", "Weather", EquipmentConnectionState.Connected,
                temperatureC, humidityPct, dewPointC, null, null, windMs, gustMs, null, null,
                Safe: true, CapturedAt: "2026-01-01T00:00:00Z");

        [Test]
        public void EvaluateWeatherBreaches_WindUsesTheWorseOfSpeedAndGust() {
            // 5 m/s sustained is fine, but the 12 m/s gust is 43 km/h — breach.
            var breaches = SafetyReactionService.EvaluateWeatherBreaches(
                Conditions(windMs: 5, gustMs: 12), WeatherPolicy(maxWindKmh: 36));
            Assert.That(breaches, Has.Count.EqualTo(1));
            Assert.That(breaches[0], Does.Contain("wind"));
        }

        [Test]
        public void EvaluateWeatherBreaches_HumidityAndDewDelta() {
            var breaches = SafetyReactionService.EvaluateWeatherBreaches(
                Conditions(humidityPct: 91, temperatureC: 5.0, dewPointC: 4.2), WeatherPolicy());
            Assert.Multiple(() => {
                Assert.That(breaches, Has.Count.EqualTo(2));
                Assert.That(breaches[0], Does.Contain("humidity"));
                Assert.That(breaches[1], Does.Contain("dew delta"));
            });
        }

        [Test]
        public void EvaluateWeatherBreaches_MissingSensorsAreNotBreaches() {
            // A device that reports nothing (or only in-range values) is clean —
            // no data is not a breach.
            Assert.That(SafetyReactionService.EvaluateWeatherBreaches(Conditions(), WeatherPolicy()), Is.Empty);
            Assert.That(SafetyReactionService.EvaluateWeatherBreaches(
                Conditions(windMs: 5, humidityPct: 60, temperatureC: 10, dewPointC: 2), WeatherPolicy()), Is.Empty);
        }

        [Test]
        public async Task WeatherBreach_WithNoSafetyMonitor_FiresTheReaction_WithTheReasonNamed() {
            SetPolicy("pause_and_park", autoResume: false, delayMin: 1, weatherTriggers: true);
            SetWeather(windMs: 15); // 54 km/h

            await service.TickAsync();

            telescope.Verify(t => t.ParkAsync(It.IsAny<ParkRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
            Assert.That(publishedEvents, Does.Contain("safety.unsafe"));
            Assert.That(postedNotifications, Has.Count.EqualTo(1));
            Assert.That(postedNotifications[0].Message, Does.Contain("wind"),
                "the operator must learn WHICH threshold tripped, not a bare unsafe");
        }

        [Test]
        public async Task WeatherBreach_WithTriggersDisabled_IsIgnored() {
            SetPolicy("pause_and_park", autoResume: false, delayMin: 1, weatherTriggers: false);
            SetWeather(windMs: 15);

            await service.TickAsync();

            telescope.Verify(t => t.ParkAsync(It.IsAny<ParkRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
            Assert.That(publishedEvents, Is.Empty, "default-off: an upgraded rig must not surprise-park");
        }

        [Test]
        public async Task MonitorSafe_ButWeatherBreached_IsUnsafe() {
            SetPolicy("pause_and_park", autoResume: false, delayMin: 1, weatherTriggers: true);
            SetMonitor(true);
            SetWeather(humidityPct: 95);

            await service.TickAsync();

            telescope.Verify(t => t.ParkAsync(It.IsAny<ParkRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
            Assert.That(postedNotifications[0].Message, Does.Contain("humidity"));
        }

        [Test]
        public async Task MonitorAndWeatherBothUnsafe_TheNotificationNamesBoth() {
            SetPolicy("pause_and_park", autoResume: false, delayMin: 1, weatherTriggers: true);
            SetMonitor(false);
            SetWeather(windMs: 15);

            await service.TickAsync();

            Assert.That(postedNotifications, Has.Count.EqualTo(1));
            Assert.That(postedNotifications[0].Message, Does.Contain("UNSAFE"));
            Assert.That(postedNotifications[0].Message, Does.Contain("wind"),
                "a combined breach must not drop the weather reasons from the operator-facing text");
        }

        [Test]
        public async Task WeatherRecovery_EmitsSafe() {
            SetPolicy("pause_and_park", autoResume: false, delayMin: 1, weatherTriggers: true);
            SetWeather(windMs: 15);
            await service.TickAsync();

            SetWeather(windMs: 2);
            await service.TickAsync();

            Assert.That(publishedEvents, Does.Contain("safety.safe"),
                "a cleared breach flows through the same safe-transition machinery");
        }

        // ─── §35 auto-resume pointing (PORT_TODO refinement) ───

        private static readonly string[] CenterThenResume = ["center", "resume"];

        private void RebuildServiceWithCentering(Mock<OpenAstroAra.Server.Services.ICenteringService> centering) {
            service.Dispose();
            service = new SafetyReactionService(
                monitor.Object, weather.Object, profiles.Object, () => sequencer.Object,
                guider.Object, telescope.Object, notifications.Object, ws.Object,
                logger: null, centeringResolver: () => centering.Object) {
                ResumeDelayFromMinutes = _ => TimeSpan.FromMilliseconds(30),
                UnparkPollInterval = TimeSpan.FromMilliseconds(5),
                UnparkTimeout = TimeSpan.FromMilliseconds(500),
            };
        }

        [Test]
        public async Task Auto_resume_recenters_the_paused_target_before_release() {
            var runId = Guid.NewGuid();
            var calls = new List<string>();
            sequencer.Setup(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<Guid> { runId });
            sequencer.Setup(s => s.GetActiveTargetCoordinatesAsync(runId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OpenAstroAra.Astrometry.Coordinates(
                    OpenAstroAra.Astrometry.Angle.ByHours(5.5),
                    OpenAstroAra.Astrometry.Angle.ByDegree(20.0),
                    OpenAstroAra.Astrometry.Epoch.JNOW));
            sequencer.Setup(s => s.ResumeRunsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
                .Callback(() => { lock (calls) { calls.Add("resume"); } })
                .ReturnsAsync(1);
            var centering = new Mock<OpenAstroAra.Server.Services.ICenteringService>();
            centering.Setup(c => c.CenterOnTarget(
                    It.IsAny<OpenAstroAra.Astrometry.Coordinates>(),
                    It.IsAny<IProgress<OpenAstroAra.PlateSolving.PlateSolveProgress>?>(),
                    It.IsAny<IProgress<OpenAstroAra.Core.Model.ApplicationStatus>?>(),
                    It.IsAny<CancellationToken>()))
                .Callback(() => { lock (calls) { calls.Add("center"); } })
                .ReturnsAsync(new OpenAstroAra.PlateSolving.PlateSolveResult()); // Success = true
            RebuildServiceWithCentering(centering);

            SetMonitor(safe: false);
            await service.TickAsync();
            SetMonitor(safe: true);
            await service.TickAsync();

            await WaitUntilAsync(() => { lock (calls) { return calls.Contains("resume"); } });
            lock (calls) {
                Assert.That(calls, Is.EqualTo(CenterThenResume),
                    "the re-center runs BEFORE the release, never after");
            }
            await WaitUntilAsync(() => {
                lock (postedNotifications) {
                    return postedNotifications.Exists(n => n.Message?.Contains("re-centered", StringComparison.Ordinal) == true);
                }
            });
        }

        [Test]
        public async Task A_failed_recenter_still_resumes_with_an_honest_warning() {
            var runId = Guid.NewGuid();
            var resumed = false;
            sequencer.Setup(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<Guid> { runId });
            sequencer.Setup(s => s.GetActiveTargetCoordinatesAsync(runId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OpenAstroAra.Astrometry.Coordinates(
                    OpenAstroAra.Astrometry.Angle.ByHours(5.5),
                    OpenAstroAra.Astrometry.Angle.ByDegree(20.0),
                    OpenAstroAra.Astrometry.Epoch.JNOW));
            sequencer.Setup(s => s.ResumeRunsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
                .Callback(() => resumed = true)
                .ReturnsAsync(1);
            var centering = new Mock<OpenAstroAra.Server.Services.ICenteringService>();
            centering.Setup(c => c.CenterOnTarget(
                    It.IsAny<OpenAstroAra.Astrometry.Coordinates>(),
                    It.IsAny<IProgress<OpenAstroAra.PlateSolving.PlateSolveProgress>?>(),
                    It.IsAny<IProgress<OpenAstroAra.Core.Model.ApplicationStatus>?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OpenAstroAra.PlateSolving.PlateSolveResult { Success = false });
            RebuildServiceWithCentering(centering);

            SetMonitor(safe: false);
            await service.TickAsync();
            SetMonitor(safe: true);
            await service.TickAsync();

            await WaitUntilAsync(() => resumed);
            await WaitUntilAsync(() => {
                lock (postedNotifications) {
                    return postedNotifications.Exists(n => n.Message?.Contains("did not converge", StringComparison.Ordinal) == true);
                }
            });
        }

        [Test]
        public async Task No_target_coordinates_means_no_recenter_attempt() {
            var runId = Guid.NewGuid();
            var resumed = false;
            sequencer.Setup(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<Guid> { runId });
            sequencer.Setup(s => s.GetActiveTargetCoordinatesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((OpenAstroAra.Astrometry.Coordinates?)null);
            sequencer.Setup(s => s.ResumeRunsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
                .Callback(() => resumed = true)
                .ReturnsAsync(1);
            var centering = new Mock<OpenAstroAra.Server.Services.ICenteringService>();
            RebuildServiceWithCentering(centering);

            SetMonitor(safe: false);
            await service.TickAsync();
            SetMonitor(safe: true);
            await service.TickAsync();

            await WaitUntilAsync(() => resumed);
            centering.Verify(c => c.CenterOnTarget(
                    It.IsAny<OpenAstroAra.Astrometry.Coordinates>(),
                    It.IsAny<IProgress<OpenAstroAra.PlateSolving.PlateSolveProgress>?>(),
                    It.IsAny<IProgress<OpenAstroAra.Core.Model.ApplicationStatus>?>(),
                    It.IsAny<CancellationToken>()),
                Times.Never, "a plan with no coordinate target must not drive the mount");
            await WaitUntilAsync(() => {
                lock (postedNotifications) {
                    return postedNotifications.Exists(n => n.Message?.Contains("Verify the pointing", StringComparison.Ordinal) == true);
                }
            });
        }
    }
}
