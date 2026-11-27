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
using OpenAstroAra.Astrometry;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#2139 — the Move Admission table's edges: an asker with no row for a move cannot ask for it.</summary>
    [TestFixture]
    public class MoveAdmissionTest {

        [Test]
        public async Task An_asker_with_no_row_for_a_move_cannot_ask_for_it() {
            using var rig = await FakeMountRig.ConnectAsync();
            var target = new Coordinates(5.0, 30.0, Epoch.J2000, Coordinates.RAType.Hours);

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rig.Service.SlewToCoordinatesAsync(target, MoveAsker.Shutdown, CancellationToken.None),
                "the shutdown ladder parks and homes; it has no GoTo");
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rig.Service.FindHome(new Progress<OpenAstroAra.Core.Model.ApplicationStatus>(), MoveAsker.SafetyPark, CancellationToken.None),
                "a safety move parks; it has no Find Home");
            await Task.Delay(100);
            Assert.That((rig.Fake.Slews, rig.Fake.Homes), Is.EqualTo((0, 0)), "nothing reached the mount");
        }
    }
}
