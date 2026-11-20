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
    /// unparks; and only after the user confirmed the start and the RA turn direction. The later stages' turns keep to the
    /// band the user confirmed.
    /// </summary>
    public partial class PolarAlignServiceTest {

        // ── a run never homes, slews or unparks ──────────────────────────────────────────────

        [Test]
        public async Task A_run_on_the_fake_mount_never_sends_Find_Home_a_GoTo_or_an_unpark() {
            // The fake mount can find Home and slew; the run, fine through check, uses its motors only.
            var mount = new FakeStopMount(tracking: true, canFindHome: true);
            var ws = new WsRecorder();
            using var svc = NewService(NewFake(), new HintFollowingSolver(), mount.Mock, ws, store: NewStore());
            mount.EventCount = () => ws.Events.Count;

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            Assert.That((await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false)).State, Is.EqualTo("adjusting"));
            await svc.NextAsync(null, CancellationToken.None).ConfigureAwait(false);
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
            Assert.That((coarse.Stage, coarse.StepDeg, coarse.MaxSweepDeg), Is.EqualTo(((string?)null, 10.0, 30.0)),
                "a coarse start can move on to the check: its band holds the check's turns too");
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

            var planError = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None));
            var startError = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartAsync(
                PolarAlignStages.Fine, new PolarAlignStartConfirmation(PolarAlignDirections.East), null, CancellationToken.None));

            Assert.That(planError!.Message, Does.Contain("did not report where it points"));
            Assert.That(startError!.Message, Does.Contain("did not report where it points"));
            Assert.That(mount.Log.Where(IsMotion), Is.Empty, string.Join(", ", mount.Log));
            Assert.That(fake.LeaseCalls, Is.Empty, "refused before the camera is taken");
            Assert.That(ws.Count(WsEventCatalog.PolarAlignStarted), Is.Zero);
        }

        [Test]
        public async Task A_stage_whose_driver_read_fails_fails_the_run_and_never_turns_from_the_cached_placeholder() {
            // After the fine stage's turns the axis sits at the band's far edge, so the check turns back. Read from the
            // placeholder, the axis would look back at the start and the check would turn further away, out of the band.
            var mount = new FakeStopMount(tracking: true);
            var ws = new WsRecorder();
            using var svc = NewStopService(mount, ws);
            await AdjustingAsync(svc, mount).ConfigureAwait(false);
            var at = mount.Count;

            mount.DriverReadsFail = 2; // the check's tilt read (it falls back to the cache: no tilt) and its band read
            await svc.NextAsync(null, CancellationToken.None).ConfigureAwait(false);
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);

            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo("mount_fault"));
            Assert.That(error.GetProperty("message").GetString(), Does.Contain("did not report where it points"));
            AssertNoMotionAfter(mount, at);
            Assert.That(mount.AtRest, Is.True);
            Assert.That(mount.Tracking, Is.False);
        }

        // ── the band the stages keep to ──────────────────────────────────────────────────────

        // awaySign +1 (east). Columns: offset from the start, this stage's two turns, the band → direction, turn back first.
        [TestCase(0.0, 20.0, 20.0, 1.0, 0.0, TestName = "The first stage turns away, the way the user confirmed")]
        [TestCase(20.3, 20.0, 20.0, -1.0, 0.0, TestName = "A stage after one that turned away turns back over the arc")]
        [TestCase(18.5, 20.0, 20.0, -1.0, 0.0, TestName = "Tracking drift between stages still turns back, ending within the tolerance")]
        [TestCase(0.4, 20.0, 20.0, 1.0, 0.0, TestName = "A stage back at the start turns away again")]
        [TestCase(10.0, 60.0, 60.0, 1.0, -10.0, TestName = "A long check after short turns goes back to the start first, then away")]
        [TestCase(-5.0, 20.0, 20.0, 1.0, 0.0, TestName = "Past the start toward the meridian, the stage turns away into the band")]
        [TestCase(70.0, 20.0, 60.0, -1.0, 0.0, TestName = "Beyond the band, the stage turns back toward it")]
        public void PlanStageTurns_keeps_every_stage_inside_the_confirmed_band(
                double offsetDeg, double travelDeg, double maxSweepDeg, double dirSign, double returnFirstDeg) {
            var (dir, back) = PolarAlignService.PlanStageTurns(1.0, offsetDeg, travelDeg, maxSweepDeg);
            Assert.That((dir, back), Is.EqualTo((dirSign, returnFirstDeg)));
            // Wherever it ends, the stage's sweep stays inside the band, within the tolerance.
            var from = offsetDeg + back;
            var to = from + (dir * travelDeg);
            if (offsetDeg is >= -PolarAlignService.BandToleranceDeg and <= 60.0) {
                Assert.That(Math.Min(from, to), Is.GreaterThanOrEqualTo(-PolarAlignService.BandToleranceDeg));
                Assert.That(Math.Max(from, to), Is.LessThanOrEqualTo(maxSweepDeg + PolarAlignService.BandToleranceDeg));
            }
        }

        [Test]
        public void PlanStageTurns_works_in_RA_reading_terms_for_a_westward_run() {
            Assert.That(PolarAlignService.PlanStageTurns(-1.0, 0.0, 20.0, 20.0), Is.EqualTo((-1.0, 0.0)), "away = west = RA down");
            Assert.That(PolarAlignService.PlanStageTurns(-1.0, 20.0, 20.0, 20.0), Is.EqualTo((1.0, 0.0)), "back = east = RA up");
            Assert.That(PolarAlignService.PlanStageTurns(-1.0, 10.0, 60.0, 60.0), Is.EqualTo((-1.0, 10.0)), "back to the start is RA up");
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
