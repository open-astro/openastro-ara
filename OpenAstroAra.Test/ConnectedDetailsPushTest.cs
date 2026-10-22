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
    /// readings, ports), then announces it again once they are cached — so a client that
    /// re-read on the first push, and saw no details, re-reads instead of waiting out its
    /// 15 s liveness poll. Two representative services; all ten share the same tail.
    /// </summary>
    [TestFixture]
    [Category("IO")] // #1265 — real disk, loopback HTTP or a simulator: not part of the quick unit run
    public class ConnectedDetailsPushTest {

        private sealed class StateRecorder : IWsBroadcaster {
            private readonly List<string> _states = new();
            public long CurrentSequence => _states.Count;
            public int Connected() { lock (_states) { return _states.Count(s => s == "connected"); } }
            public Task PublishAsync(string eventType, JsonElement payload, CancellationToken ct) {
                if (eventType == WsEventCatalog.EquipmentStateChanged) {
                    lock (_states) { _states.Add(payload.GetProperty("state").GetString()!); }
                }
                return Task.CompletedTask;
            }
        }

        private static DiscoveredDeviceDto Device(ScriptedAlpacaDevice stub, DeviceType type) => new(
            UniqueId: $"{type}-under-test", Name: $"Bench {type}", Type: type,
            HostName: stub.BaseUri.Host, IpAddress: stub.BaseUri.Host, IpPort: stub.BaseUri.Port,
            AlpacaDeviceNumber: 0, UseHttps: false);

        private static async Task WaitForConnectedPushesAsync(StateRecorder recorder, int count) {
            var sw = Stopwatch.StartNew();
            while (recorder.Connected() < count && sw.Elapsed < TimeSpan.FromSeconds(15)) {
                await Task.Delay(20);
            }
        }

        [Test]
        [Category("bench")] // loopback-only, runs in the default job too
        public async Task Focuser_connect_pushes_connected_again_once_details_are_cached() {
            await using var stub = ScriptedAlpacaDevice.Start(path => path.EndsWith("/maxstep", StringComparison.Ordinal)
                || path.EndsWith("/position", StringComparison.Ordinal) || path.EndsWith("/maxincrement", StringComparison.Ordinal)
                ? "1000" : null);
            var recorder = new StateRecorder();
            using var svc = new FocuserService(events: new EquipmentEventPublisher(recorder));

            await svc.ConnectAsync(new ConnectRequestDto(Device(stub, DeviceType.Focuser)), null, CancellationToken.None);
            await WaitForConnectedPushesAsync(recorder, 2);

            Assert.That(recorder.Connected(), Is.EqualTo(2), "Connected, then Connected again after the seed read");
        }

        [Test]
        [Category("bench")]
        public async Task SafetyMonitor_connect_pushes_connected_again_once_details_are_cached() {
            await using var stub = ScriptedAlpacaDevice.Start();
            var recorder = new StateRecorder();
            using var svc = new SafetyMonitorService(events: new EquipmentEventPublisher(recorder));

            await svc.ConnectAsync(new ConnectRequestDto(Device(stub, DeviceType.SafetyMonitor)), null, CancellationToken.None);
            await WaitForConnectedPushesAsync(recorder, 2);

            Assert.That(recorder.Connected(), Is.EqualTo(2), "Connected, then Connected again after the seed read");
        }
    }
}
