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
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Equipment.MyGuider;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.Refusals;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Alpaca;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #2066 (ARA-2) — the telescope service's single motion owner, against a scripted Alpaca mount. While Polar Align holds
    /// it, every mediator GoTo (the sequencer's, Center's) is refused before anything reaches the mount; nothing that stops
    /// the mount is: Emergency Stop's and Stop Mount's AbortSlew, a pad stop, tracking off, and the safety and fault parks.
    /// </summary>
    [TestFixture]
    [Category("IO")] // loopback scripted Alpaca device
    public class TelescopeMotionOwnerTest {

        private static DiscoveredDeviceDto Device(ScriptedAlpacaDevice box) => new(
            UniqueId: "mount-under-test", Name: "Bench mount", Type: DeviceType.Telescope,
            HostName: box.BaseUri.Host, IpAddress: box.BaseUri.Host, IpPort: box.BaseUri.Port,
            AlpacaDeviceNumber: 0, UseHttps: false);

        private static Func<string, string?> Mount(bool tracking = true) => path =>
            path.EndsWith("/connected", StringComparison.Ordinal) ? "true"
            : path.EndsWith("/slewing", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/tracking", StringComparison.Ordinal) ? (tracking ? "true" : "false")
            : path.EndsWith("/atpark", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/athome", StringComparison.Ordinal) ? "false"
            : path.EndsWith("/canmoveaxis", StringComparison.Ordinal) ? "true"
            : path.EndsWith("/equatorialsystem", StringComparison.Ordinal) ? "2" // J2000: the GoTo needs no transform
            : path.EndsWith("/rightascension", StringComparison.Ordinal) ? "6.0"
            : path.EndsWith("/declination", StringComparison.Ordinal) ? "45.0"
            : null;

        private static async Task WaitForAsync(Func<bool> condition, string failure) {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline) {
                if (condition()) {
                    return;
                }
                await Task.Delay(20);
            }
            Assert.Fail(failure);
        }

        private static async Task<(ScriptedAlpacaDevice Box, TelescopeService Svc)> ConnectedAsync() {
            var box = ScriptedAlpacaDevice.Start(Mount());
            return (box, await ConnectAsync(box));
        }

        private static async Task<TelescopeService> ConnectAsync(ScriptedAlpacaDevice box, WarningLog? log = null, Func<IGuiderMediator?>? guider = null) {
            var svc = new TelescopeService(logger: log, guiderResolver: guider) { RefreshPeriod = TimeSpan.FromMilliseconds(100) };
            await svc.ConnectAsync(new ConnectRequestDto(Device(box)), null, CancellationToken.None);
            await WaitForAsync(() => svc.GetAsync(CancellationToken.None).Result?.State == EquipmentConnectionState.Connected, "never connected");
            return svc;
        }

        /// <summary>The telescope service's warnings, in order.</summary>
        private sealed class WarningLog : Microsoft.Extensions.Logging.ILogger<TelescopeService> {
            public ConcurrentQueue<string> Messages { get; } = new();
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
                    TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
                if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning) {
                    Messages.Enqueue(formatter(state, exception));
                }
            }
        }

        private static bool Put(ScriptedAlpacaDevice box, string verb, Func<string, bool>? body = null) =>
            box.Puts.Any(p => p.Path.EndsWith("/" + verb, StringComparison.Ordinal) && (body is null || body(p.Body)));

        [Test]
        public void AcquireMotion_refuses_another_owner_and_an_old_hold_of_the_same_owner_gives_nothing_back() {
            using var svc = new TelescopeService();

            var first = svc.AcquireMotion(MotionOwners.PolarAlign);
            Assert.That(svc.MotionOwner, Is.EqualTo(MotionOwners.PolarAlign));
            Assert.Throws<InvalidOperationException>(() => svc.AcquireMotion("another_routine"), "another owner is refused");
            // A new run of the same routine takes it over; the old run's late release must not free the new run's hold.
            var second = svc.AcquireMotion(MotionOwners.PolarAlign);
            first.Dispose();
            Assert.That(svc.MotionOwner, Is.EqualTo(MotionOwners.PolarAlign), "the old hold gave nothing back");
            second.Dispose();
            Assert.That(svc.MotionOwner, Is.Null);
            second.Dispose();
            Assert.That(svc.MotionOwner, Is.Null, "a second give-back is a no-op");
        }

        [Test]
        public async Task A_mediator_GoTo_is_refused_while_Polar_Align_holds_the_mount_and_goes_out_once_it_is_given_back() {
            var (box, svc) = await ConnectedAsync();
            await using var device = box;
            using var service = svc;
            var target = new Coordinates(6.0, 45.0, Epoch.J2000, Coordinates.RAType.Hours);

            using (svc.AcquireMotion(MotionOwners.PolarAlign)) {
                var refused = await Assert.ThrowsAsync<SequenceEntityFailedException>(() => svc.SlewToCoordinatesAsync(target, CancellationToken.None));
                Assert.That(refused!.Message, Does.Contain("Polar alignment is running"));
                Assert.That(Put(box, "slewtocoordinatesasync"), Is.False, "nothing reached the mount");
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            _ = svc.SlewToCoordinatesAsync(target, cts.Token);
            await WaitForAsync(() => Put(box, "slewtocoordinatesasync"), "the GoTo never went out once the owner was given back");
            await cts.CancelAsync();
        }

        // A REST GoTo is checked against the owner when it is accepted, then waits for the guider to stop (up to 3 s): Polar
        // Align may take the mount in that wait. The GoTo is dropped when it would go out, not sent beside the run.
        [Test]
        public async Task A_REST_GoTo_accepted_before_Polar_Align_takes_the_mount_is_dropped_before_it_goes_out() {
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
            using var svc = await ConnectAsync(box, log, () => guider.Object);

            var slew = svc.SlewAsync(new SlewRequestDto(6.0, 45.0), idempotencyKey: null, CancellationToken.None);
            await stopAsked.Task.WaitAsync(TimeSpan.FromSeconds(15));
            using var hold = svc.AcquireMotion(MotionOwners.PolarAlign);
            stopDone.SetResult(true);
            await slew;

            await WaitForAsync(() => log.Messages.Any(m => m.Contains("dropped", StringComparison.Ordinal)), "the GoTo was not dropped once Polar Align held the mount");
            Assert.Multiple(() => {
                Assert.That(Put(box, "slewtocoordinatesasync"), Is.False, "the GoTo never reached the mount");
                Assert.That(Put(box, "tracking"), Is.False, "nor its tracking write");
                Assert.That(svc.MotionOwner, Is.EqualTo(MotionOwners.PolarAlign));
            });
        }

        /// <summary>A guider that is guiding and stops only when the test lets it: a REST move waits in its checks.</summary>
        private static (Mock<IGuiderMediator> Guider, TaskCompletionSource StopAsked, TaskCompletionSource<bool> StopDone) PausedGuider() {
            var stopAsked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var guider = new Mock<IGuiderMediator>();
            guider.Setup(g => g.GetInfo()).Returns(new GuiderInfo { Connected = true });
            guider.Setup(g => g.StopGuiding(It.IsAny<CancellationToken>())).Returns(() => {
                stopAsked.TrySetResult();
                return stopDone.Task;
            });
            return (guider, stopAsked, stopDone);
        }

        private static async Task WaitForOwnerFreeAsync(TelescopeService svc) {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline) {
                try {
                    svc.AcquireMotion(MotionOwners.PolarAlign).Dispose();
                    return;
                } catch (InvalidOperationException) {
                    await Task.Delay(20);
                }
            }
            Assert.Fail("the mount's motion owner was never free again");
        }

        // The REST park (POST /park) is counted in the dispatch as a REST GoTo is. While it waits for the guider to stop, Polar
        // Align's start is refused, and the park goes out. Once Polar Align holds the mount when the park's checks end, the
        // park is refused before it ends the leases, stops the guider or reaches the mount.
        [Test]
        public async Task A_REST_park_still_in_its_checks_when_Polar_Align_starts_is_dropped_or_refuses_the_start() {
            await using var box = ScriptedAlpacaDevice.Start(Mount(tracking: false));
            var (guider, stopAsked, stopDone) = PausedGuider();
            using var svc = await ConnectAsync(box, guider: () => guider.Object);

            var park = svc.ParkUnlessMotionOwnedAsync(new ParkRequestDto(), idempotencyKey: null, CancellationToken.None);
            await stopAsked.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Throws<InvalidOperationException>(() => svc.AcquireMotion(MotionOwners.PolarAlign),
                "Polar Align cannot take the mount while the park is on its way");
            stopDone.SetResult(true);
            await park;
            await WaitForAsync(() => Put(box, "park"), "the park never went out");
            await WaitForOwnerFreeAsync(svc);

            var parks = box.Puts.Count(p => p.Path.EndsWith("/park", StringComparison.Ordinal));
            var trackingWrites = box.Puts.Count(p => p.Path.EndsWith("/tracking", StringComparison.Ordinal));
            using (svc.AcquireMotion(MotionOwners.PolarAlign)) {
                var refused = await Assert.ThrowsAsync<RefusalException>(() => svc.ParkUnlessMotionOwnedAsync(new ParkRequestDto(), idempotencyKey: null, CancellationToken.None));
                Assert.That(refused!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignMovingMount));
                Assert.That(refused.Message, Does.Contain("Polar alignment is running").And.Contain("park"));
                await Task.Delay(200);
                Assert.Multiple(() => {
                    Assert.That(box.Puts.Count(p => p.Path.EndsWith("/park", StringComparison.Ordinal)), Is.EqualTo(parks), "the dropped park never reached the mount");
                    Assert.That(box.Puts.Count(p => p.Path.EndsWith("/tracking", StringComparison.Ordinal)), Is.EqualTo(trackingWrites), "nor its tracking write");
                    Assert.That(svc.MotionOwner, Is.EqualTo(MotionOwners.PolarAlign));
                });
                guider.Verify(g => g.StopGuiding(It.IsAny<CancellationToken>()), Times.Once, "the dropped park left the guider alone");
            }
        }

        [Test]
        public async Task A_REST_Find_Home_still_in_its_checks_when_Polar_Align_starts_is_dropped_or_refuses_the_start() {
            await using var box = ScriptedAlpacaDevice.Start(Mount());
            var (guider, stopAsked, stopDone) = PausedGuider();
            using var svc = await ConnectAsync(box, guider: () => guider.Object);

            var home = svc.FindHomeAsync(idempotencyKey: null, CancellationToken.None);
            await stopAsked.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Throws<InvalidOperationException>(() => svc.AcquireMotion(MotionOwners.PolarAlign),
                "Polar Align cannot take the mount while the Find Home is on its way");
            stopDone.SetResult(true);
            await home;
            await WaitForAsync(() => Put(box, "findhome"), "the Find Home never went out");
            await WaitForOwnerFreeAsync(svc);

            var homes = box.Puts.Count(p => p.Path.EndsWith("/findhome", StringComparison.Ordinal));
            using (svc.AcquireMotion(MotionOwners.PolarAlign)) {
                var refused = await Assert.ThrowsAsync<RefusalException>(() => svc.FindHomeAsync(idempotencyKey: null, CancellationToken.None));
                Assert.That(refused!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignMovingMount));
                Assert.That(refused.Message, Does.Contain("Polar alignment is running").And.Contain("home"));
                await Task.Delay(200);
                Assert.Multiple(() => {
                    Assert.That(box.Puts.Count(p => p.Path.EndsWith("/findhome", StringComparison.Ordinal)), Is.EqualTo(homes), "the dropped Find Home never reached the mount");
                    Assert.That(svc.MotionOwner, Is.EqualTo(MotionOwners.PolarAlign));
                });
                guider.Verify(g => g.StopGuiding(It.IsAny<CancellationToken>()), Times.Once, "the dropped Find Home left the guider alone");
            }
        }

        // The owner check and the send are one step: while a REST or mediator GoTo is on its way (here, during its tracking
        // write), Polar Align cannot take the mount; its start is refused (mount_busy) and reads the slewing mount next time.
        [Test]
        public async Task Polar_Align_cannot_take_the_mount_while_a_GoTo_is_being_sent() {
            TelescopeService? svc = null;
            var attempts = new ConcurrentQueue<Exception?>();
            await using var box = ScriptedAlpacaDevice.Start(Mount(tracking: false), putResponder: path => {
                if (path.EndsWith("/tracking", StringComparison.Ordinal)) {
                    try {
                        svc!.AcquireMotion(MotionOwners.PolarAlign).Dispose();
                        attempts.Enqueue(null);
                    } catch (InvalidOperationException ex) {
                        attempts.Enqueue(ex);
                    }
                }
                return null;
            });
            svc = await ConnectAsync(box);
            using var _ = svc;
            var target = new Coordinates(6.0, 45.0, Epoch.J2000, Coordinates.RAType.Hours);

            await svc.SlewAsync(new SlewRequestDto(6.0, 45.0), idempotencyKey: null, CancellationToken.None);
            await WaitForAsync(() => Put(box, "slewtocoordinatesasync"), "the REST GoTo never went out");
            Assert.That(await svc.SlewToCoordinatesAsync(target, CancellationToken.None), Is.True, "the mediator GoTo went out");

            var tried = attempts.ToArray();
            Assert.That(tried, Has.Length.EqualTo(2), "one try during each GoTo's tracking write");
            Assert.That(tried, Has.All.InstanceOf<InvalidOperationException>(), "refused while each GoTo was being sent");
            Assert.That(svc.MotionOwner, Is.Null);
            using (svc.AcquireMotion(MotionOwners.PolarAlign)) {
                Assert.That(svc.MotionOwner, Is.EqualTo(MotionOwners.PolarAlign), "free again once both were sent");
            }
        }

        // A sync moves no motor, but it shifts the RA reading Polar Align steers its turns by (a jump of 0.5° reverses the
        // motor) and measures the sweep from. While the run holds the mount, Center's mediator sync and a REST sync are not
        // sent; Center falls back to its offset, as for a mount that cannot sync.
        [Test]
        public async Task A_sync_is_not_sent_while_Polar_Align_holds_the_mount() {
            var log = new WarningLog();
            await using var box = ScriptedAlpacaDevice.Start(Mount());
            using var svc = await ConnectAsync(box, log);
            var here = new Coordinates(6.0, 45.0, Epoch.J2000, Coordinates.RAType.Hours);

            var syncsBefore = svc.SyncEpoch;
            using (svc.AcquireMotion(MotionOwners.PolarAlign)) {
                Assert.That(await ((ITelescopeMediator)svc).Sync(here, CancellationToken.None), Is.False, "Center's sync is not sent");
                await svc.SlewAsync(new SlewRequestDto(6.0, 45.0, Sync: true), idempotencyKey: null, CancellationToken.None);
                await WaitForAsync(() => log.Messages.Any(m => m.Contains("dropped", StringComparison.Ordinal)), "the REST sync was not dropped");
                Assert.That(Put(box, "synctocoordinates"), Is.False, "no sync reached the mount");
                Assert.That(svc.SyncEpoch, Is.EqualTo(syncsBefore), "#2083 — no sync counted: a clutch turn's refusal stands");
            }

            Assert.That(await ((ITelescopeMediator)svc).Sync(here, CancellationToken.None), Is.True, "sent once the run gave the mount back");
            Assert.That(Put(box, "synctocoordinates"), Is.True);
            Assert.That(svc.SyncEpoch, Is.EqualTo(syncsBefore + 1), "#2083 — the sync after counts: it clears a clutch turn");
        }

        // Joey, 2026-11-20 on #2048: Park and Home are refused for Polar Align's whole run, the sequencer's too (its Park
        // Scope and Find Home, an end-of-sequence park, a failed flip's safe rest). Only the safety, fault and shutdown
        // parks get through, and they end Polar Align first. A refused park leaves tracking as it is: the run tracks.
        [Test]
        public async Task A_sequencer_park_or_Find_Home_is_refused_while_Polar_Align_holds_the_mount() {
            var (box, svc) = await ConnectedAsync();
            await using var device = box;
            using var service = svc;
            var progress = new Progress<ApplicationStatus>();

            using (svc.AcquireMotion(MotionOwners.PolarAlign)) {
                var park = await Assert.ThrowsAsync<SequenceEntityFailedException>(() => svc.ParkTelescope(progress, CancellationToken.None));
                var home = await Assert.ThrowsAsync<SequenceEntityFailedException>(() => svc.FindHome(progress, CancellationToken.None));
                Assert.That(park!.Message, Does.Contain("Polar alignment is running").And.Contain("park"));
                Assert.That(home!.Message, Does.Contain("Polar alignment is running").And.Contain("Find Home"));
            }

            Assert.Multiple(() => {
                Assert.That(Put(box, "park"), Is.False, "no park reached the mount");
                Assert.That(Put(box, "findhome"), Is.False, "no Find Home reached the mount");
                Assert.That(Put(box, "tracking"), Is.False, "tracking left as it was");
            });
        }

        // End Night's (and the unattended shutdown's) Find Home and park go through the mediator, which refuses them while
        // Polar Align holds the mount: the shutdown ladder ends Polar Align through its takeover first, as the safety and
        // fault parks do, so the shutdown is never blocked and Polar Align's motor never runs beside its Find Home or park.
        [Test]
        public async Task End_Night_ends_Polar_Align_before_its_Find_Home_and_park_and_both_reach_the_mount() {
            var homed = false;
            var parked = false;
            await using var box = ScriptedAlpacaDevice.Start(path =>
                    path.EndsWith("/athome", StringComparison.Ordinal) ? (Volatile.Read(ref homed) ? "true" : "false")
                    : path.EndsWith("/atpark", StringComparison.Ordinal) ? (Volatile.Read(ref parked) ? "true" : "false")
                    : Mount()(path),
                putResponder: path => {
                    if (path.EndsWith("/findhome", StringComparison.Ordinal)) {
                        Volatile.Write(ref homed, true);
                    } else if (path.EndsWith("/park", StringComparison.Ordinal)) {
                        Volatile.Write(ref parked, true);
                    }
                    return null;
                });
            using var svc = await ConnectAsync(box);
            await WaitForAsync(() => svc.GetInfo() is { CanFindHome: true, CanPark: true }, "the mount's capabilities were never read");
            var hold = svc.AcquireMotion(MotionOwners.PolarAlign);
            var takeovers = new List<string>();
            var polarAlign = new Mock<IPolarAlignService>();
            polarAlign.Setup(p => p.IsActive).Returns(() => svc.MotionOwner is not null);
            polarAlign.Setup(p => p.StopForTakeoverAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns((string by, CancellationToken _) => {
                takeovers.Add(by);
                hold.Dispose(); // the run's end gives the mount back
                return Task.FromResult(new OperationAcceptedDto(Guid.NewGuid(), "polar-align.stop", DateTimeOffset.UtcNow, null));
            });
            var ladder = new ShutdownLadder(telescope: svc, occupancy: new RigOccupancy().RegisterPolarAlign(() => polarAlign.Object));

            var home = await ladder.FindHomeAsync();
            hold = svc.AcquireMotion(MotionOwners.PolarAlign); // a run started again before the park step
            var rest = await ladder.RestMountAsync();

            Assert.Multiple(() => {
                Assert.That(home.Outcome, Is.EqualTo(LadderOutcome.Done), home.Text);
                Assert.That(rest.Rest, Is.EqualTo(MountRest.Parked), rest.Step.Text);
                Assert.That(Put(box, "findhome"), Is.True, "the shutdown's Find Home reached the mount");
                Assert.That(Put(box, "park"), Is.True, "the shutdown park reached the mount");
                Assert.That(takeovers, Has.Count.EqualTo(2), "Polar Align was ended before each move");
            });
        }

        // #2048 ARA-2 — the owner never blocks a stop: Emergency Stop's and Stop Mount's AbortSlew, a pad stop (rate 0),
        // tracking off, and the safety and fault parks (they call the service's ParkAsync, not the REST park, which yields).
        [Test]
        public async Task Emergency_Stop_and_the_safety_park_go_through_while_Polar_Align_holds_the_mount() {
            var (box, svc) = await ConnectedAsync();
            await using var _ = box;
            using var __ = svc;

            using (svc.AcquireMotion(MotionOwners.PolarAlign)) {
                await svc.AbortSlewAsync("emergency_stop", CancellationToken.None);
                await svc.MoveAxisAsync(0, 0.0, null, CancellationToken.None);
                await svc.SetTrackingAsync(false, CancellationToken.None);
                await svc.ParkAsync(new ParkRequestDto("safety.unsafe"), null, CancellationToken.None);
                await WaitForAsync(() => Put(box, "park"), "the safety park never reached the mount");
                await svc.ParkAsync(new ParkRequestDto("equipment.fault"), null, CancellationToken.None);

                await WaitForAsync(() => box.Puts.Count(p => p.Path.EndsWith("/park", StringComparison.Ordinal)) == 2, "the fault park never reached the mount");
                Assert.That(svc.MotionOwner, Is.EqualTo(MotionOwners.PolarAlign), "the owner was held throughout");
            }
            Assert.That(Put(box, "abortslew"), Is.True, "Emergency Stop's AbortSlew");
            Assert.That(Put(box, "moveaxis", b => b.Contains("Rate=0", StringComparison.Ordinal)), Is.True, "the pad stop");
            Assert.That(Put(box, "tracking", b => b.Contains("Tracking=False", StringComparison.OrdinalIgnoreCase)), Is.True, "tracking off");
        }
    }
}
