#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    // One row per service: the wire token the removal must carry, the interface the
    // reconnector resolves it through, and the type tokens that must map to it.
    public sealed record RetainedDeviceRow(
        string Name,
        DeviceType Type,
        Type ServiceInterface,
        IReadOnlyList<DeviceType> ResolvedAs,
        Func<EquipmentEventPublisher, IDisposable> Create,
        Func<IDisposable, ConnectRequestDto, Task> Connect,
        Func<IDisposable, Task> Disconnect,
        Func<IDisposable, Task<bool>> Forget) {
        public override string ToString() => Name;
    }

    /// <summary>#1127 — the nine single-instance services carry identical copies of the retained-
    /// device record and the removal publish; one parameterized pass over all of them catches a
    /// wrong <c>DeviceType</c> token in any copy, and pins the reconnector's type → service map
    /// (including the FlatDevice/CoverCalibrator alias).</summary>
    [TestFixture]
    public class RetainedDeviceRemovalTest {

        private static RetainedDeviceRow Of<TService, TInterface>(DeviceType type, Func<EquipmentEventPublisher, TService> create,
                Func<TService, ConnectRequestDto, Task> connect, Func<TService, Task> disconnect, Func<TService, Task<bool>> forget,
                params DeviceType[] alsoResolvedAs)
                where TService : class, IDisposable, TInterface =>
            new(typeof(TService).Name, type, typeof(TInterface), [type, .. alsoResolvedAs],
                e => create(e), (s, r) => connect((TService)s, r), s => disconnect((TService)s), s => forget((TService)s));

        public static IEnumerable<RetainedDeviceRow> Services() {
            yield return Of<CameraService, ICameraService>(DeviceType.Camera, e => new CameraService(events: e),
                (s, r) => s.ConnectAsync(r, null, CancellationToken.None), s => s.DisconnectAsync(null, CancellationToken.None), s => s.ForgetAsync(CancellationToken.None));
            yield return Of<TelescopeService, ITelescopeService>(DeviceType.Telescope, e => new TelescopeService(events: e),
                (s, r) => s.ConnectAsync(r, null, CancellationToken.None), s => s.DisconnectAsync(null, CancellationToken.None), s => s.ForgetAsync(CancellationToken.None));
            yield return Of<FocuserService, IFocuserService>(DeviceType.Focuser, e => new FocuserService(events: e),
                (s, r) => s.ConnectAsync(r, null, CancellationToken.None), s => s.DisconnectAsync(null, CancellationToken.None), s => s.ForgetAsync(CancellationToken.None));
            yield return Of<FilterWheelService, IFilterWheelService>(DeviceType.FilterWheel, e => new FilterWheelService(events: e),
                (s, r) => s.ConnectAsync(r, null, CancellationToken.None), s => s.DisconnectAsync(null, CancellationToken.None), s => s.ForgetAsync(CancellationToken.None));
            yield return Of<RotatorService, IRotatorService>(DeviceType.Rotator, e => new RotatorService(events: e),
                (s, r) => s.ConnectAsync(r, null, CancellationToken.None), s => s.DisconnectAsync(null, CancellationToken.None), s => s.ForgetAsync(CancellationToken.None));
            yield return Of<DomeService, IDomeService>(DeviceType.Dome, e => new DomeService(events: e),
                (s, r) => s.ConnectAsync(r, null, CancellationToken.None), s => s.DisconnectAsync(null, CancellationToken.None), s => s.ForgetAsync(CancellationToken.None));
            yield return Of<SafetyMonitorService, ISafetyMonitorService>(DeviceType.SafetyMonitor, e => new SafetyMonitorService(events: e),
                (s, r) => s.ConnectAsync(r, null, CancellationToken.None), s => s.DisconnectAsync(null, CancellationToken.None), s => s.ForgetAsync(CancellationToken.None));
            yield return Of<ObservingConditionsService, IObservingConditionsService>(DeviceType.ObservingConditions, e => new ObservingConditionsService(events: e),
                (s, r) => s.ConnectAsync(r, null, CancellationToken.None), s => s.DisconnectAsync(null, CancellationToken.None), s => s.ForgetAsync(CancellationToken.None));
            yield return Of<FlatDeviceService, IFlatDeviceService>(DeviceType.FlatDevice, e => new FlatDeviceService(events: e),
                (s, r) => s.ConnectAsync(r, null, CancellationToken.None), s => s.DisconnectAsync(null, CancellationToken.None), s => s.ForgetAsync(CancellationToken.None),
                DeviceType.CoverCalibrator);
        }

        private static DiscoveredDeviceDto Dead(DeviceType type) =>
            new("uid-" + type, "Dead " + type, type, "127.0.0.1", "127.0.0.1", 1, 0, false);

        [TestCaseSource(nameof(Services))]
        public async Task Forget_publishes_the_removal_under_the_services_own_type_and_clears_the_retained_record(RetainedDeviceRow row) {
            var ws = new Mock<IWsBroadcaster>();
            var events = new List<(string Type, JsonElement Payload)>();
            ws.Setup(w => w.PublishAsync(It.IsAny<string>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
                .Callback<string, JsonElement, CancellationToken>((t, p, _) => { lock (events) { events.Add((t, p.Clone())); } })
                .Returns(Task.CompletedTask);
            using var svc = row.Create(new EquipmentEventPublisher(ws.Object));
            var retained = (IRetainedDeviceSource)svc;
            Assert.That(retained.RetainedDevice, Is.Null, "nothing retained before a connect");

            await row.Connect(svc, new ConnectRequestDto(Dead(row.Type)));
            await row.Disconnect(svc); // supersedes the dead connect; the device stays retained
            Assert.That(retained.RetainedDevice?.UniqueId, Is.EqualTo("uid-" + row.Type));

            Assert.That(await row.Forget(svc), Is.True);

            Assert.That(retained.RetainedDevice, Is.Null, "the reconnector's fallback must see nothing after Remove");
            (string Type, JsonElement Payload) removed;
            lock (events) {
                removed = events.Last(e => e.Type == WsEventCatalog.EquipmentStateChanged);
            }
            Assert.That(removed.Payload.GetProperty("removed").GetBoolean(), Is.True);
            Assert.That(removed.Payload.GetProperty("device_type").GetString(), Is.EqualTo(row.Type.ToString()).IgnoreCase,
                "the removal must carry THIS service's type, or another type's clients refresh for nothing and this type's keep the card");
            Assert.That(removed.Payload.GetProperty("device_id").GetString(), Is.EqualTo("uid-" + row.Type));
        }

        [TestCaseSource(nameof(Services))]
        public async Task The_reconnector_resolves_the_retained_device_through_the_services_interface(RetainedDeviceRow row) {
            using var svc = row.Create(new EquipmentEventPublisher(Mock.Of<IWsBroadcaster>()));
            await row.Connect(svc, new ConnectRequestDto(Dead(row.Type)));
            await row.Disconnect(svc);
            var sp = new Mock<IServiceProvider>();
            sp.Setup(s => s.GetService(row.ServiceInterface)).Returns(svc);
            var reconnector = new EquipmentReconnector(sp.Object, new Mock<IEquipmentSelectionStore>().Object, NullLogger<EquipmentReconnector>.Instance);

            foreach (var type in row.ResolvedAs) {
                Assert.That(reconnector.ResolveRetained(type)?.UniqueId, Is.EqualTo("uid-" + row.Type), $"{type} must map to {row.Name}");
            }
            Assert.That(reconnector.ResolveRetained(DeviceType.Switch), Is.Null, "the switch registry is addressed by id, never a retained fallback");
        }
    }
}
