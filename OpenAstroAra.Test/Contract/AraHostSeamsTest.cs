#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Test.Contract {

    /// <summary>The contract host boots Program.Main's own wiring, so it must swap the systemctl seams the
    /// same way the BuildApp hosts do (<see cref="TestHostSeams"/>): with no guider reachable it would
    /// otherwise run <c>systemctl start openastro-guider</c> and pop a polkit password dialog.</summary>
    [TestFixture]
    [Category("IO")]
    [NonParallelizable] // AraHost sets process-wide state (profile dir, Serilog, Notifier)
    public class AraHostSeamsTest {

        [Test]
        public void FakeSystemctlSeams_are_what_AraHost_resolves() {
            using var host = new AraHost();

            Assert.Multiple(() => {
                Assert.That(host.Services.GetRequiredService<IGuiderProcessSupervisor>(),
                    Is.TypeOf<TestHostSeams.FakeGuiderProcessSupervisor>());
                Assert.That(host.Services.GetRequiredService<IHostPoweroffStarter>(),
                    Is.TypeOf<TestHostSeams.FakeHostPoweroffStarter>());
            });
        }
    }
}