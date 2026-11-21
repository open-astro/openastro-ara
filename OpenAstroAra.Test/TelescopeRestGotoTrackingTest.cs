#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using AlignmentMode = ASCOM.Common.DeviceInterfaces.AlignmentMode;
using Moq;
using NUnit.Framework;
using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Equipment.MyGuider;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Alpaca;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #2073 — a REST GoTo (the Mount panel's) turns tracking on first, as the mediator GoTo and Park
    /// already did: an ITelescopeV4 mount (AlpacaBridge) refuses an equatorial goto with Tracking off,
    /// for example right after Find Home. A GoTo the mount still refuses is logged with the driver's
    /// reason in the message, not only in the exception.
    /// </summary>
    [TestFixture]
    [Category("IO")] // loopback scripted Alpaca device
    public class TelescopeRestGotoTrackingTest {

        private const string GotoVerb = "slewtocoordinatesasync";
        private const string TrackingRefusal = "SlewToCoordinatesAsync requires Tracking to be true";

        private static DiscoveredDeviceDto Device(ScriptedAlpacaDevice box) => new(
            UniqueId: "mount-under-test", Name: "Bench mount", Type: DeviceType.Telescope,
            HostName: box.BaseUri.Host, IpAddress: box.BaseUri.Host, IpPort: box.BaseUri.Port,
            AlpacaDeviceNumber: 0, UseHttps: false);

        private static Func<string, string?> Mount(bool tracking) => path =>
            path.EndsWith("/connected", StringComparison.Ordinal) ? "true"
            : path.EndsWith("/slewing", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/tracking", StringComparison.Ordinal) ? (tracking ? "true" : "false")
            : path.EndsWith("/atpark", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/athome", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/rightascension", StringComparison.Ordinal) ? "6.0"
            : path.EndsWith("/declination", StringComparison.Ordinal) ? "6.0"
            : null;

        private static async Task WaitForAsync(Func<bool> condition, string failure) {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline) {
                if (condition()) {
                    return;
                }
                await Task.Delay(50);
            }
            Assert.Fail(failure);
        }

        private static int PutIndex(ScriptedAlpacaDevice box, string verb, Func<string, bool>? body = null) {
            var puts = box.Puts.ToArray();
            for (var i = 0; i < puts.Length; i++) {
                if (puts[i].Path.EndsWith("/" + verb, StringComparison.Ordinal) && (body is null || body(puts[i].Body))) {
                    return i;
                }
            }
            return -1;
        }

        private static bool TrackingOn(string body) => body.Contains("Tracking=True", StringComparison.OrdinalIgnoreCase);

        private static async Task<TelescopeService> ConnectedAsync(ScriptedAlpacaDevice box, WarningLog? log = null, Func<IGuiderMediator?>? guider = null) {
            var svc = new TelescopeService(logger: log, guiderResolver: guider);
            await svc.ConnectAsync(new ConnectRequestDto(Device(box)), null, CancellationToken.None);
            await WaitForAsync(() => svc.GetAsync(CancellationToken.None).Result?.State == EquipmentConnectionState.Connected, "never connected");
            return svc;
        }

        [Test]
        public async Task A_goto_with_tracking_off_turns_tracking_on_before_the_slew() {
            await using var box = ScriptedAlpacaDevice.Start(Mount(tracking: false));
            using var svc = await ConnectedAsync(box);

            await svc.SlewAsync(new SlewRequestDto(6.0, 6.0), idempotencyKey: null, CancellationToken.None);

            await WaitForAsync(() => PutIndex(box, GotoVerb) >= 0, "the mount never received the goto");
            var tracking = PutIndex(box, "tracking", TrackingOn);
            Assert.That(tracking, Is.GreaterThanOrEqualTo(0), "a Tracking=true write reached the mount");
            Assert.That(tracking, Is.LessThan(PutIndex(box, GotoVerb)), "tracking is turned on before the goto");
        }

        [Test]
        public async Task A_goto_with_tracking_on_sends_no_tracking_write() {
            await using var box = ScriptedAlpacaDevice.Start(Mount(tracking: true));
            using var svc = await ConnectedAsync(box);

            await svc.SlewAsync(new SlewRequestDto(6.0, 6.0), idempotencyKey: null, CancellationToken.None);

            await WaitForAsync(() => PutIndex(box, GotoVerb) >= 0, "the mount never received the goto");
            Assert.That(PutIndex(box, "tracking"), Is.EqualTo(-1), "a mount already tracking is not written to");
        }

        [Test]
        public async Task A_refused_tracking_write_still_sends_the_goto() {
            await using var box = ScriptedAlpacaDevice.Start(Mount(tracking: false),
                putResponder: path => path.EndsWith("/tracking", StringComparison.Ordinal) ? ScriptedAlpacaDevice.Error(0x40B, "tracking cannot be set") : null);
            using var svc = await ConnectedAsync(box);

            await svc.SlewAsync(new SlewRequestDto(6.0, 6.0), idempotencyKey: null, CancellationToken.None);

            await WaitForAsync(() => PutIndex(box, GotoVerb) >= 0, "a refused tracking write must not stop the goto attempt");
        }

        [Test]
        public async Task A_refused_goto_logs_the_driver_reason() {
            var log = new WarningLog();
            await using var box = ScriptedAlpacaDevice.Start(Mount(tracking: true),
                putResponder: path => path.EndsWith("/" + GotoVerb, StringComparison.Ordinal) ? ScriptedAlpacaDevice.Error(0x40B, TrackingRefusal) : null);
            using var svc = await ConnectedAsync(box, log);

            await svc.SlewAsync(new SlewRequestDto(6.0, 6.0), idempotencyKey: null, CancellationToken.None);

            await WaitForAsync(() => log.Messages.Any(m => m.Contains("slew to RA", StringComparison.Ordinal)), "the refused goto was never logged");
            var line = log.Messages.First(m => m.Contains("slew to RA", StringComparison.Ordinal));
            Assert.That(line, Does.Contain(TrackingRefusal), "the one-line message carries the driver's reason");
        }

        [Test]
        public async Task A_stop_that_lands_after_the_goto_was_accepted_drops_the_goto() {
            // The GoTo waits for the guider to stop (up to 3 s) and then for the tracking round trips; a Stop
            // Mount or emergency stop (AbortSlew, then Tracking off) in that window must not be undone by the
            // GoTo turning tracking back on and moving the mount after it.
            var log = new WarningLog();
            await using var box = ScriptedAlpacaDevice.Start(Mount(tracking: false));
            var stopAsked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var guider = new Mock<IGuiderMediator>();
            guider.Setup(g => g.GetInfo()).Returns(new GuiderInfo { Connected = true });
            guider.Setup(g => g.StopGuiding(It.IsAny<CancellationToken>())).Returns(() => {
                stopAsked.TrySetResult();
                return stopDone.Task;
            });
            using var svc = await ConnectedAsync(box, log, () => guider.Object);

            var slew = svc.SlewAsync(new SlewRequestDto(6.0, 6.0), idempotencyKey: null, CancellationToken.None);
            await stopAsked.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await svc.AbortSlewAsync(CancellationToken.None);
            stopDone.SetResult(true);
            await slew;

            await WaitForAsync(() => log.Messages.Any(m => m.Contains("dropped", StringComparison.Ordinal)), "the goto was not dropped after the stop");
            Assert.Multiple(() => {
                Assert.That(PutIndex(box, "abortslew"), Is.GreaterThanOrEqualTo(0), "the stop reached the mount");
                Assert.That(PutIndex(box, GotoVerb), Is.EqualTo(-1), "the goto never reaches the mount after the stop");
                Assert.That(PutIndex(box, "tracking"), Is.EqualTo(-1), "tracking is not turned back on after the stop");
            });
        }

        [Test]
        public async Task A_stop_during_the_tracking_write_drops_the_goto_and_turns_tracking_off_again() {
            // The stop lands while the GoTo's own Tracking=true write is on the wire (after the first abort
            // check passed). Stop Mount sends only AbortSlew, so the mount would be left tracking unless the
            // GoTo turns tracking back off; the GoTo itself must not follow.
            var log = new WarningLog();
            TelescopeService? svc = null;
            var armed = 0;
            Task? stop = null;
            await using var box = ScriptedAlpacaDevice.Start(Mount(tracking: false), putResponder: path => {
                if (path.EndsWith("/tracking", StringComparison.Ordinal) && Interlocked.Exchange(ref armed, 0) == 1) {
                    // The abort epoch moves synchronously, before AbortSlewAsync's first await; its AbortSlew
                    // PUT then queues behind this one on the serial scripted device.
                    stop = svc!.AbortSlewAsync(CancellationToken.None);
                }
                return null;
            });
            svc = await ConnectedAsync(box, log);
            using var _ = svc;

            Volatile.Write(ref armed, 1);
            await svc.SlewAsync(new SlewRequestDto(6.0, 6.0), idempotencyKey: null, CancellationToken.None);

            await WaitForAsync(() => log.Messages.Any(m => m.Contains("dropped", StringComparison.Ordinal)), "the goto was not dropped after the stop");
            await WaitForAsync(() => PutIndex(box, "tracking", b => !TrackingOn(b)) >= 0, "tracking was never turned back off");
            await stop!.WaitAsync(TimeSpan.FromSeconds(15));
            var on = PutIndex(box, "tracking", TrackingOn);
            Assert.Multiple(() => {
                Assert.That(on, Is.GreaterThanOrEqualTo(0), "the GoTo's tracking write reached the mount");
                Assert.That(PutIndex(box, "tracking", b => !TrackingOn(b)), Is.GreaterThan(on), "tracking is turned off after the GoTo turned it on");
                Assert.That(PutIndex(box, GotoVerb), Is.EqualTo(-1), "the goto never reaches the mount after the stop");
            });
        }

        [Test]
        public async Task A_stop_that_lands_after_the_park_was_accepted_drops_the_park() {
            // The REST Park has the GoTo's window: it waits for the guider and turns tracking on before the
            // park. A Stop Mount or emergency stop in that window must not be undone by it.
            var log = new WarningLog();
            await using var box = ScriptedAlpacaDevice.Start(Mount(tracking: false));
            var stopAsked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var guider = new Mock<IGuiderMediator>();
            guider.Setup(g => g.GetInfo()).Returns(new GuiderInfo { Connected = true });
            guider.Setup(g => g.StopGuiding(It.IsAny<CancellationToken>())).Returns(() => {
                stopAsked.TrySetResult();
                return stopDone.Task;
            });
            using var svc = await ConnectedAsync(box, log, () => guider.Object);

            var park = svc.ParkAsync(new ParkRequestDto(), idempotencyKey: null, CancellationToken.None);
            await stopAsked.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await svc.AbortSlewAsync(CancellationToken.None);
            stopDone.SetResult(true);
            await park;

            await WaitForAsync(() => log.Messages.Any(m => m.Contains("park dropped", StringComparison.Ordinal)), "the park was not dropped after the stop");
            Assert.Multiple(() => {
                Assert.That(PutIndex(box, "abortslew"), Is.GreaterThanOrEqualTo(0), "the stop reached the mount");
                Assert.That(PutIndex(box, "park"), Is.EqualTo(-1), "the park never reaches the mount after the stop");
                Assert.That(PutIndex(box, "tracking"), Is.EqualTo(-1), "tracking is not turned back on after the stop");
            });
        }

        // #2107 — two parks in flight (a second Park while the first still waits for the guider): one goes to the mount, the
        // other is dropped with a log line that names the park it lost to (its 202 stands), and no AbortSlew: neither park
        // aborts the other.
        [Test]
        public async Task Two_parks_in_flight_send_one_park_and_name_the_dropped_one() {
            var log = new WarningLog();
            await using var box = ScriptedAlpacaDevice.Start(Mount(tracking: true));
            var asked = 0;
            var bothAsked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var guider = new Mock<IGuiderMediator>();
            guider.Setup(g => g.GetInfo()).Returns(new GuiderInfo { Connected = true });
            guider.Setup(g => g.StopGuiding(It.IsAny<CancellationToken>())).Returns(() => {
                if (Interlocked.Increment(ref asked) == 2) {
                    bothAsked.TrySetResult();
                }
                return stopDone.Task;
            });
            using var svc = await ConnectedAsync(box, log, () => guider.Object);
            var before = svc.AbortEpoch;

            var first = svc.ParkAsync(new ParkRequestDto(), idempotencyKey: null, CancellationToken.None);
            var second = svc.ParkAsync(new ParkRequestDto(), idempotencyKey: null, CancellationToken.None);
            await bothAsked.Task.WaitAsync(TimeSpan.FromSeconds(15));
            stopDone.SetResult(true);
            await first;
            await second;

            await WaitForAsync(() => PutIndex(box, "park") >= 0, "no park reached the mount");
            await WaitForAsync(() => log.Messages.Any(m => m.Contains("park dropped", StringComparison.Ordinal)), "the other park was not dropped");
            await Task.Delay(200);
            Assert.Multiple(() => {
                Assert.That(box.Puts.Count(p => p.Path.EndsWith("/park", StringComparison.Ordinal)), Is.EqualTo(1), "one park reaches the mount");
                Assert.That(PutIndex(box, "abortslew"), Is.EqualTo(-1), "neither park aborts the other");
                // One park counts as a stop; the other either found that count (it read the count first) or was requested
                // after it and counted its own, which drops the first: both ways one park goes out.
                Assert.That(svc.AbortReasonSince(before, except: null), Is.EqualTo(MountStopReasons.Park));
                Assert.That(log.Messages, Has.Some.Contains("park dropped: the mount was stopped (a park)"), "the dropped park says what took the mount");
            });
        }

        // #2066 — a Find Home has the park's window (it waits for the guider before the homing): Emergency Stop's latch, or a
        // Stop Mount, that lands in it must not be followed by the homing.
        [TestCase("emergency_stop")]
        [TestCase("stop_mount")]
        public async Task A_stop_that_lands_after_the_find_home_was_accepted_drops_the_homing(string stop) {
            var log = new WarningLog();
            await using var box = ScriptedAlpacaDevice.Start(Mount(tracking: false));
            var stopAsked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var guider = new Mock<IGuiderMediator>();
            guider.Setup(g => g.GetInfo()).Returns(new GuiderInfo { Connected = true });
            guider.Setup(g => g.StopGuiding(It.IsAny<CancellationToken>())).Returns(() => {
                stopAsked.TrySetResult();
                return stopDone.Task;
            });
            using var svc = await ConnectedAsync(box, log, () => guider.Object);

            var home = svc.FindHomeAsync(idempotencyKey: null, CancellationToken.None);
            await stopAsked.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (stop == "emergency_stop") {
                svc.FenceMotion("emergency_stop");
            } else {
                await svc.AbortSlewAsync(CancellationToken.None);
            }
            stopDone.SetResult(true);
            await home;

            await WaitForAsync(() => log.Messages.Any(m => m.Contains("Find Home dropped", StringComparison.Ordinal)), "the homing was not dropped after the stop");
            Assert.That(PutIndex(box, "findhome"), Is.EqualTo(-1), "the homing never reaches the mount after the stop");
        }

        // #2066 (ADR rule 3, #2048 ARA-2) — the GoTo's PUT is slow to reach the mount, and a stop lands meanwhile (Emergency
        // Stop's latch; its AbortSlew, or Stop Mount's, can reach the mount before the GoTo does). The mount would then slew
        // after the stop: the GoTo is aborted again once its PUT returns.
        [Test]
        public async Task Abort_racing_a_delayed_slew_PUT_aborts_again() {
            var log = new WarningLog();
            TelescopeService? svc = null;
            await using var box = ScriptedAlpacaDevice.Start(Mount(tracking: true), putResponder: path => {
                if (path.EndsWith("/" + GotoVerb, StringComparison.Ordinal)) {
                    Thread.Sleep(200); // the PUT on the wire
                    svc!.FenceMotion("emergency_stop");
                }
                return null;
            });
            svc = await ConnectedAsync(box, log);
            using var _ = svc;

            await svc.SlewAsync(new SlewRequestDto(6.0, 6.0), idempotencyKey: null, CancellationToken.None);

            await WaitForAsync(() => PutIndex(box, "abortslew") >= 0, "the GoTo that raced the stop was never aborted again");
            Assert.Multiple(() => {
                Assert.That(PutIndex(box, GotoVerb), Is.GreaterThanOrEqualTo(0), "the GoTo reached the mount");
                Assert.That(PutIndex(box, "abortslew"), Is.GreaterThan(PutIndex(box, GotoVerb)), "the abort follows the GoTo");
                Assert.That(log.Messages, Has.Some.Contains("aborted again"));
            });
        }

        [TestCase("park")]
        [TestCase("findhome")]
        public async Task A_park_or_find_home_after_a_raced_GoTo_is_not_aborted_by_it(string later) {
            var log = new WarningLog();
            TelescopeService? svc = null;
            await using var box = ScriptedAlpacaDevice.Start(Mount(tracking: true), putResponder: path => {
                if (path.EndsWith("/" + GotoVerb, StringComparison.Ordinal)) {
                    Thread.Sleep(200); // the PUT on the wire
                    svc!.FenceMotion("user_request"); // a Stop Mount, then a park or Find Home before the PUT returns
                    svc.FenceMotion(later);
                }
                return null;
            });
            svc = await ConnectedAsync(box, log);
            using var _ = svc;

            await svc.SlewAsync(new SlewRequestDto(6.0, 6.0), idempotencyKey: null, CancellationToken.None);

            await WaitForAsync(() => log.Messages.Any(m => m.Contains("aborted again", StringComparison.Ordinal)), "the raced GoTo never finished");
            Assert.That(PutIndex(box, "abortslew"), Is.LessThan(0), "the GoTo's re-abort would stop the " + later + " that came after it");
        }

        // #2066 — the latest count decides: a park or Find Home that a Stop Mount then stopped does not shield the GoTo, which
        // would otherwise slew after the user's last stop.
        [TestCase("park")]
        [TestCase("findhome")]
        public async Task A_stop_after_a_park_still_aborts_a_raced_GoTo(string earlier) {
            var log = new WarningLog();
            TelescopeService? svc = null;
            await using var box = ScriptedAlpacaDevice.Start(Mount(tracking: true), putResponder: path => {
                if (path.EndsWith("/" + GotoVerb, StringComparison.Ordinal)) {
                    Thread.Sleep(200); // the PUT on the wire
                    svc!.FenceMotion(earlier); // a park or Find Home, then a Stop Mount before the PUT returns
                    svc.FenceMotion("user_request");
                }
                return null;
            });
            svc = await ConnectedAsync(box, log);
            using var _ = svc;

            await svc.SlewAsync(new SlewRequestDto(6.0, 6.0), idempotencyKey: null, CancellationToken.None);

            await WaitForAsync(() => PutIndex(box, "abortslew") >= 0, "the GoTo that raced the last stop was never aborted again");
            Assert.Multiple(() => {
                Assert.That(PutIndex(box, "abortslew"), Is.GreaterThan(PutIndex(box, GotoVerb)), "the abort follows the GoTo");
                Assert.That(log.Messages, Has.Some.Contains("a stop (user_request) landed"), "the abort is the stop's, not the " + earlier + "'s");
            });
        }

        // #2066 — a Stop Mount lands while GoTo A's PUT is on the wire, and the user sends GoTo B before that PUT returns. A's
        // second abort would stop B, the user's newer move: A is not aborted again.
        [Test]
        public async Task A_newer_GoTo_after_the_stop_is_not_aborted_by_the_raced_GoTo() {
            var log = new WarningLog();
            TelescopeService? svc = null;
            var gotos = 0;
            await using var box = ScriptedAlpacaDevice.Start(Mount(tracking: true), putResponder: path => {
                if (path.EndsWith("/" + GotoVerb, StringComparison.Ordinal) && Interlocked.Increment(ref gotos) == 1) {
                    Thread.Sleep(200); // A's PUT on the wire
                    svc!.FenceMotion("user_request"); // a Stop Mount
                    // GoTo B is accepted before A's PUT returns (the pose check is cached and there is no guider, so
                    // nothing reaches this busy device yet); its own PUT follows A's.
                    svc.SlewAsync(new SlewRequestDto(7.0, 7.0), idempotencyKey: null, CancellationToken.None).GetAwaiter().GetResult();
                }
                return null;
            });
            svc = await ConnectedAsync(box, log);
            using var _ = svc;

            await svc.SlewAsync(new SlewRequestDto(6.0, 6.0), idempotencyKey: null, CancellationToken.None);

            await WaitForAsync(() => log.Messages.Any(m => m.Contains("aborted again", StringComparison.Ordinal)), "the raced GoTo never finished");
            await WaitForAsync(() => box.Puts.Count(p => p.Path.EndsWith("/" + GotoVerb, StringComparison.Ordinal)) >= 2, "GoTo B never reached the mount");
            Assert.Multiple(() => {
                Assert.That(PutIndex(box, "abortslew"), Is.LessThan(0), "the raced GoTo's second abort would stop GoTo B");
                Assert.That(log.Messages, Has.None.Contains("the mount was aborted again"));
            });
        }

        // #2066 — the same race, but the mount refuses GoTo B: B moves nothing, so it cannot stand in for A's second abort. A
        // must still be aborted, or the mount slews to A after the user's stop.
        [Test]
        public async Task A_newer_GoTo_the_mount_refuses_still_leaves_the_raced_GoTo_aborted() {
            var log = new WarningLog();
            TelescopeService? svc = null;
            var gotos = 0;
            await using var box = ScriptedAlpacaDevice.Start(Mount(tracking: true), putResponder: path => {
                if (!path.EndsWith("/" + GotoVerb, StringComparison.Ordinal)) {
                    return null;
                }
                if (Interlocked.Increment(ref gotos) == 1) {
                    Thread.Sleep(200); // A's PUT on the wire
                    svc!.FenceMotion("user_request"); // a Stop Mount
                    svc.SlewAsync(new SlewRequestDto(7.0, 7.0), idempotencyKey: null, CancellationToken.None).GetAwaiter().GetResult();
                    return null;
                }
                return ScriptedAlpacaDevice.ErrorPrefix + "target is below the mount's horizon limit"; // B refused
            });
            svc = await ConnectedAsync(box, log);
            using var _ = svc;

            await svc.SlewAsync(new SlewRequestDto(6.0, 6.0), idempotencyKey: null, CancellationToken.None);

            await WaitForAsync(() => log.Messages.Any(m => m.Contains("failed", StringComparison.Ordinal)), "GoTo B was never refused");
            await WaitForAsync(() => PutIndex(box, "abortslew") >= 0, "the raced GoTo was left slewing after GoTo B failed");
            var gotoB = box.Puts.ToArray().Select((p, i) => (p, i)).Last(x => x.p.Path.EndsWith("/" + GotoVerb, StringComparison.Ordinal)).i;
            Assert.That(PutIndex(box, "abortslew"), Is.GreaterThan(gotoB), "the abort follows GoTo B's refusal");
        }

        // #2066 — GoTo B, sent after the stop, is still waiting for the guider to stop when A's PUT returns. A's second abort
        // goes out at once (the mount must not carry on to A while B waits), and B's PUT waits for it, so it cannot stop B.
        [Test]
        public async Task A_GoTo_still_stopping_the_guider_is_not_aborted_by_the_raced_GoTo() {
            var log = new WarningLog();
            TelescopeService? svc = null;
            Task<OperationAcceptedDto>? slewB = null;
            var gotos = 0;
            var guiderStops = 0;
            var stopDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var guider = new Mock<IGuiderMediator>();
            guider.Setup(g => g.GetInfo()).Returns(new GuiderInfo { Connected = true });
            guider.Setup(g => g.StopGuiding(It.IsAny<CancellationToken>()))
                .Returns(() => Interlocked.Increment(ref guiderStops) == 1 ? Task.FromResult(true) : stopDone.Task); // A's; B's waits
            await using var box = ScriptedAlpacaDevice.Start(Mount(tracking: true), putResponder: path => {
                if (path.EndsWith("/" + GotoVerb, StringComparison.Ordinal) && Interlocked.Increment(ref gotos) == 1) {
                    Thread.Sleep(200); // A's PUT on the wire
                    svc!.FenceMotion("user_request"); // a Stop Mount
                    slewB = svc.SlewAsync(new SlewRequestDto(7.0, 7.0), idempotencyKey: null, CancellationToken.None); // held in the guider stop
                }
                return null;
            });
            svc = await ConnectedAsync(box, log, () => guider.Object);
            using var _ = svc;

            await svc.SlewAsync(new SlewRequestDto(6.0, 6.0), idempotencyKey: null, CancellationToken.None);

            await WaitForAsync(() => log.Messages.Any(m => m.Contains("aborted again", StringComparison.Ordinal)), "the raced GoTo never finished");
            stopDone.SetResult(true);
            await slewB!.WaitAsync(TimeSpan.FromSeconds(15));
            await WaitForAsync(() => box.Puts.Count(p => p.Path.EndsWith("/" + GotoVerb, StringComparison.Ordinal)) >= 2, "GoTo B never reached the mount");
            AssertAbortedBetweenTheGoTos(box, log);
        }

        // #2066 — the same race with a sequencer (mediator) slew sent after the stop: it is not stopped by A's second abort.
        [Test]
        public async Task A_sequencer_slew_after_the_stop_is_not_aborted_by_the_raced_GoTo() {
            var log = new WarningLog();
            TelescopeService? svc = null;
            Task<bool>? slewB = null;
            var gotos = 0;
            var mount = Mount(tracking: true);
            await using var box = ScriptedAlpacaDevice.Start(
                // J2000, not JNOW: GetInfo reports JNOW for a system not read yet too, so only J2000 shows the read landed. Unread,
                // the slew reads it itself, an await that lets A's PUT return before the slew reaches its send.
                path => path.EndsWith("/equatorialsystem", StringComparison.Ordinal) ? "2" : mount(path),
                putResponder: path => {
                    if (path.EndsWith("/" + GotoVerb, StringComparison.Ordinal) && Interlocked.Increment(ref gotos) == 1) {
                        Thread.Sleep(200); // A's PUT on the wire
                        svc!.FenceMotion("user_request"); // a Stop Mount
                        // The sequencer's slew goes out before A's PUT returns: everything it reads first is cached, so it
                        // reaches its send synchronously; its PUT follows A's.
                        slewB = svc.SlewToCoordinatesAsync(new Coordinates(Angle.ByHours(6.0), Angle.ByDegree(6.0), Epoch.J2000), CancellationToken.None);
                    }
                    return null;
                });
            svc = await ConnectedAsync(box, log);
            using var _ = svc;
            await WaitForAsync(() => svc.GetInfo().EquatorialSystem == Epoch.J2000, "the mount's coordinate system was never read");

            await svc.SlewAsync(new SlewRequestDto(6.0, 6.0), idempotencyKey: null, CancellationToken.None);

            await WaitForAsync(() => log.Messages.Any(m => m.Contains("aborted again", StringComparison.Ordinal)), "the raced GoTo never finished");
            await WaitForAsync(() => slewB is not null, "the sequencer slew was never sent");
            Assert.That(await slewB!.WaitAsync(TimeSpan.FromSeconds(15)), Is.True, "the sequencer slew arrives");
            Assert.Multiple(() => {
                Assert.That(PutIndex(box, "abortslew"), Is.LessThan(0), "the raced GoTo's second abort would stop the sequencer slew");
                Assert.That(log.Messages, Has.None.Contains("the mount was aborted again"));
            });
        }

        // #2066 — the race with B held in its checks: the stop lands while A's PUT is on the wire, B starts after it, and the
        // mount's position must be read again before B moves (as after a reconnect). B's position read is held until A's PUT
        // has returned and A has decided whether to abort again (its log line); B then goes on, and when A aborts again, B
        // is given the chance to reach the mount before A's AbortSlew does.
        private sealed class HeldChecksRace : IAsyncDisposable {
            public readonly WarningLog Log = new();
            public readonly ManualResetEventSlim Gate = new(false);
            public ScriptedAlpacaDevice Box = null!;
            public TelescopeService Svc = null!;
            public Task<bool>? MoveB;
            private int _gotos;
            private int _holding;

            public static async Task<HeldChecksRace> StartAsync(Func<TelescopeService, Task<bool>> startB, bool systemReads = true) {
                var race = new HeldChecksRace();
                var mount = Mount(tracking: true);
                race.Box = ScriptedAlpacaDevice.Start(
                    path => {
                        if (path.EndsWith("/equatorialsystem", StringComparison.Ordinal)) {
                            return systemReads ? "1" : ScriptedAlpacaDevice.Error("EquatorialSystem unreadable"); // "1" = JNOW
                        }
                        if (path.EndsWith("/declination", StringComparison.Ordinal) && Volatile.Read(ref race._holding) == 1) {
                            race.Gate.Wait(TimeSpan.FromSeconds(15)); // B's position read, held until A has decided
                        }
                        return mount(path);
                    },
                    putResponder: path => {
                        if (path.EndsWith("/" + GotoVerb, StringComparison.Ordinal) && Interlocked.Increment(ref race._gotos) == 1) {
                            Thread.Sleep(200); // A's PUT on the wire
                            race.Svc.FenceMotion("user_request"); // a Stop Mount
                            race.Svc.PoseTrust.BeginSession(race.Svc.PoseTrust.DeviceId, restoredReason: null); // read again first
                            Volatile.Write(ref race._holding, 1);
                            race.MoveB = startB(race.Svc); // returns at its held read; this device answers it after A's PUT
                        }
                        return null;
                    });
                race.Log.OnWarning = message => {
                    if (!message.Contains("the mount was aborted again", StringComparison.Ordinal)) {
                        return;
                    }
                    // A has decided to abort again and is about to send it: B's read answers now, and B is given time to
                    // reach the mount first if nothing holds its PUT back.
                    race.Gate.Set();
                    SpinWait.SpinUntil(() => race.Box.Puts.Count(p => p.Path.EndsWith("/" + GotoVerb, StringComparison.Ordinal)) >= 2, TimeSpan.FromSeconds(1));
                };
                race.Svc = await ConnectedAsync(race.Box, race.Log);
                return race;
            }

            /// <summary>Sends GoTo A, waits for A's decision, then lets B's position read answer.</summary>
            public async Task<Task<bool>> RunAsync() {
                await Svc.SlewAsync(new SlewRequestDto(6.0, 6.0), idempotencyKey: null, CancellationToken.None);
                await WaitForAsync(() => Log.Messages.Any(m => m.Contains("aborted again", StringComparison.Ordinal)), "the raced GoTo never finished");
                Assert.That(MoveB, Is.Not.Null, "B started while A's PUT was on the wire");
                Gate.Set();
                return MoveB!;
            }

            public async ValueTask DisposeAsync() {
                Gate.Set();
                Svc?.Dispose();
                if (Box is not null) {
                    await Box.DisposeAsync();
                }
                Gate.Dispose();
            }
        }

        private static readonly Coordinates SlewBTarget = new(Angle.ByHours(6.0), Angle.ByDegree(6.0), Epoch.JNOW);

        // #2066 — the raced GoTo's second abort, and B's PUT after it: A's abort lies between A's GoTo and B's, and A said it
        // aborted the mount again (not that it kept the abort back for B).
        private static void AssertAbortedBetweenTheGoTos(ScriptedAlpacaDevice box, WarningLog log) {
            var puts = box.Puts.ToArray();
            var gotos = puts.Select((p, i) => (p, i)).Where(x => x.p.Path.EndsWith("/" + GotoVerb, StringComparison.Ordinal)).Select(x => x.i).ToArray();
            var abort = PutIndex(box, "abortslew");
            Assert.Multiple(() => {
                Assert.That(gotos, Has.Length.EqualTo(2), "both GoTos reached the mount");
                Assert.That(log.Messages, Has.Some.Contains("the mount was aborted again"), "the raced GoTo was aborted again at once");
                Assert.That(abort, Is.GreaterThan(gotos[0]), "the abort follows the raced GoTo");
                Assert.That(abort, Is.LessThan(gotos.Length > 1 ? gotos[1] : int.MaxValue), "the newer move's PUT waits for the abort");
            });
        }

        // #2066 — a sequencer slew sent after the stop is still reading the mount when A's PUT returns. A's second abort goes
        // out at once: kept back for B, it would leave the mount driving on to A for as long as B's checks take (each read can
        // wait about 10 s). B's PUT waits for that abort, so the abort cannot stop B.
        [Test]
        public async Task A_sequencer_slew_still_in_its_checks_is_not_aborted_by_the_raced_GoTo() {
            await using var race = await HeldChecksRace.StartAsync(svc => svc.SlewToCoordinatesAsync(SlewBTarget, CancellationToken.None));

            var slewB = await race.RunAsync();

            Assert.That(await slewB.WaitAsync(TimeSpan.FromSeconds(15)), Is.True, "the sequencer slew arrives");
            AssertAbortedBetweenTheGoTos(race.Box, race.Log);
        }

        // #2066 — the same with a meridian flip still in its checks.
        [Test]
        public async Task A_meridian_flip_still_in_its_checks_is_not_aborted_by_the_raced_GoTo() {
            await using var race = await HeldChecksRace.StartAsync(svc => svc.MeridianFlip(SlewBTarget, CancellationToken.None));

            var flip = await race.RunAsync();

            Assert.That(await flip.WaitAsync(TimeSpan.FromSeconds(15)), Is.True, "the flip goto arrives");
            AssertAbortedBetweenTheGoTos(race.Box, race.Log);
        }

        // #2066 — a move requested just after A decided to abort again, with nothing left to check, still sends its PUT only
        // once A's AbortSlew has gone out: the two calls travel on separate connections, and the abort arriving second would
        // stop the move the user asked for after the stop.
        [Test]
        public async Task A_sequencer_slew_requested_as_the_raced_GoTo_aborts_again_waits_for_that_abort() {
            var log = new WarningLog();
            TelescopeService? svc = null;
            Task<bool>? slewB = null;
            var gotos = 0;
            var mount = Mount(tracking: true);
            await using var box = ScriptedAlpacaDevice.Start(
                path => path.EndsWith("/equatorialsystem", StringComparison.Ordinal) ? "1" : mount(path), // JNOW
                putResponder: path => {
                    if (path.EndsWith("/" + GotoVerb, StringComparison.Ordinal) && Interlocked.Increment(ref gotos) == 1) {
                        Thread.Sleep(200); // A's PUT on the wire
                        svc!.FenceMotion("user_request"); // a Stop Mount
                    }
                    return null;
                });
            log.OnWarning = message => {
                if (!message.Contains("the mount was aborted again", StringComparison.Ordinal)) {
                    return;
                }
                // A has decided to abort again and is about to send it: B starts now, and is given time to reach the mount
                // first if nothing holds it back.
                slewB = Task.Run(() => svc!.SlewToCoordinatesAsync(SlewBTarget, CancellationToken.None));
                SpinWait.SpinUntil(() => box.Puts.Count(p => p.Path.EndsWith("/" + GotoVerb, StringComparison.Ordinal)) >= 2, TimeSpan.FromSeconds(1));
            };
            svc = await ConnectedAsync(box, log);
            using var _ = svc;
            await WaitForAsync(() => svc.GetInfo().EquatorialSystem == Epoch.JNOW, "the mount's coordinate system was never read");

            await svc.SlewAsync(new SlewRequestDto(6.0, 6.0), idempotencyKey: null, CancellationToken.None);

            await WaitForAsync(() => slewB is not null, "the raced GoTo never aborted again");
            Assert.That(await slewB!.WaitAsync(TimeSpan.FromSeconds(15)), Is.True, "the sequencer slew arrives");
            AssertAbortedBetweenTheGoTos(box, log);
        }

        // #2066 — a meridian flip whose pier-side write the mount ignores while it slews: the flip waits for the mount to stop
        // before its goto. A's second abort must not wait for the flip, or the mount drives all the way to A after the stop.
        [Test]
        public async Task A_meridian_flip_waiting_on_its_pier_side_write_does_not_hold_the_raced_GoTo_on_its_way() {
            var log = new WarningLog();
            TelescopeService? svc = null;
            Task<bool>? flip = null;
            var gotos = 0;
            var slewing = 0; // the mount slews to A from A's GoTo until an AbortSlew or the flip's goto
            var ra = (SiteAstrometry.LocalSiderealTimeDeg(DateTimeOffset.UtcNow, 0.0) / 15.0 + 3.0) % 24.0; // 3 h east: pierWest
            var raText = ra.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var mount = Mount(tracking: true);
            await using var box = ScriptedAlpacaDevice.Start(
                path => path.EndsWith("/equatorialsystem", StringComparison.Ordinal) ? "1" // JNOW
                    : path.EndsWith("/slewing", StringComparison.Ordinal) ? (Volatile.Read(ref slewing) == 1 ? "true" : "false")
                    : path.EndsWith("/rightascension", StringComparison.Ordinal) ? raText
                    : path.EndsWith("/sideofpier", StringComparison.Ordinal) ? "0" // pierEast: the flip writes pierWest
                    : path.EndsWith("/cansetpierside", StringComparison.Ordinal) ? "true"
                    : mount(path),
                putResponder: path => {
                    if (path.EndsWith("/abortslew", StringComparison.Ordinal)) {
                        Volatile.Write(ref slewing, 0);
                    } else if (path.EndsWith("/" + GotoVerb, StringComparison.Ordinal) && Interlocked.Increment(ref gotos) == 1) {
                        Volatile.Write(ref slewing, 1);
                        Thread.Sleep(200); // A's PUT on the wire
                        svc!.FenceMotion("user_request"); // a Stop Mount
                        flip = Task.Run(() => svc.MeridianFlip(new Coordinates(Angle.ByHours(ra), Angle.ByDegree(6.0), Epoch.JNOW), CancellationToken.None));
                        Thread.Sleep(100); // the flip starts before A's PUT returns
                    } else if (path.EndsWith("/" + GotoVerb, StringComparison.Ordinal)) {
                        Volatile.Write(ref slewing, 0);
                    }
                    return null;
                });
            var profile = new OpenAstroAra.Profile.Profile();
            profile.AstrometrySettings.Longitude = 0.0;
            profile.MeridianFlipSettings.UseSideOfPier = true;
            var profileService = new Mock<OpenAstroAra.Profile.Interfaces.IProfileService>();
            profileService.SetupGet(p => p.ActiveProfile).Returns(profile);
            using (profile) {
                svc = new TelescopeService(logger: log, profileService: profileService.Object) { SlewSettleMaxPolls = 30 }; // 3 s
                using var _ = svc;
                await svc.ConnectAsync(new ConnectRequestDto(Device(box)), null, CancellationToken.None);
                await WaitForAsync(() => svc.GetInfo() is { EquatorialSystem: Epoch.JNOW, SideOfPier: PierSide.pierEast } info && !double.IsNaN(info.SiderealTime),
                    "the mount never reported its coordinate system, pier side and sidereal time");

                await svc.SlewAsync(new SlewRequestDto(ra, 6.0), idempotencyKey: null, CancellationToken.None);

                await WaitForAsync(() => flip is not null, "the flip never started");
                Assert.That(await flip!.WaitAsync(TimeSpan.FromSeconds(15)), Is.True, "the flip goto arrives");
                var puts = box.Puts.ToArray();
                var pier = Array.FindIndex(puts, p => p.Path.EndsWith("/sideofpier", StringComparison.Ordinal));
                Assert.Multiple(() => {
                    Assert.That(pier, Is.GreaterThanOrEqualTo(0), "the flip wrote the pier side");
                    Assert.That(log.Messages, Has.None.Contains("still moving"), "the flip waited out its bound while the mount drove on to A");
                });
                AssertAbortedBetweenTheGoTos(box, log);
            }
        }

        // #2066 — the sequencer slew A's second abort was kept back for is then refused by its own checks (the mount's
        // coordinate system cannot be read): it moves nothing, so the abort A kept back goes out, or the mount slews to A after
        // the user's stop.
        [Test]
        public async Task A_sequencer_slew_its_checks_refuse_still_leaves_the_raced_GoTo_aborted() {
            await using var race = await HeldChecksRace.StartAsync(svc => svc.SlewToCoordinatesAsync(SlewBTarget, CancellationToken.None), systemReads: false);

            var slewB = await race.RunAsync();

            await Assert.ThrowsAsync<SequenceEntityFailedException>(() => slewB.WaitAsync(TimeSpan.FromSeconds(15)), "the unknown system refuses the slew");
            await WaitForAsync(() => PutIndex(race.Box, "abortslew") >= 0, "the raced GoTo was left slewing after the sequencer slew was refused");
            Assert.Multiple(() => {
                Assert.That(PutIndex(race.Box, "abortslew"), Is.GreaterThan(PutIndex(race.Box, GotoVerb)), "the abort follows the GoTo");
                Assert.That(race.Box.Puts.Count(p => p.Path.EndsWith("/" + GotoVerb, StringComparison.Ordinal)), Is.EqualTo(1), "the refused slew never reached the mount");
            });
        }

        // #2066 — the same race with a sequencer (mediator) Park or Find Home sent after the stop: it is the user's newer move
        // too, and A's second abort would stop it.
        [TestCase("park")]
        [TestCase("findhome")]
        public async Task A_sequencer_park_or_find_home_after_the_stop_is_not_aborted_by_the_raced_GoTo(string move) {
            var log = new WarningLog();
            TelescopeService? svc = null;
            Task<bool>? moveB = null;
            var gotos = 0;
            var arrived = 0; // the park or Find Home reached the mount: it reports AtPark / AtHome from then on
            var mount = Mount(tracking: true);
            await using var box = ScriptedAlpacaDevice.Start(
                path => path.EndsWith("/at" + (move == "park" ? "park" : "home"), StringComparison.Ordinal)
                    ? (Volatile.Read(ref arrived) == 1 ? "true" : "false")
                    : mount(path),
                putResponder: path => {
                    if (path.EndsWith("/" + GotoVerb, StringComparison.Ordinal) && Interlocked.Increment(ref gotos) == 1) {
                        Thread.Sleep(200); // A's PUT on the wire
                        svc!.FenceMotion("user_request"); // a Stop Mount
                        // The pose check is cached, so the move reaches its send synchronously; its PUT follows A's.
                        moveB = move == "park"
                            ? svc.ParkTelescope(new Progress<ApplicationStatus>(), CancellationToken.None)
                            : svc.FindHome(new Progress<ApplicationStatus>(), CancellationToken.None);
                    } else if (path.EndsWith("/" + move, StringComparison.Ordinal)) {
                        Volatile.Write(ref arrived, 1);
                    }
                    return null;
                });
            svc = await ConnectedAsync(box, log);
            using var _ = svc;

            await svc.SlewAsync(new SlewRequestDto(6.0, 6.0), idempotencyKey: null, CancellationToken.None);

            await WaitForAsync(() => moveB is not null, "the sequencer " + move + " was never sent");
            Assert.That(await moveB!.WaitAsync(TimeSpan.FromSeconds(15)), Is.True, "the sequencer " + move + " arrives");
            Assert.Multiple(() => {
                Assert.That(PutIndex(box, "abortslew"), Is.LessThan(0), "the raced GoTo's second abort would stop the sequencer " + move);
                Assert.That(log.Messages, Has.None.Contains("the mount was aborted again"));
            });
        }

        // #2066 — the sequencer park sent after the stop is refused by the pose check (#2070): it moves nothing, so it cannot
        // stand in for A's second abort. The abort A kept back for it goes out, or the mount slews to A after the user's stop.
        [Test]
        public async Task A_sequencer_park_the_pose_check_refuses_still_leaves_the_raced_GoTo_aborted() {
            var log = new WarningLog();
            TelescopeService? svc = null;
            Task<bool>? parkB = null;
            var gotos = 0;
            await using var box = ScriptedAlpacaDevice.Start(Mount(tracking: true), putResponder: path => {
                if (path.EndsWith("/" + GotoVerb, StringComparison.Ordinal) && Interlocked.Increment(ref gotos) == 1) {
                    Thread.Sleep(200); // A's PUT on the wire
                    svc!.FenceMotion("user_request"); // a Stop Mount
                    // A power blip flags the position as unverified after A's own check passed: a quiet 30 s, then the
                    // declination jumps with no move commanded.
                    var now = svc.PoseClock.GetUtcNow() + MountPoseTrust.QuietWindow;
                    svc.PoseTrust.Observe(atHome: false, slewing: false, decDeg: 6.0, now);
                    svc.PoseTrust.Observe(atHome: false, slewing: false, decDeg: 20.0, now + MountPoseTrust.QuietWindow + TimeSpan.FromSeconds(1));
                    // The refused park stops tracking first; that PUT queues behind A's, so the refusal settles the park
                    // after A's PUT returned and kept its second abort back for it.
                    parkB = svc.ParkTelescope(new Progress<ApplicationStatus>(), CancellationToken.None);
                } else if (path.EndsWith("/tracking", StringComparison.Ordinal)) {
                    Thread.Sleep(300); // the tracking-off write on the wire while A's return is handled
                }
                return null;
            });
            svc = await ConnectedAsync(box, log);
            using var _ = svc;

            await svc.SlewAsync(new SlewRequestDto(6.0, 6.0), idempotencyKey: null, CancellationToken.None);

            await WaitForAsync(() => parkB is not null, "the sequencer park was never sent");
            await Assert.ThrowsAsync<SequenceEntityFailedException>(() => parkB!.WaitAsync(TimeSpan.FromSeconds(15)), "the pose check refuses the park");
            await WaitForAsync(() => PutIndex(box, "abortslew") >= 0, "the raced GoTo was left slewing after the sequencer park was refused");
            Assert.Multiple(() => {
                Assert.That(PutIndex(box, "park"), Is.LessThan(0), "the refused park never reached the mount");
                Assert.That(PutIndex(box, "abortslew"), Is.GreaterThan(PutIndex(box, GotoVerb)), "the abort follows the GoTo");
            });
        }

        [Test]
        public async Task A_find_home_with_no_stop_reaches_the_mount() {
            await using var box = ScriptedAlpacaDevice.Start(Mount(tracking: false));
            using var svc = await ConnectedAsync(box);

            await svc.FindHomeAsync(idempotencyKey: null, CancellationToken.None);

            await WaitForAsync(() => PutIndex(box, "findhome") >= 0, "the mount never received the Find Home");
        }

        [Test]
        public async Task A_park_with_no_stop_turns_tracking_on_then_parks() {
            await using var box = ScriptedAlpacaDevice.Start(Mount(tracking: false));
            using var svc = await ConnectedAsync(box);

            await svc.ParkAsync(new ParkRequestDto(), idempotencyKey: null, CancellationToken.None);

            await WaitForAsync(() => PutIndex(box, "park") >= 0, "the mount never received the park");
            var tracking = PutIndex(box, "tracking", TrackingOn);
            Assert.That(tracking, Is.GreaterThanOrEqualTo(0).And.LessThan(PutIndex(box, "park")), "tracking is turned on before the park");
        }

        [TestCase("0", AlignmentMode.AltAz)]
        [TestCase("1", AlignmentMode.Polar)]
        [TestCase("2", AlignmentMode.GermanPolar)]
        public async Task The_mount_type_is_read_once_at_connect(string wire, AlignmentMode expected) {
            var mount = Mount(tracking: true);
            await using var box = ScriptedAlpacaDevice.Start(path => path.EndsWith("/alignmentmode", StringComparison.Ordinal) ? wire : mount(path));
            using var svc = await ConnectedAsync(box);

            await WaitForAsync(() => svc.MountAlignmentMode is not null, "the mount type was never read");
            Assert.That(svc.MountAlignmentMode, Is.EqualTo(expected));
        }

        // #2083 — Polar Align's motor start needs CanMoveAxis(Primary), read once at connect.
        [TestCase("true", true)]
        [TestCase("false", false)]
        public async Task CanMoveAxis_primary_is_read_once_at_connect(string wire, bool expected) {
            var mount = Mount(tracking: true);
            await using var box = ScriptedAlpacaDevice.Start(path => path.EndsWith("/canmoveaxis", StringComparison.Ordinal) ? wire : mount(path));
            using var svc = await ConnectedAsync(box);

            await WaitForAsync(() => svc.CanMovePrimaryAxis is not null, "CanMoveAxis(Primary) was never read");
            Assert.That(svc.CanMovePrimaryAxis, Is.EqualTo(expected));
        }

        [Test]
        public async Task A_mount_that_does_not_report_its_type_reads_unknown() {
            var log = new InfoLog();
            var mount = Mount(tracking: true);
            await using var box = ScriptedAlpacaDevice.Start(path => path.EndsWith("/alignmentmode", StringComparison.Ordinal) ? ScriptedAlpacaDevice.NotImplementedPrefix + "AlignmentMode" : mount(path));
            var svc = new TelescopeService(logger: log);
            using var _ = svc;
            await svc.ConnectAsync(new ConnectRequestDto(Device(box)), null, CancellationToken.None);
            await WaitForAsync(() => svc.GetAsync(CancellationToken.None).Result?.Capabilities is not null, "the capabilities were never read");

            Assert.That(svc.MountAlignmentMode, Is.Null, "unknown: the counterweight check stays on");
            await WaitForAsync(() => log.Messages.Any(m => m.Contains("AlignmentMode", StringComparison.Ordinal)), "the unknown type was never logged");
            Assert.That(log.Messages.Count(m => m.Contains("AlignmentMode", StringComparison.Ordinal)), Is.EqualTo(1), "logged once per connect");
        }

        private sealed class InfoLog : Microsoft.Extensions.Logging.ILogger<TelescopeService> {
            public ConcurrentQueue<string> Messages { get; } = new();
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
                    TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
                if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Information) {
                    Messages.Enqueue(formatter(state, exception));
                }
            }
        }

        private sealed class WarningLog : Microsoft.Extensions.Logging.ILogger<TelescopeService> {
            public ConcurrentQueue<string> Messages { get; } = new();
            /// <summary>Runs on the logging thread, after the message is queued.</summary>
            public Action<string>? OnWarning { get; set; }
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
                    TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
                if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning) {
                    var message = formatter(state, exception);
                    Messages.Enqueue(message);
                    OnWarning?.Invoke(message);
                }
            }
        }
    }
}
