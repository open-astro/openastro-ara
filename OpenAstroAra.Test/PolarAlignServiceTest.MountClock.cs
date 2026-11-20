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
using OpenAstroAra.Server.Contracts.Refusals;
using OpenAstroAra.Server.Services;
using System.Threading;

namespace OpenAstroAra.Test {

    public partial class PolarAlignServiceTest {

        // #2071 — the routine turns the mount against its sidereal time: with the mount's clock not verified it
        // is refused, whatever the user confirmed, before the lease, a Home, a slew or any motor move.
        [Test]
        public async System.Threading.Tasks.Task Start_with_the_mount_clock_not_verified_is_refused_before_anything_moves() {
            var fake = NewFake();
            var mount = NewMount();
            var gate = new Mock<IMountClockGate>();
            gate.Setup(g => g.MountClockMotionRefusal()).Returns("Mount clock not verified: the rig clock has not been synced from the app or a GPS.");
            using var svc = new PolarAlignService(fake, NullLogger<PolarAlignService>.Instance, new ScriptedSolver(), mount.Object,
                NewStore(), new WsRecorder(), log: null, new FakeFrameFetcher(), mountClock: gate.Object);

            var refusal = await Assert.ThrowsAsync<RefusalException>(() => svc.StartAsync(PolarAlignStages.Fine, new PolarAlignStartConfirmation(PolarAlignDirections.East), null, CancellationToken.None));

            Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.MountClockNotVerified));
            Assert.That(fake.LeaseCalls, Is.Empty, "a refused start must not acquire the lease");
            mount.Verify(m => m.FindHome(It.IsAny<System.IProgress<OpenAstroAra.Core.Model.ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
            mount.Verify(m => m.SlewToCoordinatesAsync(It.IsAny<OpenAstroAra.Astrometry.Coordinates>(), It.IsAny<CancellationToken>()), Times.Never);
            mount.Verify(m => m.MoveAxis(It.IsAny<OpenAstroAra.Core.Enums.TelescopeAxes>(), It.IsAny<double>()), Times.Never);
        }
    }
}
