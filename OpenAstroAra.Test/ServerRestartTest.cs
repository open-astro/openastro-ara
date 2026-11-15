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
using OpenAstroAra.Server.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

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

        [Test]
        public void ExitDaemonRestarter_uses_a_non_zero_exit_code() {
            Assert.That(ExitDaemonRestarter.RestartExitCode, Is.Not.EqualTo(0));
        }
    }
}