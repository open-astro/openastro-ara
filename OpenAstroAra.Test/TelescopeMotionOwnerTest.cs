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

            using (svc.AcquireMotion(MotionOwners.PolarAlign)) {
                Assert.That(await ((ITelescopeMediator)svc).Sync(here, CancellationToken.None), Is.False, "Center's sync is not sent");
                await svc.SlewAsync(new SlewRequestDto(6.0, 45.0, Sync: true), idempotencyKey: null, CancellationToken.None);
                await WaitForAsync(() => log.Messages.Any(m => m.Contains("dropped", StringComparison.Ordinal)), "the REST sync was not dropped");
                Assert.That(Put(box, "synctocoordinates"), Is.False, "no sync reached the mount");
            }

            Assert.That(await ((ITelescopeMediator)svc).Sync(here, CancellationToken.None), Is.True, "sent once the run gave the mount back");
            Assert.That(Put(box, "synctocoordinates"), Is.True);
        }

        // #2048 ARA-2 — the owner never blocks a stop: Emergency Stop's and Stop Mount's AbortSlew, a pad stop (rate 0),
        // tracking off, and the safety and fault parks (they call the service's ParkAsync, not the REST endpoint).
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
                Assert.That(svc.MotionOwner, Is.EqualTo(MotionOwners.PolarAlign), "the owner was held throughout");
            }
            Assert.That(Put(box, "abortslew"), Is.True, "Emergency Stop's AbortSlew");
            Assert.That(Put(box, "moveaxis", b => b.Contains("Rate=0", StringComparison.Ordinal)), Is.True, "the pad stop");
            Assert.That(Put(box, "tracking", b => b.Contains("Tracking=False", StringComparison.OrdinalIgnoreCase)), Is.True, "tracking off");
        }
    }
}
