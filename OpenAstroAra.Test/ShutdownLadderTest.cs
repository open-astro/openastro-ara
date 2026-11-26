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
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Equipment.MyTelescope;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#2142 — the shutdown ladder ends every routine on the rig before it moves the mount.</summary>
    [TestFixture]
    public class ShutdownLadderTest {

        private static Mock<ITelescopeMediator> ParkableMount() {
            var mount = new Mock<ITelescopeMediator>();
            mount.Setup(m => m.GetInfo()).Returns(new TelescopeInfo { Connected = true, CanPark = true, TrackingEnabled = true });
            mount.Setup(m => m.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<MoveAsker>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            return mount;
        }

        [Test]
        public async Task The_park_step_ends_a_center_under_way_first() {
            using var center = await RunningCenter.StartAsync();
            var mount = ParkableMount();
            var ladder = new ShutdownLadder(telescope: mount.Object, occupancy: new RigOccupancy().RegisterCenter(() => center.Service));

            var rest = await ladder.RestMountAsync();
            await center.ReleaseNextAttemptAsync();

            Assert.Multiple(() => {
                Assert.That(rest.Rest, Is.EqualTo(MountRest.Parked), rest.Step.Text);
                Assert.That(center.Slewed(), Is.False, "the Center slewed after the shutdown park");
            });
        }
    }
}
