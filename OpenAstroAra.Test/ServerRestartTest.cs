#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Hosting;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Test {

    [TestFixture]
    public class ServerRestartTest {
        private sealed class FakeRestarter : IDaemonRestarter {
            public readonly TaskCompletionSource Called = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public int Calls;
            public void Restart() { Interlocked.Increment(ref Calls); Called.TrySetResult(); }
        }

        [Test]
        public async Task RestartAsync_returns_accepted_then_calls_the_seam_once_after_the_notice() {
            var fake = new FakeRestarter();
            var svc = new PlaceholderServerStateService(restarter: fake);

            var accepted = await svc.RestartAsync("test", null, CancellationToken.None);

            Assert.That(accepted, Is.Not.Null);
            Assert.That(fake.Calls, Is.EqualTo(0), "the seam must wait for the 2 s notice");
            Assert.That(await Task.WhenAny(fake.Called.Task, Task.Delay(TimeSpan.FromSeconds(10))), Is.SameAs(fake.Called.Task));
            await Task.Delay(300);
            Assert.That(fake.Calls, Is.EqualTo(1));
        }

        private sealed class FakeHostApplicationLifetime : IHostApplicationLifetime {
            public int StopCalls;
            public CancellationToken ApplicationStarted => CancellationToken.None;
            public CancellationToken ApplicationStopping => CancellationToken.None;
            public CancellationToken ApplicationStopped => CancellationToken.None;
            public void StopApplication() => StopCalls++;
        }

        [Test]
        public void ExitDaemonRestarter_Restart_sets_exit_code_75_and_stops_the_host() {
            var lifetime = new FakeHostApplicationLifetime();
            var saved = Environment.ExitCode;
            try {
                new ExitDaemonRestarter(lifetime).Restart();

                Assert.That(Environment.ExitCode, Is.EqualTo(ExitDaemonRestarter.RestartExitCode));
                Assert.That(ExitDaemonRestarter.RestartExitCode, Is.EqualTo(75));
                Assert.That(lifetime.StopCalls, Is.EqualTo(1));
            } finally {
                Environment.ExitCode = saved;
            }
        }
    }
}