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
using OpenAstroAra.Core.Enums;
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
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
                    TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
                if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning) {
                    Messages.Enqueue(formatter(state, exception));
                }
            }
        }
    }
}
