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
using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Model;
using TelescopeAxes = OpenAstroAra.Core.Enums.TelescopeAxes;
using OpenAstroAra.Equipment.Equipment.MyTelescope;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.Refusals;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>The confirm step the app takes before a start (#2048): read the plan, then start with its direction.</summary>
    internal static class PolarAlignStartConfirmed {
        public static async Task<OperationAcceptedDto> StartConfirmedAsync(
                this PolarAlignService svc, string? stage, string? idempotencyKey, CancellationToken ct) {
            var plan = await svc.GetStartPlanAsync(stage, ct).ConfigureAwait(false);
            return await svc.StartAsync(stage, new PolarAlignStartConfirmation(plan.Direction, plan.NewOrigin), idempotencyKey, ct).ConfigureAwait(false);
        }

        public static Task<OperationAcceptedDto> StartConfirmedAsync(this PolarAlignService svc, string? idempotencyKey, CancellationToken ct) =>
            svc.StartConfirmedAsync(stage: null, idempotencyKey, ct);
    }

    /// <summary>
    /// #2048 — Polar Align starts like NINA's Three Point Polar Alignment (rows 1–3 of the NINA comparison): from where the
    /// user pointed the mount, never with Find Home or a GoTo; never from a parked mount, which it refuses and never
    /// unparks; and only after the user confirmed the start and the RA turn direction. Each Start makes one measurement
    /// (#2082), and its turns go the way the user confirmed.
    /// </summary>
    public partial class PolarAlignServiceTest {

        // ── a run never homes, slews or unparks ──────────────────────────────────────────────

        [Test]
        public async Task A_run_on_the_fake_mount_never_sends_Find_Home_a_GoTo_or_an_unpark() {
            // The fake mount can find Home and slew; a fine run and the Check from its adjust loop use its motors only.
            var mount = new FakeStopMount(tracking: true, canFindHome: true);
            var ws = new WsRecorder();
            using var svc = NewService(NewFake(), new HintFollowingSolver(), mount.Mock, ws, store: TenDegreeStore());
            mount.EventCount = () => ws.Events.Count;

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            Assert.That((await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false)).State, Is.EqualTo("adjusting"));
            await svc.StartConfirmedAsync(PolarAlignStages.Check, null, CancellationToken.None).ConfigureAwait(false);
            Assert.That((await PollStateAsync(svc, "checked", "failed").ConfigureAwait(false)).State, Is.EqualTo("checked"));

            var log = mount.Log;
            Assert.That(log.Where(c => c.StartsWith("move primary", StringComparison.Ordinal) && !c.EndsWith(" 0", StringComparison.Ordinal)),
                Is.Not.Empty, "the turns ran on the RA motor");
            Assert.That(log, Does.Not.Contain("findhome").And.Not.Contain("goto").And.Not.Contain("unpark"), string.Join(", ", log));
            Assert.That(ws.Count("polar_align.needs_home"), Is.Zero);
            Assert.That(ws.Events.Where(e => e.Type == WsEventCatalog.PolarAlignStage || e.Type == WsEventCatalog.PolarAlignStarted)
                .Select(e => e.Payload.GetProperty("state").GetString()), Has.None.EqualTo("homing"));
        }

        // ── parked ───────────────────────────────────────────────────────────────────────────

        [Test]
        public async Task A_parked_mount_is_refused_before_anything_moves_and_is_never_unparked() {
            // NINA refuses a parked mount (PolarAlignment.cs 1108-1110). Ara used to unpark it and move at once.
            var (mount, _) = ParkedMount(unparks: true);
            var fake = NewFake();
            var ws = new WsRecorder();
            using var svc = NewService(fake, new ScriptedSolver(), mount, ws);

            var planRefusal = await Assert.ThrowsAsync<RefusalException>(() => svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None));
            var startRefusal = await Assert.ThrowsAsync<RefusalException>(() => svc.StartAsync(
                PolarAlignStages.Fine, new PolarAlignStartConfirmation(PolarAlignDirections.East), null, CancellationToken.None));

            Assert.That(planRefusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignMountParked), "the confirm step already says so");
            Assert.That(startRefusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignMountParked));
            Assert.That(startRefusal.Message, Is.EqualTo("Mount is parked — unpark it and point near the pole first"));
            mount.Verify(m => m.UnparkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
            mount.Verify(m => m.MoveAxis(It.IsAny<TelescopeAxes>(), It.IsAny<double>()), Times.Never);
            Assert.That(fake.LeaseCalls, Is.Empty, "refused before the camera is taken");
            Assert.That(ws.Count(WsEventCatalog.PolarAlignStarted), Is.Zero);
            Assert.That((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).State, Is.EqualTo("idle"));
        }

        [Test]
        public async Task A_park_that_lands_after_Start_fails_the_run_as_mount_parked_before_anything_moves() {
            // StartAsync saw an unparked mount; the park lands while it takes the guide camera. The run must fail as
            // mount_parked before any turn or tilt, and never unpark the mount.
            var mount = NewMount();
            var parked = false;
            mount.Setup(m => m.GetInfo()).Returns(() => new TelescopeInfo {
                Connected = true, SiderealTime = 20.0, TrackingEnabled = !Volatile.Read(ref parked), AtPark = Volatile.Read(ref parked),
                SideOfPier = OpenAstroAra.Core.Enums.PierSide.pierWest, // NewMount's pose: HA −4 h, counterweight down
            });
            var fake = NewFake();
            fake.LeaseGate = active => {
                if (active) {
                    Volatile.Write(ref parked, true);
                }
                return Task.CompletedTask;
            };
            var ws = new WsRecorder();
            using var svc = NewService(fake, new ScriptedSolver(), mount, ws);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);

            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo("mount_parked"));
            Assert.That(error.GetProperty("message").GetString(), Is.EqualTo(PolarAlignService.MountParkedMessage));
            mount.Verify(m => m.UnparkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
            mount.Verify(m => m.MoveAxis(It.IsAny<TelescopeAxes>(), It.Is<double>(rate => rate != 0.0)), Times.Never,
                "no turn and no tilt: only the exit's MoveAxis 0");
            NoHomeNoGoTo(mount);
        }

        // ── the confirm step ─────────────────────────────────────────────────────────────────

        [Test]
        public async Task A_start_without_the_users_confirmation_is_refused_and_moves_nothing() {
            var mount = NewMount();
            var fake = NewFake();
            var ws = new WsRecorder();
            using var svc = NewService(fake, new ScriptedSolver(), mount, ws);

            var refusal = await Assert.ThrowsAsync<RefusalException>(() => svc.StartAsync(PolarAlignStages.Fine, confirmation: null, null, CancellationToken.None));

            Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignUnconfirmed));
            Assert.That(refusal.Parameters["direction"], Is.EqualTo(PolarAlignDirections.East), "the direction to confirm comes with the refusal");
            mount.Verify(m => m.MoveAxis(It.IsAny<TelescopeAxes>(), It.IsAny<double>()), Times.Never);
            mount.Verify(m => m.SetTrackingEnabled(It.IsAny<bool>()), Times.Never);
            Assert.That(fake.LeaseCalls, Is.Empty);
            Assert.That(ws.Count(WsEventCatalog.PolarAlignStarted), Is.Zero);
            Assert.That((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).State, Is.EqualTo("idle"));
        }

        [Test]
        public async Task A_start_confirmed_for_the_other_direction_is_refused_with_the_direction_now() {
            // The user saw "west", but the hour angle crossed the meridian since: the run would turn east. Ask again.
            var mount = NewMount();
            var fake = NewFake();
            using var svc = NewService(fake, new ScriptedSolver(), mount);

            var refusal = await Assert.ThrowsAsync<RefusalException>(() => svc.StartAsync(
                PolarAlignStages.Fine, new PolarAlignStartConfirmation(PolarAlignDirections.West), null, CancellationToken.None));

            Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignUnconfirmed));
            Assert.That(refusal.Parameters["direction"], Is.EqualTo(PolarAlignDirections.East));
            mount.Verify(m => m.MoveAxis(It.IsAny<TelescopeAxes>(), It.IsAny<double>()), Times.Never);
            Assert.That(fake.LeaseCalls, Is.Empty);
            Assert.That((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).State, Is.EqualTo("idle"));
        }

        // LST 20 h: RA 0 is 60° east of the meridian (turn east, RA up); RA 270° is 30° west of it (turn west, RA down).
        [TestCase(0.0, PolarAlignDirections.East, 30.0, 60.0)]
        [TestCase(270.0, PolarAlignDirections.West, 240.0, 210.0)]
        public async Task A_confirmed_start_runs_from_the_current_pose_and_turns_the_confirmed_way(
                double startRaDeg, string direction, double firstTurnRaDeg, double secondTurnRaDeg) {
            var stops = new ConcurrentQueue<Coordinates>();
            var mount = NewMount();
            var position = new Coordinates(startRaDeg, 85.0, Epoch.JNOW, Coordinates.RAType.Degrees);
            mount.Setup(m => m.GetCurrentPosition()).Returns(() => position);
            SimulateMotors(mount, () => position, c => position = c, stopped: stops.Enqueue);
            using var svc = NewService(NewFake(), new HintFollowingSolver(), mount);

            var plan = await svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None).ConfigureAwait(false);
            Assert.That(plan.Direction, Is.EqualTo(direction));
            Assert.That((plan.StepDeg, plan.Turns, plan.MaxSweepDeg), Is.EqualTo((30.0, 2, 60.0)), "the bench's 30° turns, twice");

            await svc.StartAsync(PolarAlignStages.Fine, new PolarAlignStartConfirmation(plan.Direction, plan.NewOrigin), null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("adjusting"));
            Assert.That(stops.Select(c => c.RADegrees).ToArray(), Is.EqualTo(new[] { firstTurnRaDeg, secondTurnRaDeg }).Within(1e-6),
                "two turns from where the mount pointed, the confirmed way; no tilt (already off the axis), no Home, no GoTo");
            NoHomeNoGoTo(mount);
        }

        [Test]
        public async Task The_check_only_plan_bounds_the_sweep_by_the_check_step() {
            var store = NewStore();
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { SeedRotationDeg = 10.0, CheckStepDeg = 15.0 });
            using var svc = NewService(NewFake(), new ScriptedSolver(), NewMount(), store: store);

            var check = await svc.GetStartPlanAsync(PolarAlignStages.Check, CancellationToken.None).ConfigureAwait(false);
            var coarse = await svc.GetStartPlanAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That((check.StepDeg, check.MaxSweepDeg), Is.EqualTo((15.0, 30.0)));
            Assert.That((coarse.Stage, coarse.StepDeg, coarse.MaxSweepDeg), Is.EqualTo(((string?)null, 10.0, 20.0)),
                "#2082 — one measurement per Start: its own two turns, never a check after them");
        }

        // ── the band's readings come from the driver, never the cache ──────────────────────────

        [Test]
        public async Task A_start_whose_driver_read_fails_is_refused_and_never_planned_from_the_cached_placeholder() {
            // A WiFi hiccup fails the status poll and the fresh read together: the cache is then the (0, 0) placeholder.
            // A plan read from it shows a direction and anchors a band that have nothing to do with the mount.
            var mount = new FakeStopMount(tracking: true) { DriverReadsFail = 2 };
            var fake = NewFake();
            var ws = new WsRecorder();
            using var svc = NewStopService(mount, ws, guider: fake);

            var planError = await Assert.ThrowsAsync<RefusalException>(() => svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None));
            var startError = await Assert.ThrowsAsync<RefusalException>(() => svc.StartAsync(
                PolarAlignStages.Fine, new PolarAlignStartConfirmation(PolarAlignDirections.East), null, CancellationToken.None));

            // #2066 — a declared cause the app words in its own language (ADR 0022).
            Assert.That(planError!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignPointingUnknown));
            Assert.That(startError!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignPointingUnknown));
            Assert.That(mount.Log.Where(IsMotion), Is.Empty, string.Join(", ", mount.Log));
            Assert.That(fake.LeaseCalls, Is.Empty, "refused before the camera is taken");
            Assert.That(ws.Count(WsEventCatalog.PolarAlignStarted), Is.Zero);
        }

        [Test]
        public async Task A_stage_whose_driver_read_fails_fails_the_run_and_never_turns_from_the_cached_placeholder() {
            // The plan read the driver; the run's own first reading, where its turns start, fails (a WiFi hiccup). The cache is
            // then the (0, 0) placeholder: a seed anchored there would turn from a pose that has nothing to do with the mount.
            var mount = new FakeStopMount(tracking: true);
            var fake = NewFake();
            fake.LeaseGate = active => {
                if (active) {
                    mount.DriverReadsFail = 1; // after the plan, before the run's first reading
                }
                return Task.CompletedTask;
            };
            var ws = new WsRecorder();
            using var svc = NewStopService(mount, ws, guider: fake);
            var at = mount.Count;

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);

            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.PointingUnknown));
            Assert.That(error.GetProperty("message").GetString(), Does.Contain("no fresh reading of where the mount points"));
            AssertNoMotionAfter(mount, at);
            Assert.That(mount.AtRest, Is.True);
            Assert.That(mount.Tracking, Is.False);
        }

        [Test]
        public async Task A_run_whose_first_reading_is_within_3_degrees_of_the_pole_fails_as_on_axis_and_moves_nothing() {
            // The plan read Dec 86.9° (3.1° from the pole) and the user confirmed it; the mount was moved to Dec 87.5° before
            // the run's own first reading. The run must fail as on_axis before anything moves: nothing tilts it off any more.
            var mount = new FakeStopMount(tracking: true) { DecDeg = 86.9 };
            var fake = NewFake();
            fake.LeaseGate = active => {
                if (active) {
                    mount.DecDeg = 87.5; // after the plan, before the run's first reading
                }
                return Task.CompletedTask;
            };
            var ws = new WsRecorder();
            using var svc = NewStopService(mount, ws, guider: fake);
            var plan = await svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None).ConfigureAwait(false);

            await svc.StartAsync(PolarAlignStages.Fine, new PolarAlignStartConfirmation(plan.Direction, plan.NewOrigin), null, CancellationToken.None).ConfigureAwait(false);
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);

            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo("on_axis"));
            Assert.That(error.GetProperty("message").GetString(), Is.EqualTo(PolarAlignService.OnAxisMessage));
            Assert.That(mount.Log.Where(IsMotion), Is.Empty, "zero motions: " + string.Join(", ", mount.Log));
            Assert.That(mount.AtRest, Is.True);
        }

        [Test]
        public async Task Two_Checks_at_once_from_the_adjust_loop_run_one_check_and_move_nothing_more() {
            // A double tap on Check: the second Start arrives while the first ends the adjust loop. The first's end is held at
            // its mount stop, the loop still adjusting. One Check runs and the other is a no-op accept; both used to end the
            // loop, and each then saw the other's end and failed, so no check ran.
            var mount = new FakeStopMount(tracking: true);
            var ws = new WsRecorder();
            mount.EventCount = () => ws.Events.Count;
            using var svc = NewService(NewFake(), new HintFollowingSolver(), mount.Mock, ws, store: TenDegreeStore());
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            Assert.That((await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false)).State, Is.EqualTo("adjusting"));
            var plan = await svc.GetStartPlanAsync(PolarAlignStages.Check, CancellationToken.None).ConfigureAwait(false);
            var confirmation = new PolarAlignStartConfirmation(plan.Direction, plan.NewOrigin);
            var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            mount.StopGate = () => {
                stopEntered.TrySetResult();
                return stopGate.Task;
            };
            var raBefore = mount.RaDeg;
            var mark = Mark(svc);

            var first = svc.StartAsync(PolarAlignStages.Check, confirmation, null, CancellationToken.None);
            await stopEntered.Task.WaitAsync(SignalTimeout).ConfigureAwait(false);
            var second = svc.StartAsync(PolarAlignStages.Check, confirmation, null, CancellationToken.None);
            await Task.WhenAny(second, Task.Delay(TimeSpan.FromMilliseconds(300))).ConfigureAwait(false);
            stopGate.SetResult();
            await Task.WhenAll(first, second).ConfigureAwait(false); // neither tap fails
            var status = await PollStateAsync(svc, mark, "checked", "failed").ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("checked"));
            var events = ws.Events.Skip(mark).ToArray();
            Assert.That(events.Where(e => e.Type == WsEventCatalog.PolarAlignStarted).Select(e => e.Payload.GetProperty("stage").GetString()),
                Is.EqualTo(OnlyTheCheckStarted), "exactly one check started");
            Assert.That(events.Count(e => e.Type == WsEventCatalog.PolarAlignChecked), Is.EqualTo(1));
            Assert.That(events.Count(e => e.Type == WsEventCatalog.PolarAlignError), Is.Zero);
            Assert.That(Math.Abs(OffsetFrom(raBefore, mount.RaDeg)), Is.EqualTo(2 * 10.0).Within(1.5),
                "the one check's two 10° turns and nothing more: " + string.Join(", ", mount.Log));
        }

        // ── the retired use_home setting ─────────────────────────────────────────────────────

        [Test]
        public void A_profile_saved_with_use_home_still_loads_and_keeps_its_other_polar_align_settings() {
            // #2048 retired use_home. A profile.json written by an older daemon still carries it: it must load as before,
            // and the key is simply ignored (the routine never homes).
            var dir = Path.Combine(Path.GetTempPath(), "ara-use-home-" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            try {
                var first = new FileProfileStore(dir);
                first.PutPolarAlignSettings(first.GetPolarAlignSettings() with { SeedRotationDeg = 12.0, CheckStepDeg = 14.0 });
                var path = Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories).Single(f => File.ReadAllText(f).Contains("\"polar_align\"", StringComparison.Ordinal));
                var json = File.ReadAllText(path);
                var withUseHome = Regex.Replace(json, "\"polar_align\"\\s*:\\s*\\{", m => m.Value + "\"use_home\": false, ", RegexOptions.None, TimeSpan.FromSeconds(1));
                Assert.That(withUseHome, Does.Contain("\"use_home\": false"));
                File.WriteAllText(path, withUseHome);

                var reloaded = new FileProfileStore(dir).GetPolarAlignSettings();

                Assert.That((reloaded.SeedRotationDeg, reloaded.CheckStepDeg), Is.EqualTo((12.0, 14.0)), "the section loaded, not reset to defaults");
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        // The daemon's own HTTP JSON setup (Program.cs).
        private static readonly System.Text.Json.JsonSerializerOptions ApiJson = new() {
            TypeInfoResolver = AraJsonSerializerContext.Default,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
        };

        [Test]
        public void A_polar_align_settings_body_with_use_home_deserializes_and_ignores_it() {
            // An older app PUTs the section with use_home: the daemon's JSON setup must take it, not answer 400.
            var dto = System.Text.Json.JsonSerializer.Deserialize<PolarAlignSettingsDto>(
                "{\"exposure_seconds\": 2.5, \"use_home\": true, \"seed_rotation_deg\": 11}", ApiJson);

            Assert.That(dto, Is.Not.Null);
            Assert.That((dto!.ExposureSeconds, dto.SeedRotationDeg), Is.EqualTo((2.5, 11.0)));
        }
    }
}
