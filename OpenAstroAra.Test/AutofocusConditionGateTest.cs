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
using OpenAstroAra.Sequencer.Interfaces;
using OpenAstroAra.Sequencer.SequenceItem;
using OpenAstroAra.Sequencer.Trigger.Autofocus;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §59.9 — autofocus condition deferral: the <see cref="DiagnosticsAutofocusGate"/> over
    /// §51 diagnostics (sky-condition issues defer, everything else doesn't, faults fail
    /// open, one notification per episode) and the trigger family's deferral behavior
    /// (level-based triggers retry naturally; the edge-based exposure-count trigger latches).
    /// </summary>
    [TestFixture]
    public class AutofocusConditionGateTest {

        private static DiagnosticsStateDto State(params DiagnosticIssueDto[] issues) => new(
            Health: issues.Length == 0 ? DiagnosticHealth.Green : DiagnosticHealth.Yellow,
            Mode: DiagnosticsMode.Observe,
            OpenIssueCount: issues.Length,
            LastHourIssueCount: issues.Length,
            LastEvaluationUtc: DateTimeOffset.UtcNow,
            OpenIssues: issues);

        private static DiagnosticIssueDto Issue(string type) => new(
            Id: Guid.NewGuid(),
            IssueType: type,
            Severity: DiagnosticHealth.Yellow,
            Description: type,
            DetectedUtc: DateTimeOffset.UtcNow,
            RecommendedAction: null,
            AutoCorrectible: false);

        private static Mock<IDiagnosticsService> Diagnostics(DiagnosticsStateDto state) {
            var diagnostics = new Mock<IDiagnosticsService>();
            diagnostics.Setup(d => d.GetStateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(state);
            return diagnostics;
        }

        [Test]
        public void SkyConditionIssue_DefersWithTheHumanReason() {
            var gate = new DiagnosticsAutofocusGate(Diagnostics(State(Issue("clouds_passing"))).Object);
            Assert.That(gate.DeferralReason(), Is.EqualTo("clouds passing"));
        }

        [Test]
        public void NonSkyIssues_NeverDefer() {
            var gate = new DiagnosticsAutofocusGate(Diagnostics(State(Issue("disk_space_critical"), Issue("guider_crash"))).Object);
            Assert.That(gate.DeferralReason(), Is.Null,
                "a disk or guider issue says nothing about whether stars are measurable");
        }

        [Test]
        public void CleanState_DoesNotDefer() {
            var gate = new DiagnosticsAutofocusGate(Diagnostics(State()).Object);
            Assert.That(gate.DeferralReason(), Is.Null);
        }

        [Test]
        public void ABrokenDiagnosticsRead_FailsOpen() {
            var diagnostics = new Mock<IDiagnosticsService>();
            diagnostics.Setup(d => d.GetStateAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("db gone"));
            var gate = new DiagnosticsAutofocusGate(diagnostics.Object);

            Assert.That(gate.DeferralReason(), Is.Null, "diagnostics must never freeze focusing");
        }

        [Test]
        public void AWedgedDiagnosticsRead_FailsOpenAfterTheBound() {
            var diagnostics = new Mock<IDiagnosticsService>();
            diagnostics.Setup(d => d.GetStateAsync(It.IsAny<CancellationToken>()))
                .Returns(new TaskCompletionSource<DiagnosticsStateDto>().Task);
            var gate = new DiagnosticsAutofocusGate(diagnostics.Object);

            Assert.That(gate.DeferralReason(), Is.Null,
                "a wedged DB costs one bounded pause, never a hung run worker");
        }

        [Test]
        public void NotifiesOncePerEpisode_AndAgainOnANewEpisode() {
            var state = State(Issue("dew_formation"));
            var diagnostics = new Mock<IDiagnosticsService>();
            diagnostics.Setup(d => d.GetStateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => state);
            var notifications = new Mock<INotificationService>();
            var posted = new List<string>();
            notifications.Setup(n => n.CreateAsync(It.IsAny<NotificationDto>(), It.IsAny<CancellationToken>()))
                .Callback<NotificationDto, CancellationToken>((n, _) => { lock (posted) posted.Add(n.Message); })
                .Returns(Task.CompletedTask);
            var gate = new DiagnosticsAutofocusGate(diagnostics.Object, notifications.Object) {
                // Zero TTL so each call re-reads — this test drives state transitions call-by-call.
                CacheTtl = TimeSpan.Zero,
            };

            gate.DeferralReason();
            gate.DeferralReason();
            gate.DeferralReason();
            lock (posted) {
                Assert.That(posted, Has.Count.EqualTo(1), "one alert per episode, not one per trigger check");
                Assert.That(posted[0], Does.Contain("dew forming").And.Contain("Will run when conditions recover"));
            }

            state = State(); // sky recovers …
            Assert.That(gate.DeferralReason(), Is.Null);
            state = State(Issue("clouds_passing")); // … and a NEW episode opens
            Assert.That(gate.DeferralReason(), Is.EqualTo("clouds passing"));
            lock (posted) {
                Assert.That(posted, Has.Count.EqualTo(2), "a fresh episode alerts again");
            }
        }

        [Test]
        public void OneReadServesTheWholeTriggerPass() {
            var reads = 0;
            var diagnostics = new Mock<IDiagnosticsService>();
            diagnostics.Setup(d => d.GetStateAsync(It.IsAny<CancellationToken>()))
                .Callback(() => Interlocked.Increment(ref reads))
                .ReturnsAsync(State(Issue("clouds_passing")));
            var gate = new DiagnosticsAutofocusGate(diagnostics.Object); // default 1 s TTL

            // Five due triggers in one RunTriggers pass — without memoization each would
            // block the run-engine thread on its own diagnostics read.
            for (var i = 0; i < 5; i++) {
                Assert.That(gate.DeferralReason(), Is.EqualTo("clouds passing"));
            }
            Assert.That(reads, Is.EqualTo(1));
        }

        // ---- trigger-side deferral behavior ----

        private sealed class FakeGate : IAutofocusConditionGate {
            public string? Reason { get; set; }
            public string? DeferralReason() => Reason;
        }

        private static ISequenceItem LightExposure() {
            var item = new Mock<IExposureItem>();
            item.SetupGet(x => x.ImageType).Returns("LIGHT");
            return item.As<ISequenceItem>().Object;
        }

        [Test]
        public void TimeTrigger_DefersThenRetriesWhenTheSkyRecovers() {
            var history = new AutofocusConditionGateTestHistory();
            history.Afs.Add(new AutofocusHistoryEntry(0, DateTimeOffset.UtcNow.AddMinutes(-60), 5.0, "Ha"));
            var gate = new FakeGate { Reason = "clouds passing" };
            var sut = new AutofocusAfterTimeTrigger(history, gate) { Amount = 30 };

            Assert.That(sut.ShouldTrigger(null, LightExposure()), Is.False, "deferred while clouds pass");

            gate.Reason = null;
            Assert.That(sut.ShouldTrigger(null, LightExposure()), Is.True, "elapsed time persists — fires on recovery");
        }

        [Test]
        public void FilterTrigger_ADeferralKeepsTheReferenceSoTheOwedFocusSurvives() {
            var wheel = new Mock<OpenAstroAra.Equipment.Interfaces.Mediator.IFilterWheelMediator>();
            wheel.Setup(w => w.GetInfo()).Returns(new OpenAstroAra.Equipment.Equipment.MyFilterWheel.FilterWheelInfo {
                Connected = true,
                SelectedFilter = new OpenAstroAra.Core.Model.Equipment.FilterInfo("Ha", 0, 0),
            });
            var gate = new FakeGate();
            var sut = new AutofocusAfterFilterChange(wheel.Object, null, null, gate);
            sut.Initialize();

            wheel.Setup(w => w.GetInfo()).Returns(new OpenAstroAra.Equipment.Equipment.MyFilterWheel.FilterWheelInfo {
                Connected = true,
                SelectedFilter = new OpenAstroAra.Core.Model.Equipment.FilterInfo("OIII", 0, 0),
            });
            gate.Reason = "dew forming on the optics";
            Assert.That(sut.ShouldTrigger(null, LightExposure()), Is.False);
            Assert.That(sut.LastAutoFocusFilter, Is.EqualTo("Ha"), "the reference must survive the deferral");

            gate.Reason = null;
            Assert.That(sut.ShouldTrigger(null, LightExposure()), Is.True, "the owed focus fires on recovery");
        }

        [Test]
        public void ExposureCountTrigger_LatchesADeferredFireUntilRecovery() {
            var gate = new FakeGate();
            var sut = new AutofocusAfterExposures(gate) { AfterExposures = 2 };

            Assert.That(sut.ShouldTrigger(LightExposure(), null), Is.False, "exposure 1 of 2");
            gate.Reason = "clouds passing";
            Assert.That(sut.ShouldTrigger(LightExposure(), null), Is.False, "due at exposure 2 but deferred");

            gate.Reason = null;
            Assert.That(sut.ShouldTrigger(new Mock<ISequenceItem>().Object, null), Is.True,
                "the latched fire lands on the NEXT check after recovery — it must not slip to exposure 4");
            Assert.That(sut.ShouldTrigger(LightExposure(), null), Is.False, "exposure 3 — the cadence resumes normally");
            Assert.That(sut.ShouldTrigger(LightExposure(), null), Is.True, "exposure 4 fires on cadence");
        }
    }

    // NUnit-visible helper: a plain fake IImageHistory (the trigger family test has its own
    // private copy; kept separate so the two fixtures stay independent).
    internal sealed class AutofocusConditionGateTestHistory : IImageHistory {
        public List<ImageHistoryEntry> Images { get; } = [];
        public List<AutofocusHistoryEntry> Afs { get; } = [];
        public IReadOnlyList<ImageHistoryEntry> ImagePoints => Images;
        public IReadOnlyList<AutofocusHistoryEntry> AutofocusPoints => Afs;
    }
}
