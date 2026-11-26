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
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    [TestFixture]
    public class RigOccupancyTest {

        [Test]
        public void A_routine_answers_only_for_what_it_holds() {
            var aligning = true;
            var occupancy = new RigOccupancy()
                .Register("polar alignment", RigHolding.Mount | RigHolding.GuideCamera, () => aligning);

            Assert.That(occupancy.Holder(RigHolding.Disk), Is.Null, "polar alignment writes no frames");
            Assert.That(occupancy.Holder(RigHolding.Mount), Is.EqualTo("polar alignment"));
            Assert.That(occupancy.Holder(), Is.EqualTo("polar alignment"));
            aligning = false;
            Assert.That(occupancy.Holder(), Is.Null, "a routine that ended answers nothing");
        }

        [Test]
        public void The_session_holds_the_rig_for_a_run_with_no_catalog_session() {
            var runs = new ActiveRunSessionRegistry();
            var sequencer = new Mock<ISequencerService>();
            var occupancy = new RigOccupancy().RegisterSession(runs, () => sequencer.Object);
            Assert.That(occupancy.Holder(), Is.Null, "nothing runs");

            sequencer.SetupGet(s => s.HasUnfinishedRun).Returns(true);
            Assert.That(occupancy.Holder(RigHolding.Disk), Is.EqualTo(RigOccupancy.Session), "the run is on with no catalog session");

            sequencer.SetupGet(s => s.HasUnfinishedRun).Returns(false);
            var session = Guid.NewGuid();
            runs.Enter(session);
            Assert.That(occupancy.Holder(RigHolding.Mount), Is.EqualTo(RigOccupancy.Session), "an open catalog session counts");
            runs.Exit(session);
            Assert.That(occupancy.Holder(), Is.Null);
        }

        private static readonly string[] MountHolderFirst = { "centering", "camera loop" };

        // #2142 — a stop ladder asks the register: every running routine that holds what must be free and has a stop,
        // the mount's holders first; a routine that does not hold it, or has ended, is left alone.
        [Test]
        public async Task A_stop_ends_the_running_holders_mount_first_and_leaves_the_rest_alone() {
            var stopped = new List<string>();
            var loopRuns = true;
            var centerRuns = true;
            var occupancy = new RigOccupancy()
                .Register("camera loop", RigHolding.MainCamera, () => loopRuns, (_, _) => { stopped.Add("camera loop"); loopRuns = false; return Task.CompletedTask; })
                .Register("centering", RigHolding.Mount | RigHolding.MainCamera, () => centerRuns, (_, _) => { stopped.Add("centering"); centerRuns = false; return Task.CompletedTask; })
                .Register("disk writer", RigHolding.Disk, () => true, (_, _) => { stopped.Add("disk writer"); return Task.CompletedTask; })
                .Register("ended", RigHolding.Any, () => false, (_, _) => { stopped.Add("ended"); return Task.CompletedTask; });

            var stops = await occupancy.StopAsync(RigHolding.Mount | RigHolding.MainCamera, MountStopReasons.EmergencyStop, TimeSpan.FromSeconds(5));

            Assert.Multiple(() => {
                Assert.That(stopped, Is.EqualTo(MountHolderFirst));
                Assert.That(stops.Select(s => s.Name), Is.EqualTo(MountHolderFirst));
                Assert.That(stops.All(s => s.Stopped), Is.True);
            });
        }

        private static readonly (string, bool)[] HungDeafOther = { ("hung", false), ("deaf", false), ("other", true) };

        // #2142 — a routine whose stop never returns, or throws, is reported; the next one still stops.
        [Test]
        public async Task A_routine_that_does_not_stop_is_reported_and_the_next_one_still_stops() {
            var otherRuns = true;
            var occupancy = new RigOccupancy()
                .Register("hung", RigHolding.Mount, () => true, (_, _) => new TaskCompletionSource().Task)
                .Register("deaf", RigHolding.Mount, () => true, (_, _) => throw new InvalidOperationException("driver refused"), failedRung: "stop_deaf")
                .Register("other", RigHolding.Mount, () => otherRuns, (_, _) => { otherRuns = false; return Task.CompletedTask; });

            var stops = await occupancy.StopAsync(RigHolding.Mount, MountStopReasons.SafetyPark, TimeSpan.FromMilliseconds(100))
                .WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Multiple(() => {
                Assert.That(stops.Select(s => (s.Name, s.Stopped)), Is.EqualTo(HungDeafOther));
                Assert.That(stops[0].Fault, Is.InstanceOf<TimeoutException>());
                Assert.That(stops[0].FailedRung, Is.EqualTo("stop_hung"));
                Assert.That(stops[1].FailedRung, Is.EqualTo("stop_deaf"));
                Assert.That(stops[1].Fault, Is.InstanceOf<InvalidOperationException>());
            });
        }

        // #2142 — the Session registers no stop: a ladder ends or pauses runs in its own step, so a safety park that asks
        // for the mount does not abort the run it should pause.
        [Test]
        public async Task The_session_is_not_stopped_through_the_register() {
            var sequencer = new Mock<ISequencerService>();
            sequencer.SetupGet(s => s.HasUnfinishedRun).Returns(true);
            var occupancy = new RigOccupancy().RegisterSession(new ActiveRunSessionRegistry(), () => sequencer.Object);

            var stops = await occupancy.StopAsync(RigHolding.Any, MountStopReasons.SafetyPark, TimeSpan.FromSeconds(1));

            Assert.That(stops, Is.Empty);
            sequencer.Verify(s => s.AbortActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Never);
            sequencer.Verify(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        // #2142 — the Polar Align Routine's stop is its takeover, named for the ladder, and the takeover's return confirms it.
        [Test]
        public async Task Polar_alignment_stops_by_its_takeover_and_is_confirmed_by_its_return() {
            var runs = true;
            var polar = new Mock<IPolarAlignService>();
            polar.SetupGet(p => p.IsActive).Returns(() => runs);
            polar.Setup(p => p.StopForTakeoverAsync(MountStopReasons.Shutdown, It.IsAny<CancellationToken>()))
                .Callback(() => runs = false).ReturnsAsync(new OpenAstroAra.Server.Contracts.OperationAcceptedDto(Guid.NewGuid(), "polar-align.stop", DateTimeOffset.UtcNow, null));
            var occupancy = new RigOccupancy().RegisterPolarAlign(() => polar.Object);

            var stops = await occupancy.StopAsync(RigHolding.Mount, MountStopReasons.Shutdown, TimeSpan.FromSeconds(1));

            Assert.That(runs, Is.False, "the takeover ended the run");
            polar.Verify(p => p.StopAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never, "not the Stop that confirms its own mount stop");
            Assert.That(stops.Single(), Is.EqualTo(new RoutineStop("polar alignment",
                RigHolding.Mount | RigHolding.MainCamera | RigHolding.GuideCamera, true, "stop_polar_align", null)));
        }

        /// <summary>#2143 — one routine registered by its helper over a fake (End Night and filter-offset calibration are
        /// real services: their fixtures cover them): its name, what it holds, how to make it run,
        /// and whether its stop reached it.</summary>
        private sealed record Routine(string Name, RigHolding Holds, string FailedRung, Func<RigOccupancy, Action, Func<bool>> Arrange);

        private static IEnumerable<string> RoutineNames() => Routines().Select(r => r.Name);

        private static IEnumerable<Routine> Routines() {
            yield return new Routine("autofocus", RigHolding.MainCamera | RigHolding.Focuser, "cancel_autofocus", (occupancy, ended) => {
                var tracker = new AutofocusRunTracker();
                var run = new CancellationTokenSource();
                tracker.Begin("classic", 100, 5, null, null, run);
                run.Token.Register(() => { tracker.Cancel(100); ended(); });
                occupancy.RegisterAutofocus(() => tracker);
                return () => tracker.IsRunning;
            });
            yield return new Routine("the Bahtinov focus readout", RigHolding.MainCamera, "stop_bahtinov", (occupancy, ended) => {
                var runs = true;
                var bahtinov = new Mock<IBahtinovFocusService>();
                bahtinov.SetupGet(b => b.IsActive).Returns(() => runs);
                bahtinov.Setup(b => b.StopAsync()).Callback(() => { runs = false; ended(); }).Returns(Task.CompletedTask);
                occupancy.RegisterBahtinov(() => bahtinov.Object);
                return () => runs;
            });
            yield return new Routine("guide-camera focus", RigHolding.GuideCamera, "stop_guide_focus", (occupancy, ended) => {
                var runs = true;
                var focus = new Mock<IGuideFocusService>();
                focus.SetupGet(f => f.IsActive).Returns(() => runs);
                focus.Setup(f => f.StopAsync()).Callback(() => { runs = false; ended(); }).Returns(Task.CompletedTask);
                occupancy.RegisterGuideFocus(() => focus.Object);
                return () => runs;
            });
            yield return new Routine("the rotation readout", RigHolding.MainCamera, "stop_rotation_assist", (occupancy, ended) => {
                var runs = true;
                var rotation = new Mock<IRotationAssistService>();
                rotation.SetupGet(r => r.IsBusy).Returns(() => runs);
                rotation.Setup(r => r.StopAsync()).Callback(() => { runs = false; ended(); }).Returns(Task.CompletedTask);
                occupancy.RegisterRotationAssist(() => rotation.Object);
                return () => runs;
            });
            yield return new Routine("Live View", RigHolding.MainCamera, "stop_live_view", (occupancy, ended) => {
                var runs = true;
                var camera = new Mock<ICameraService>();
                camera.Setup(c => c.GetLiveViewStatus()).Returns(() => LiveViewStatus(runs));
                camera.Setup(c => c.StopLiveViewAsync()).Callback(() => { runs = false; ended(); }).Returns(Task.CompletedTask);
                occupancy.RegisterLiveView(() => camera.Object);
                return () => runs;
            });
            yield return new Routine("a disk-to-disk copy", RigHolding.Disk, "stop_storage_copy", (occupancy, ended) => {
                var runs = true;
                var copy = new Mock<IStorageCopyService>();
                copy.SetupGet(c => c.IsRunning).Returns(() => runs);
                copy.Setup(c => c.Cancel()).Callback(() => { runs = false; ended(); }).Returns(true);
                occupancy.RegisterStorageCopy(() => copy.Object);
                return () => runs;
            });
        }

        private static OpenAstroAra.Server.Contracts.LiveViewStatusDto LiveViewStatus(bool active) =>
            new(active, 1, 0, null, null, null, null, null);

        // #2143 — each remaining routine: the busy questions that care about what it holds name it, the others do not, and
        // a ladder that frees what it holds stops it through its own stop and reports it stopped.
        [TestCaseSource(nameof(RoutineNames))]
        public async Task A_registered_routine_answers_for_its_holding_and_a_ladder_stops_it(string name) {
            var routine = Routines().Single(r => r.Name == name);
            var occupancy = new RigOccupancy();
            var stopReached = false;
            var isRunning = routine.Arrange(occupancy, () => stopReached = true);
            Assert.That(isRunning(), Is.True, "the routine runs");

            Assert.Multiple(() => {
                foreach (var holding in new[] { RigHolding.Mount, RigHolding.MainCamera, RigHolding.GuideCamera, RigHolding.Focuser, RigHolding.Disk }) {
                    Assert.That(occupancy.Holder(holding), Is.EqualTo((routine.Holds & holding) != 0 ? routine.Name : null), holding.ToString());
                }
            });

            var stops = await occupancy.StopAsync(routine.Holds, MountStopReasons.EmergencyStop, TimeSpan.FromSeconds(5));

            Assert.Multiple(() => {
                Assert.That(stopReached, Is.True, "its own stop was called");
                Assert.That(stops.Single(), Is.EqualTo(new RoutineStop(routine.Name, routine.Holds, true, routine.FailedRung, null)));
                Assert.That(isRunning(), Is.False, "it ended");
            });
        }
    }
}
