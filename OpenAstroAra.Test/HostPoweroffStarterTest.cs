#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"


using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Test {

    [TestFixture]
    public class HostPoweroffStarterTest {
        private static readonly string[] ExpectedArgs = ["--no-ask-password", "start", "--no-block", "openastroara-poweroff.service"];

        private static (SystemctlHostPoweroffStarter Starter, List<(string File, string[] Args)> Calls) Make(int exit, string output) {
            var calls = new List<(string, string[])>();
            var starter = new SystemctlHostPoweroffStarter((file, args, _) => {
                calls.Add((file, args));
                return Task.FromResult((exit, output));
            });
            return (starter, calls);
        }

        [Test]
        public async Task StartAsync_runs_exactly_systemctl_no_ask_password_start_no_block_of_the_poweroff_unit() {
            var (starter, calls) = Make(0, string.Empty);

            var result = await starter.StartAsync(CancellationToken.None);

            Assert.Multiple(() => {
                Assert.That(result.Started, Is.True);
                Assert.That(calls, Has.Count.EqualTo(1));
                Assert.That(calls[0].File, Is.EqualTo("systemctl"));
                Assert.That(calls[0].Args, Is.EqualTo(ExpectedArgs));
            });
        }

        [Test]
        public async Task StartAsync_reports_a_non_zero_exit_as_a_failure_with_the_output() {
            var (starter, _) = Make(1, " Access denied \n");

            var result = await starter.StartAsync(CancellationToken.None);

            Assert.Multiple(() => {
                Assert.That(result.Started, Is.False);
                Assert.That(result.Message, Is.EqualTo("Access denied"));
            });
        }

        [Test]
        public async Task StartAsync_gives_a_message_when_a_failed_start_printed_nothing() {
            var (starter, _) = Make(-1, string.Empty);

            var result = await starter.StartAsync(CancellationToken.None);

            Assert.Multiple(() => {
                Assert.That(result.Started, Is.False);
                Assert.That(result.Message, Is.Not.Empty);
            });
        }
    }
}