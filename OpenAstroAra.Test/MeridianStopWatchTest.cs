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
using OpenAstroAra.Astrometry;
using OpenAstroAra.Equipment.Equipment.MyTelescope;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#2044 Testing Decision 7 — with the flip off and "Stop before meridian" set, the watch stops tracking
    /// and pauses the runs once when the meridian is that close; never with the flip on, and never a slew or a park.</summary>
    [TestFixture]
    public class MeridianStopWatchTest {

        private const double Lst = 3.0; // hours

        private static SafetyPoliciesDto Safety(bool flipAuto, int? stopBefore) =>
            new(OnUnsafe: "pause_and_park", AutoResumeWhenSafe: true, ResumeDelayMin: 10,
                MeridianFlipAuto: flipAuto, MeridianPauseMin: 0, MeridianRecenter: true,
                MeridianRecalGuider: true, OnAltitudeLimit: "skip_target",
                ParkIfNoMoreTargets: true, OnGuiderLost: "pause_and_retry",
                GuiderRetryTimeoutSec: 60, SkipTargetIfRecoveryFails: true,
                StopBeforeMeridianMin: stopBefore);

        // The target is [minutesToMeridian] sidereal minutes east of the meridian (negative = already past it).
        private static TelescopeInfo Tracking(double minutesToMeridian, bool tracking = true) => new() {
            Connected = true,
            TrackingEnabled = tracking,
            SiderealTime = Lst,
            Coordinates = new Coordinates(Angle.ByHours(Lst + minutesToMeridian / 60.0), Angle.ByDegree(30), Epoch.JNOW),
        };

        private sealed class Rig {
            public Mock<IProfileStore> Profiles { get; } = new();
            public Mock<ITelescopeMediator> Mount { get; } = new(MockBehavior.Strict);
            public Mock<ISequencerService> Sequencer { get; } = new();
            public List<(string Event, JsonElement Payload)> Published { get; } = [];
            public MeridianStopWatch Watch { get; }
            public TelescopeInfo Info { get; set; }

            public Rig(SafetyPoliciesDto safety, TelescopeInfo info, IReadOnlyList<Guid>? paused = null) {
                Profiles.Setup(p => p.GetSafetyPolicies()).Returns(safety);
                Info = info;
                Mount.Setup(m => m.GetInfo()).Returns(() => Info);
                Mount.Setup(m => m.SetTrackingEnabled(false)).Returns(true);
                Sequencer.Setup(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()))
                    .ReturnsAsync(paused ?? [Guid.NewGuid()]);
                var ws = new Mock<IWsBroadcaster>();
                ws.Setup(w => w.PublishAsync(It.IsAny<WsEvent>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
                    .Callback<WsEvent, JsonElement, CancellationToken>((e, p, _) => Published.Add((e.Token, p)))
                    .Returns(Task.CompletedTask);
                Watch = new MeridianStopWatch(Profiles.Object, Mount.Object, () => Sequencer.Object, ws.Object,
                    NullLogger<MeridianStopWatch>.Instance);
            }
        }

        [Test]
        public async Task Flip_off_stop_before_10_nine_minutes_out_stops_tracking_and_pauses_runs_once() {
            var rig = new Rig(Safety(flipAuto: false, stopBefore: 10), Tracking(9));
            await rig.Watch.TickAsync(CancellationToken.None);
            await rig.Watch.TickAsync(CancellationToken.None);

            rig.Mount.Verify(m => m.SetTrackingEnabled(false), Times.Once);
            rig.Sequencer.Verify(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Once);
            Assert.That(rig.Published, Has.Count.EqualTo(1));
            var (token, payload) = rig.Published[0];
            Assert.That(token, Is.EqualTo("telescope.limit_reached"));
            Assert.That(payload.GetProperty("limit").GetString(), Is.EqualTo("stop_before_meridian"));
            Assert.That(payload.GetProperty("minutes").GetInt32(), Is.EqualTo(10));
            Assert.That(payload.GetProperty("tracking_stopped").GetBoolean(), Is.True);
            Assert.That(payload.GetProperty("runs_paused").GetInt32(), Is.EqualTo(1));
        }

        [Test]
        public async Task The_watch_never_slews_or_parks() {
            // Strict mount: any call but GetInfo and SetTrackingEnabled(false) throws, and none other is recorded.
            var rig = new Rig(Safety(flipAuto: false, stopBefore: 10), Tracking(9));
            await rig.Watch.TickAsync(CancellationToken.None);

            rig.Mount.Verify(m => m.GetInfo(), Times.AtLeastOnce);
            rig.Mount.Verify(m => m.SetTrackingEnabled(false), Times.Once);
            rig.Mount.VerifyNoOtherCalls();
        }

        [Test]
        public async Task With_flip_auto_it_does_not_trip() {
            var rig = new Rig(Safety(flipAuto: true, stopBefore: 10), Tracking(9));
            await rig.Watch.TickAsync(CancellationToken.None);

            rig.Mount.Verify(m => m.SetTrackingEnabled(It.IsAny<bool>()), Times.Never);
            rig.Sequencer.Verify(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Never);
            Assert.That(rig.Published, Is.Empty);
        }

        [TestCase(11.0)]
        [TestCase(-2.0)]
        public async Task Outside_the_window_or_past_the_meridian_it_does_not_trip(double minutesToMeridian) {
            var rig = new Rig(Safety(flipAuto: false, stopBefore: 10), Tracking(minutesToMeridian));
            await rig.Watch.TickAsync(CancellationToken.None);

            rig.Mount.Verify(m => m.SetTrackingEnabled(It.IsAny<bool>()), Times.Never);
            Assert.That(rig.Published, Is.Empty);
        }

        [Test]
        public async Task Stop_before_off_does_not_trip() {
            var rig = new Rig(Safety(flipAuto: false, stopBefore: null), Tracking(1));
            await rig.Watch.TickAsync(CancellationToken.None);

            rig.Mount.Verify(m => m.SetTrackingEnabled(It.IsAny<bool>()), Times.Never);
            Assert.That(rig.Published, Is.Empty);
        }

        [Test]
        public async Task With_no_plan_running_it_still_stops_tracking_and_says_so() {
            var rig = new Rig(Safety(flipAuto: false, stopBefore: 10), Tracking(9), paused: []);
            await rig.Watch.TickAsync(CancellationToken.None);

            rig.Mount.Verify(m => m.SetTrackingEnabled(false), Times.Once);
            Assert.That(rig.Published, Has.Count.EqualTo(1));
            Assert.That(rig.Published[0].Payload.GetProperty("runs_paused").GetInt32(), Is.Zero);
        }

        [Test]
        public async Task It_rearms_once_the_pointing_leaves_the_window() {
            // In the window, out of it (past the meridian, or a slew to the next target), back in: a second trip.
            var rig = new Rig(Safety(flipAuto: false, stopBefore: 10), Tracking(9));
            await rig.Watch.TickAsync(CancellationToken.None);
            rig.Info = Tracking(60);
            await rig.Watch.TickAsync(CancellationToken.None);
            rig.Info = Tracking(8);
            await rig.Watch.TickAsync(CancellationToken.None);

            rig.Mount.Verify(m => m.SetTrackingEnabled(false), Times.Exactly(2));
            rig.Sequencer.Verify(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
            Assert.That(rig.Published, Has.Count.EqualTo(2));
        }

        [Test]
        public async Task A_mount_not_tracking_is_left_alone() {
            var rig = new Rig(Safety(flipAuto: false, stopBefore: 10), Tracking(9, tracking: false));
            await rig.Watch.TickAsync(CancellationToken.None);

            rig.Mount.Verify(m => m.SetTrackingEnabled(It.IsAny<bool>()), Times.Never);
            rig.Sequencer.Verify(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Never);
        }
    }
}
