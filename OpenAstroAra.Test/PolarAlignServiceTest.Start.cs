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
using OpenAstroAra.Test.PolarAlign;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// Polar Align starts from the mount's Home position: a mount that can find Home by itself and is not there is refused,
    /// one that cannot is not asked; nothing else about the mount's pose refuses a start (a start at the pole is fine). A
    /// run never sends Find Home or a GoTo and never unparks; its two turns go the way the profile's east_direction says.
    /// </summary>
    public partial class PolarAlignServiceTest {

        /// <summary>NINA's 10° steps for the seed and the check.</summary>
        private static InMemoryProfileStore TenDegreeStore() {
            var store = NewStore();
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { SeedRotationDeg = 10.0, CheckStepDeg = 10.0 });
            return store;
        }

        /// <summary>A fine start; waits for the adjust loop.</summary>
        private static async Task FineRunToAdjustingAsync(PolarAlignService svc) {
            var mark = Mark(svc);
            await svc.StartAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            Assert.That((await PollStateAsync(svc, mark, "adjusting", "failed").ConfigureAwait(false)).State, Is.EqualTo("adjusting"));
        }

        private static double OffsetFrom(double originRaDeg, double raDeg) => ((raDeg - originRaDeg + 540.0) % 360.0) - 180.0;

        // ── a run never homes, slews or unparks ──────────────────────────────────────────────

        [Test]
        public async Task A_run_on_the_fake_mount_never_sends_Find_Home_a_GoTo_or_an_unpark() {
            // The fake mount can find Home and slew; a fine run and a check use its motors only.
            var mount = new FakeStopMount(tracking: true, canFindHome: true);
            var ws = new WsRecorder();
            using var svc = NewService(NewFake(), new MountFollowingSolver(), mount.Mock, ws, store: TenDegreeStore());
            mount.EventCount = () => ws.Events.Count;

            await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            await svc.StartAsync(PolarAlignStages.Check, null, CancellationToken.None).ConfigureAwait(false);
            Assert.That((await PollStateAsync(svc, "checked", "failed").ConfigureAwait(false)).State, Is.EqualTo("checked"));

            var log = mount.Log;
            Assert.That(log.Where(c => c.StartsWith("move primary", StringComparison.Ordinal) && !c.EndsWith(" 0", StringComparison.Ordinal)),
                Is.Not.Empty, "the turns ran on the RA motor");
            Assert.That(log, Does.Not.Contain("findhome").And.Not.Contain("goto").And.Not.Contain("unpark"), string.Join(", ", log));
        }

        // ── Home ─────────────────────────────────────────────────────────────────────────────

        [Test]
        public async Task A_start_away_from_Home_on_a_mount_that_can_find_Home_is_refused_as_not_home_and_moves_nothing() {
            var mount = new FakeStopMount(tracking: true, canFindHome: true) { AtHome = false };
            var fake = NewFake();
            var ws = new WsRecorder();
            using var svc = NewStopService(mount, ws, guider: fake);

            var fine = await Assert.ThrowsAsync<RefusalException>(() => svc.StartAsync(PolarAlignStages.Fine, null, CancellationToken.None));
            var check = await Assert.ThrowsAsync<RefusalException>(() => svc.StartAsync(PolarAlignStages.Check, null, CancellationToken.None));

            Assert.That(fine!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignNotHome));
            Assert.That(fine.Message, Is.EqualTo(PolarAlignService.NotHomeMessage));
            Assert.That(check!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignNotHome));
            Assert.That(mount.Log, Is.Empty, "no command reached the mount: " + string.Join(", ", mount.Log));
            Assert.That(fake.LeaseCalls, Is.Empty, "refused before the camera is taken");
            Assert.That(ws.Count(WsEventCatalog.PolarAlignStarted), Is.Zero);
            Assert.That((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).State, Is.EqualTo("idle"));
            // The refused start gave the mount's motion owner back: once the mount is at Home the next start runs.
            mount.AtHome = true;
            await AdjustingAsync(svc, mount).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
        }

        [Test]
        public async Task A_start_at_Home_on_a_mount_that_can_find_Home_is_accepted() {
            var mount = new FakeStopMount(tracking: true, canFindHome: true) { AtHome = true };
            var ws = new WsRecorder();
            using var svc = NewStopService(mount, ws);

            await AdjustingAsync(svc, mount).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(mount.Log.Count(IsMotion), Is.GreaterThanOrEqualTo(2), "the two turns ran: " + string.Join(", ", mount.Log));
        }

        [Test]
        public async Task A_start_on_a_mount_that_cannot_find_Home_is_accepted_whatever_it_reports_about_Home() {
            // Its user sets it to its home position: Ara has no way to send it there, so it does not ask.
            var mount = new FakeStopMount(tracking: true, canFindHome: false) { AtHome = false };
            var ws = new WsRecorder();
            using var svc = NewStopService(mount, ws);

            await AdjustingAsync(svc, mount).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(mount.Log.Count(IsMotion), Is.GreaterThanOrEqualTo(2), "the two turns ran: " + string.Join(", ", mount.Log));
        }

        [Test]
        public async Task A_start_at_the_pole_is_accepted_and_measures_the_axis_from_the_cameras_small_circle() {
            // Home: the mount reads Dec 90°. The camera looks 0.2° off the RA axis, so its three frames lie on a 0.2° circle.
            var mount = NewMount();
            var position = new Coordinates(0.0, 90.0, Epoch.JNOW, Coordinates.RAType.Degrees);
            mount.Setup(m => m.GetCurrentPosition()).Returns(() => position);
            SimulateMotors(mount, () => position, c => position = c);
            using var svc = NewService(NewFake(), new MountFollowingSolver { DecDeg = 89.8 }, mount, store: TenDegreeStore());

            await svc.StartAsync(PolarAlignStages.Check, null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "checked", "failed").ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("checked"));
            Assert.That(status.CheckResult!.ConeHalfAngleDeg, Is.EqualTo(0.2).Within(0.001));
            Assert.That(status.CheckResult.TurnMismatchDeg, Is.LessThan(0.01), "the frames show the two 10° turns the motor was told");
            // The bench axis is the true pole: only the refraction term is left.
            Assert.That(status.CheckResult.TotalErrorArcmin, Is.LessThan(1.5));
        }

        // ── parked ───────────────────────────────────────────────────────────────────────────

        [Test]
        public async Task A_parked_mount_is_refused_before_anything_moves_and_is_never_unparked() {
            // NINA refuses a parked mount (PolarAlignment.cs 1108-1110).
            var (mount, _) = ParkedMount(unparks: true);
            var fake = NewFake();
            var ws = new WsRecorder();
            using var svc = NewService(fake, new ScriptedSolver(), mount, ws);

            var refusal = await Assert.ThrowsAsync<RefusalException>(() => svc.StartAsync(PolarAlignStages.Fine, null, CancellationToken.None));

            Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignMountParked));
            Assert.That(refusal.Message, Is.EqualTo("Mount is parked — unpark it and send it Home first"));
            mount.Verify(m => m.UnparkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
            mount.Verify(m => m.MoveAxis(It.IsAny<TelescopeAxes>(), It.IsAny<double>()), Times.Never);
            Assert.That(fake.LeaseCalls, Is.Empty, "refused before the camera is taken");
            Assert.That(ws.Count(WsEventCatalog.PolarAlignStarted), Is.Zero);
            Assert.That((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).State, Is.EqualTo("idle"));
        }

        [Test]
        public async Task A_park_that_lands_after_Start_fails_the_run_as_mount_parked_before_anything_moves() {
            // StartAsync saw an unparked mount; the park lands while it takes the guide camera. The run must fail as
            // mount_parked before any turn, and never unpark the mount.
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

            await svc.StartAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);

            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo("mount_parked"));
            Assert.That(error.GetProperty("message").GetString(), Is.EqualTo(PolarAlignService.MountParkedMessage));
            mount.Verify(m => m.UnparkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
            mount.Verify(m => m.MoveAxis(It.IsAny<TelescopeAxes>(), It.Is<double>(rate => rate != 0.0)), Times.Never,
                "no turn: only the exit's MoveAxis 0");
            NoHomeNoGoTo(mount);
        }

        // ── the turn direction ───────────────────────────────────────────────────────────────

        // The profile's east_direction picks the way (null: east in the north), never the mount's pose: a positive motor
        // rate (RA up on the bench mount) for east, a negative one for west.
        [TestCase(null, 30.0, 60.0)]
        [TestCase(true, 30.0, 60.0)]
        [TestCase(false, 330.0, 300.0)]
        public async Task A_start_turns_twice_by_its_step_the_way_the_profiles_east_direction_says(
                bool? eastDirection, double firstTurnRaDeg, double secondTurnRaDeg) {
            var stops = new ConcurrentQueue<Coordinates>();
            var mount = NewMount();
            var position = new Coordinates(0.0, 85.0, Epoch.JNOW, Coordinates.RAType.Degrees);
            mount.Setup(m => m.GetCurrentPosition()).Returns(() => position);
            SimulateMotors(mount, () => position, c => position = c, stopped: stops.Enqueue);
            var store = NewStore();
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { EastDirection = eastDirection });
            using var svc = NewService(NewFake(), new MountFollowingSolver(), mount, store: store);

            await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(stops.Select(c => c.RADegrees).ToArray(), Is.EqualTo(new[] { firstTurnRaDeg, secondTurnRaDeg }).Within(1e-6),
                "the bench's two 30° turns, the profile's way; no Home, no GoTo");
            NoHomeNoGoTo(mount);
        }

        // ── Check from the adjust loop ───────────────────────────────────────────────────────

        [Test]
        public async Task A_Check_from_the_adjust_loop_away_from_Home_is_refused_and_the_loop_runs_on() {
            // After its two turns a mount that can find Home is no longer there: the Check is refused, and the loop it was
            // asked from is left running, the mount untouched.
            var mount = new FakeStopMount(tracking: true, canFindHome: true);
            var ws = new WsRecorder();
            using var svc = NewStopService(mount, ws);
            await AdjustingAsync(svc, mount).ConfigureAwait(false);
            mount.AtHome = false;
            var at = mount.Count;

            var refusal = await Assert.ThrowsAsync<RefusalException>(() => svc.StartAsync(PolarAlignStages.Check, null, CancellationToken.None));

            Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignNotHome));
            Assert.That((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).State, Is.EqualTo("adjusting").Or.EqualTo("paused"));
            Assert.That(mount.Since(at), Is.Empty, "nothing was sent to the mount: " + string.Join(", ", mount.Since(at)));
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
        }

        [Test]
        public async Task Two_Checks_at_once_from_the_adjust_loop_run_one_check_and_move_nothing_more() {
            // A double tap on Check: the second Start arrives while the first ends the adjust loop. The first's end is held at
            // its mount stop, the loop still adjusting. One Check runs and the other is a no-op accept; both used to end the
            // loop, and each then saw the other's end and failed, so no check ran.
            var mount = new FakeStopMount(tracking: true);
            var ws = new WsRecorder();
            mount.EventCount = () => ws.Events.Count;
            using var svc = NewService(NewFake(), new MountFollowingSolver(), mount.Mock, ws, store: TenDegreeStore());
            await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            mount.StopGate = () => {
                stopEntered.TrySetResult();
                return stopGate.Task;
            };
            var raBefore = mount.RaDeg;
            var mark = Mark(svc);

            var first = svc.StartAsync(PolarAlignStages.Check, null, CancellationToken.None);
            await stopEntered.Task.WaitAsync(SignalTimeout).ConfigureAwait(false);
            var second = svc.StartAsync(PolarAlignStages.Check, null, CancellationToken.None);
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

        // ── settings an older profile still carries ──────────────────────────────────────────

        [TestCase("\"use_home\": false, ")]
        [TestCase("\"mount_clearance\": {\"mount-1\": {\"confirmed_reach_deg\": 20.5}}, ")]
        public void A_profile_saved_with_a_removed_polar_align_setting_still_loads_and_keeps_its_other_settings(string removed) {
            // A profile.json written by an older daemon still carries use_home or the per-mount clearance: it must load as
            // before, and the key is simply ignored.
            var dir = Path.Combine(Path.GetTempPath(), "ara-use-home-" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            try {
                var first = new FileProfileStore(dir);
                first.PutPolarAlignSettings(first.GetPolarAlignSettings() with { SeedRotationDeg = 12.0, CheckStepDeg = 14.0 });
                var path = Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories).Single(f => File.ReadAllText(f).Contains("\"polar_align\"", StringComparison.Ordinal));
                var json = File.ReadAllText(path);
                var withRemoved = Regex.Replace(json, "\"polar_align\"\\s*:\\s*\\{", m => m.Value + removed, RegexOptions.None, TimeSpan.FromSeconds(1));
                Assert.That(withRemoved, Does.Contain(removed));
                File.WriteAllText(path, withRemoved);

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
