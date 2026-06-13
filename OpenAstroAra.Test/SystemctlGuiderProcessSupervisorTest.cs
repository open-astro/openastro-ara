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
using NUnit.Framework;
using OpenAstroAra.Server.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// The <c>systemctl</c> seam's no-crash contract. It's environment-dependent (the unit isn't
    /// installed on dev/CI), so we only assert it degrades safely: a defined status, never a throw,
    /// and a no-op restart. The §63.3 decision logic is covered by
    /// <see cref="GuiderRecoveryCoordinatorTest"/> against a fake supervisor.
    /// </summary>
    [TestFixture]
    public class SystemctlGuiderProcessSupervisorTest {

        private static SystemctlGuiderProcessSupervisor NewSupervisor() =>
            new(NullLogger<SystemctlGuiderProcessSupervisor>.Instance);

        [Test]
        public async Task QueryStatusAsync_returns_a_defined_status_and_never_throws() {
            var supervisor = NewSupervisor();
            // On a host without the openastro-phd2 unit (every dev/CI box) this is Unknown or Inactive;
            // the contract under test is simply "a valid enum, no exception".
            var status = await supervisor.QueryStatusAsync(CancellationToken.None);
            Assert.That(Enum.IsDefined(status), Is.True);
        }

        [Test]
        public void RequestRestart_does_not_throw_when_systemctl_is_unavailable() {
            var supervisor = NewSupervisor();
            Assert.DoesNotThrow(supervisor.RequestRestart);
        }

        [Test]
        public void RequestStart_does_not_throw_when_systemctl_is_unavailable() {
            var supervisor = NewSupervisor();
            Assert.DoesNotThrow(supervisor.RequestStart);
        }
    }
}
