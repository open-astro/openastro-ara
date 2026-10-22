#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Alpaca;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// A connect announces Connected before the service has read the device's details (caps,
    /// readings, ports), then publishes <see cref="EquipmentEventPublisher.DetailsReady"/> once
    /// they are cached — so a client that re-read on the first push, and saw no details, re-reads
    /// instead of waiting out its 15 s liveness poll. Every service has its own connect tail, so
    /// every service is a case.
    /// </summary>
    [TestFixture]
    [Category("IO")] // #1265 — real disk, loopback HTTP or a simulator: not part of the quick unit run
    public class ConnectedDetailsPushTest {

        private sealed record Published(string Type, string? State, string? DeviceId, bool DetailsReady);

        private sealed class Recorder : IWsBroadcaster {
            private readonly List<Published> _events = new();
            public long CurrentSequence => _events.Count;
            public Published[] Snapshot() { lock (_events) { return _events.ToArray(); } }
            public Task PublishAsync(string eventType, JsonElement payload, CancellationToken ct) {
                var state = payload.TryGetProperty("state", out var s) ? s.GetString() : null;
                var id = payload.TryGetProperty("device_id", out var d) ? d.GetString() : null;
                var ready = payload.TryGetProperty("details_ready", out var r) && r.GetBoolean();
                lock (_events) { _events.Add(new Published(eventType, state, id, ready)); }
                return Task.CompletedTask;
            }
        }

        private static TestCaseData Make<T>(DeviceType type, Func<EquipmentEventPublisher, T> create,
                Func<T, ConnectRequestDto, Task> connect) where T : IDisposable =>
            new TestCaseData(type,
                    new Func<EquipmentEventPublisher, IDisposable>(e => create(e)),
                    new Func<IDisposable, ConnectRequestDto, Task>((svc, req) => connect((T)svc, req)))
                .SetArgDisplayNames(type.ToString());

        public static IEnumerable<TestCaseData> Services() {
            yield return Make(DeviceType.Camera, e => new CameraService(events: e), (s, r) => s.ConnectAsync(r, null, CancellationToken.None));
            yield return Make(DeviceType.Telescope, e => new TelescopeService(events: e), (s, r) => s.ConnectAsync(r, null, CancellationToken.None));
            yield return Make(DeviceType.Focuser, e => new FocuserService(events: e), (s, r) => s.ConnectAsync(r, null, CancellationToken.None));
            yield return Make(DeviceType.FilterWheel, e => new FilterWheelService(events: e), (s, r) => s.ConnectAsync(r, null, CancellationToken.None));
            yield return Make(DeviceType.Rotator, e => new RotatorService(events: e), (s, r) => s.ConnectAsync(r, null, CancellationToken.None));
            yield return Make(DeviceType.FlatDevice, e => new FlatDeviceService(events: e), (s, r) => s.ConnectAsync(r, null, CancellationToken.None));
            yield return Make(DeviceType.ObservingConditions, e => new ObservingConditionsService(events: e), (s, r) => s.ConnectAsync(r, null, CancellationToken.None));
            yield return Make(DeviceType.SafetyMonitor, e => new SafetyMonitorService(events: e), (s, r) => s.ConnectAsync(r, null, CancellationToken.None));
            yield return Make(DeviceType.Dome, e => new DomeService(events: e), (s, r) => s.ConnectAsync(r, null, CancellationToken.None));
            yield return Make(DeviceType.Switch, e => new SwitchService(events: e), (s, r) => s.ConnectAsync(r, null, CancellationToken.None));
        }

        // Numeric answers for the seed reads that need one; everything else answers `true`.
        private static string? Script(string path) =>
            path.EndsWith("/maxstep", StringComparison.Ordinal) || path.EndsWith("/position", StringComparison.Ordinal)
            || path.EndsWith("/maxincrement", StringComparison.Ordinal) || path.EndsWith("/maxswitch", StringComparison.Ordinal)
                ? "1" : null;

        [Test]
        [Category("bench")] // loopback-only, runs in the default job too
        [TestCaseSource(nameof(Services))]
        public async Task Connect_publishes_details_ready_once_the_seed_read_is_cached(DeviceType type,
                Func<EquipmentEventPublisher, IDisposable> create, Func<IDisposable, ConnectRequestDto, Task> connect) {
            await using var stub = ScriptedAlpacaDevice.Start(Script);
            var recorder = new Recorder();
            using var svc = create(new EquipmentEventPublisher(recorder));
            var device = new DiscoveredDeviceDto(
                UniqueId: $"{type}-under-test", Name: $"Bench {type}", Type: type,
                HostName: stub.BaseUri.Host, IpAddress: stub.BaseUri.Host, IpPort: stub.BaseUri.Port,
                AlpacaDeviceNumber: 0, UseHttps: false);

            await connect(svc, new ConnectRequestDto(device));
            var sw = Stopwatch.StartNew();
            while (!recorder.Snapshot().Any(e => e.DetailsReady) && sw.Elapsed < TimeSpan.FromSeconds(5)) {
                await Task.Delay(20);
            }

            var events = recorder.Snapshot();
            var connected = events.Where(e => e.Type == WsEventCatalog.EquipmentStateChanged && e.State == "connected").ToArray();
            Assert.That(connected, Has.Length.EqualTo(2), "Connected, then details-ready after the seed read");
            Assert.That(connected[0].DetailsReady, Is.False);
            Assert.That(connected[1].DetailsReady, Is.True);
            Assert.That(connected[1].DeviceId, Is.EqualTo(device.UniqueId), "the push names the device");
            Assert.That(events.Count(e => e.Type == WsEventCatalog.EquipmentConnected), Is.EqualTo(1),
                "details-ready is not a second connect: no repeated alias");
        }
    }
}
