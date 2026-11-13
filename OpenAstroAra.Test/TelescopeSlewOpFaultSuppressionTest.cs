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
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Alpaca;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1536 — a refused slew publishes a telescope op fault (feeding the escalation to
    /// abort_and_park); a caller that reports the failure itself, like Polar Align, runs the slew
    /// through <see cref="TelescopeService.RunWithoutOpFaultsAsync{T}"/> so it does not count.</summary>
    [TestFixture]
    [Category("IO")] // #1265 — loopback HTTP: not part of the quick unit run
    [Category("bench")]
    public class TelescopeSlewOpFaultSuppressionTest {

        private static async Task<List<EquipmentFaultEvent>> RefusedSlewFaultsAsync(bool suppressed) {
            await using var box = ScriptedAlpacaDevice.Start(
                path => path.EndsWith("/equatorialsystem", StringComparison.Ordinal) ? "2"
                    : path.EndsWith("/slewing", StringComparison.Ordinal) ? "false"
                    : path.EndsWith("/atpark", StringComparison.Ordinal) ? "false"
                    : path.EndsWith("/rightascension", StringComparison.Ordinal) ? "6.0"
                    : path.EndsWith("/declination", StringComparison.Ordinal) ? "45.0"
                    : null,
                path => path.EndsWith("/slewtocoordinatesasync", StringComparison.Ordinal) ? "!error:Invalid Dec" : null);
            var ws = new Mock<IWsBroadcaster>();
            ws.Setup(w => w.PublishAsync(It.IsAny<OpenAstroAra.Server.Contracts.WsEvents.WsEvent>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            var hub = new EquipmentFaultHub(ws.Object);
            var faults = new List<EquipmentFaultEvent>();
            hub.Subscribe(f => { lock (faults) { faults.Add(f); } });
            using var svc = new TelescopeService(faults: hub);
            var device = new DiscoveredDeviceDto(
                UniqueId: "mount-under-test", Name: "Bench mount", Type: DeviceType.Telescope,
                HostName: box.BaseUri.Host, IpAddress: box.BaseUri.Host, IpPort: box.BaseUri.Port,
                AlpacaDeviceNumber: 0, UseHttps: false);
            await svc.ConnectAsync(new ConnectRequestDto(device), null, CancellationToken.None);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (Math.Abs(((ITelescopeMediator)svc).GetInfo().RightAscension - 6.0) > 1e-9 && DateTime.UtcNow < deadline) {
                await Task.Delay(50);
            }
            var target = new Coordinates(Angle.ByHours(6.0), Angle.ByDegree(45.0), Epoch.J2000);
            Task<bool> Slew() => ((ITelescopeMediator)svc).SlewToCoordinatesAsync(target, CancellationToken.None);

            await Assert.ThrowsAsync<SequenceEntityFailedException>(
                () => suppressed ? TelescopeService.RunWithoutOpFaultsAsync(Slew) : Slew());
            lock (faults) {
                return [.. faults];
            }
        }

        [Test]
        public async Task A_refused_slew_publishes_an_op_fault_by_default() {
            var faults = await RefusedSlewFaultsAsync(suppressed: false);
            Assert.That(faults, Has.Count.EqualTo(1));
            Assert.That(faults[0].Kind, Is.EqualTo(EquipmentFaultKind.OpError));
        }

        [Test]
        public async Task A_refused_slew_run_without_op_faults_publishes_none() {
            Assert.That(await RefusedSlewFaultsAsync(suppressed: true), Is.Empty);
        }
    }
}