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

    /// <summary>#1149 — the §65.5 one-live-job-per-type policy, sharpened by an identity: a repeat
    /// for the same thing joins the running job; a request for something else is a conflict the
    /// caller reports (the centering endpoint's 409), never a silent join.</summary>
    [TestFixture]
    public class InMemoryBatchJobServiceIdentityTest {

        private static Func<Action<int>, CancellationToken, Task> Block(TaskCompletionSource gate) =>
            async (_, ct) => await gate.Task.WaitAsync(ct);

        [Test]
        public void The_same_identity_joins_the_running_job() {
            var jobs = new InMemoryBatchJobService(null);
            var gate = new TaskCompletionSource();
            try {
                var first = jobs.Enqueue("center", 1, Block(gate), "center:A");
                var again = jobs.Enqueue("center", 1, Block(gate), "center:A");
                Assert.That(again.JobId, Is.EqualTo(first.JobId));
            } finally {
                gate.SetResult();
            }
        }

        [Test]
        public void A_different_identity_is_a_conflict_that_names_the_running_job() {
            var jobs = new InMemoryBatchJobService(null);
            var gate = new TaskCompletionSource();
            try {
                var first = jobs.Enqueue("center", 1, Block(gate), "center:A");
                var ex = Assert.Throws<BatchJobConflictException>(() => jobs.Enqueue("center", 1, Block(gate), "center:B"));
                Assert.That(ex!.RunningJobId, Is.EqualTo(first.JobId));
                Assert.That(ex.RunningIdentity, Is.EqualTo("center:A"));
                Assert.That(ex.RequestedIdentity, Is.EqualTo("center:B"));
                Assert.That(jobs.GetJob(first.JobId)!.State, Is.Not.EqualTo("cancelled"), "the running job is untouched");
            } finally {
                gate.SetResult();
            }
        }

        [Test]
        public void A_null_identity_on_either_side_joins_as_before() {
            var jobs = new InMemoryBatchJobService(null);
            var gate = new TaskCompletionSource();
            try {
                var first = jobs.Enqueue("center", 1, Block(gate), "center:A");
                Assert.That(jobs.Enqueue("center", 1, Block(gate)).JobId, Is.EqualTo(first.JobId), "no identity requested");
                gate.SetResult();
                Assert.That(SpinWait.SpinUntil(() => jobs.GetJob(first.JobId)!.State == "complete", TimeSpan.FromSeconds(5)), Is.True);
                var gate2 = new TaskCompletionSource();
                try {
                    var untagged = jobs.Enqueue("center", 1, Block(gate2));
                    Assert.That(jobs.Enqueue("center", 1, Block(gate2), "center:Z").JobId, Is.EqualTo(untagged.JobId), "no identity running");
                } finally {
                    gate2.SetResult();
                }
            } finally {
                gate.TrySetResult();
            }
        }

        [Test]
        public async Task A_finished_job_for_another_identity_does_not_conflict() {
            var jobs = new InMemoryBatchJobService(null);
            var first = jobs.Enqueue("center", 1, static (_, _) => Task.CompletedTask, "center:A");
            Assert.That(SpinWait.SpinUntil(() => jobs.GetJob(first.JobId)!.State == "complete", TimeSpan.FromSeconds(5)), Is.True);

            var second = jobs.Enqueue("center", 1, static (_, _) => Task.CompletedTask, "center:B");

            Assert.That(second.JobId, Is.Not.EqualTo(first.JobId));
            await Task.Yield();
        }
    }
}
