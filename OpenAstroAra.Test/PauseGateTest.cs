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
using OpenAstroAra.Sequencer.Utility;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §38 — the headless pause gate the execution strategies suspend on at
    /// instruction boundaries (see <see cref="PauseGate"/>).
    /// </summary>
    [TestFixture]
    public class PauseGateTest {

        [Test]
        public async Task Wait_returns_immediately_when_not_paused() {
            var gate = new PauseGate();
            var entered = false;
            gate.PauseEntered += (_, _) => entered = true;

            var wait = gate.WaitWhilePausedAsync(CancellationToken.None);
            await wait.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.That(wait.IsCompletedSuccessfully, Is.True);
            Assert.That(entered, Is.False, "no suspension happened, PauseEntered must not fire");
        }

        [Test]
        public async Task Wait_suspends_until_resume_and_reports_entry() {
            var gate = new PauseGate();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gate.PauseEntered += (_, _) => entered.TrySetResult();

            gate.RequestPause();
            Assert.That(gate.IsPauseRequested, Is.True);

            var wait = gate.WaitWhilePausedAsync(CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(wait.IsCompleted, Is.False, "wait must be suspended while paused");

            gate.Resume();
            await wait.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(wait.IsCompletedSuccessfully, Is.True);
            Assert.That(gate.IsPauseRequested, Is.False);
        }

        [Test]
        public async Task Cancellation_aborts_a_suspended_wait() {
            var gate = new PauseGate();
            using var cts = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gate.PauseEntered += (_, _) => entered.TrySetResult();

            gate.RequestPause();
            var wait = gate.WaitWhilePausedAsync(cts.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            await cts.CancelAsync();
            Assert.ThrowsAsync<TaskCanceledException>(() => wait.WaitAsync(TimeSpan.FromSeconds(2)));
            // The gate stays armed — abort wins over pause, it doesn't clear it.
            Assert.That(gate.IsPauseRequested, Is.True);
        }

        [Test]
        public async Task Request_is_idempotent_and_resume_without_pause_is_harmless() {
            var gate = new PauseGate();
            gate.Resume(); // never paused — must not throw
            gate.RequestPause();
            gate.RequestPause(); // second request joins the first
            var wait = gate.WaitWhilePausedAsync(CancellationToken.None);
            gate.Resume();
            await wait.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(wait.IsCompletedSuccessfully, Is.True);
        }

        [Test]
        public async Task Repause_racing_resume_holds_the_run_at_the_same_boundary() {
            var gate = new PauseGate();
            var entered = 0;
            var enteredTwice = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gate.PauseEntered += (_, _) => {
                if (Interlocked.Increment(ref entered) == 2) enteredTwice.TrySetResult();
            };

            gate.RequestPause();
            var wait = gate.WaitWhilePausedAsync(CancellationToken.None);
            // Resume, then immediately re-pause: the wait loop must re-check and
            // suspend again rather than sail through the boundary.
            gate.Resume();
            gate.RequestPause();
            await enteredTwice.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(wait.IsCompleted, Is.False, "re-armed gate must re-suspend the same boundary");

            gate.Resume();
            await wait.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(wait.IsCompletedSuccessfully, Is.True);
        }
    }
}
