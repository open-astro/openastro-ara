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
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// The <c>systemctl</c> seam. Start and restart are asserted through the process seam only: a real
    /// <c>systemctl start|restart openastro-guider</c> from a test run reaches polkit, and on a Linux
    /// desktop with an auth agent that popped a password dialog on every run. The read-only is-active
    /// call needs no authorisation, so its no-crash contract still runs against the real process.
    /// The §63.3 decision logic is covered by <see cref="GuiderRecoveryCoordinatorTest"/> against a
    /// fake supervisor.
    /// </summary>
    [TestFixture]
    public class SystemctlGuiderProcessSupervisorTest {
        private static readonly string[] StartArgs = ["--no-ask-password", "start", "openastro-guider"];
        private static readonly string[] RestartArgs = ["--no-ask-password", "restart", "openastro-guider"];
        private static readonly string[] IsActiveArgs = ["--no-ask-password", "is-active", "openastro-guider"];

        private static SystemctlGuiderProcessSupervisor NewSupervisor(List<string[]> launched,
                Func<string[], CancellationToken, Task<string?>>? readStdout = null, Action<string[]>? launch = null) =>
            new(NullLogger<SystemctlGuiderProcessSupervisor>.Instance,
                launch ?? launched.Add,
                readStdout ?? ((_, _) => Task.FromResult<string?>(null)));

        [Test]
        public async Task QueryStatusAsync_returns_a_defined_status_and_never_throws() {
            var supervisor = new SystemctlGuiderProcessSupervisor(NullLogger<SystemctlGuiderProcessSupervisor>.Instance);
            // On a host without the openastro-guider unit (every dev/CI box) this is Unknown or Inactive;
            // the contract under test is simply "a valid enum, no exception".
            var status = await supervisor.QueryStatusAsync(CancellationToken.None);
            Assert.That(Enum.IsDefined(status), Is.True);
        }

        [Test]
        public void Unit_is_the_one_the_guider_deb_ships() {
            // openastro-guider's debian/ ships openastro-guider.service; the old "openastro-phd2" name
            // made every is-active read Unknown and every restart a silent no-op on a real Pi.
            Assert.That(SystemctlGuiderProcessSupervisor.Unit, Is.EqualTo("openastro-guider"));
        }

        [Test]
        public void RequestStart_runs_systemctl_no_ask_password_start_of_the_guider_unit() {
            var launched = new List<string[]>();
            NewSupervisor(launched).RequestStart();
            Assert.That(launched, Is.EqualTo(new[] { StartArgs }));
        }

        [Test]
        public void RequestRestart_runs_systemctl_no_ask_password_restart_of_the_guider_unit() {
            var launched = new List<string[]>();
            NewSupervisor(launched).RequestRestart();
            Assert.That(launched, Is.EqualTo(new[] { RestartArgs }));
        }

        [Test]
        public async Task QueryStatusAsync_runs_systemctl_no_ask_password_is_active_of_the_guider_unit() {
            string[]? asked = null;
            var supervisor = NewSupervisor([], (args, _) => {
                asked = args;
                return Task.FromResult<string?>("active\n");
            });

            var status = await supervisor.QueryStatusAsync(CancellationToken.None);

            Assert.Multiple(() => {
                Assert.That(asked, Is.EqualTo(IsActiveArgs));
                Assert.That(status, Is.EqualTo(GuiderProcessStatus.Active));
            });
        }

        [TestCase("activating", GuiderProcessStatus.Activating)]
        [TestCase("reloading", GuiderProcessStatus.Activating)]
        [TestCase("failed", GuiderProcessStatus.Failed)]
        [TestCase("inactive", GuiderProcessStatus.Inactive)]
        [TestCase("deactivating", GuiderProcessStatus.Inactive)]
        [TestCase("unknown-word", GuiderProcessStatus.Unknown)]
        public async Task QueryStatusAsync_classifies_the_is_active_word(string word, GuiderProcessStatus expected) {
            var supervisor = NewSupervisor([], (_, _) => Task.FromResult<string?>(word + "\n"));
            Assert.That(await supervisor.QueryStatusAsync(CancellationToken.None), Is.EqualTo(expected));
        }

        [Test]
        public async Task QueryStatusAsync_is_Unknown_when_systemctl_cannot_run() {
            var supervisor = NewSupervisor([], (_, _) => throw new Win32Exception(2, "No such file or directory"));
            Assert.That(await supervisor.QueryStatusAsync(CancellationToken.None), Is.EqualTo(GuiderProcessStatus.Unknown));
        }

        [Test]
        public void RequestStart_and_RequestRestart_do_not_throw_when_systemctl_is_unavailable() {
            var supervisor = NewSupervisor([], launch: _ => throw new Win32Exception(2, "No such file or directory"));
            Assert.Multiple(() => {
                Assert.DoesNotThrow(supervisor.RequestStart);
                Assert.DoesNotThrow(supervisor.RequestRestart);
            });
        }
    }
}