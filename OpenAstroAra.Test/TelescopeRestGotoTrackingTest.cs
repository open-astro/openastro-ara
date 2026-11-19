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
