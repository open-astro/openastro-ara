#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;
using NUnit.Framework;
using OpenAstroAra.Core.Model;
using OpenAstroAra.PlateSolving;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §18.I — POST /platesolve/center: range-validated 202 job enqueue, and the REAL job body
    /// (<see cref="PlateSolveEndpoints.CenterWork"/>) run through a live
    /// <see cref="InMemoryBatchJobService"/>: converge = complete, no-converge / solver-config /
    /// equipment faults = Failed with an honest reason (never a stranded "running" job), and
    /// per-solve-attempt progress ticks.
    /// </summary>
    [TestFixture]
    public class CenteringEndpointTest {

        private static readonly OpenAstroAra.Astrometry.Coordinates AnyTarget = new(
            5.5, 20.0, OpenAstroAra.Astrometry.Epoch.J2000, OpenAstroAra.Astrometry.Coordinates.RAType.Hours);

        private static Mock<ICenteringService> Centering(Func<CancellationToken, Task<PlateSolveResult>> behaviour,
                Action<IProgress<PlateSolveProgress>?>? captureProgress = null) {
            var centering = new Mock<ICenteringService>();
            centering.Setup(c => c.CenterOnTarget(
                    It.IsAny<OpenAstroAra.Astrometry.Coordinates>(),
                    It.IsAny<IProgress<PlateSolveProgress>?>(),
                    It.IsAny<IProgress<ApplicationStatus>?>(),
                    It.IsAny<CancellationToken>()))
                .Returns((OpenAstroAra.Astrometry.Coordinates _, IProgress<PlateSolveProgress>? sp,
                        IProgress<ApplicationStatus>? _, CancellationToken ct) => {
                    captureProgress?.Invoke(sp);
                    return behaviour(ct);
                });
            return centering;
        }

        private static Mock<IProfileService> Profile(int attempts) {
            var settings = new Mock<IPlateSolveSettings>();
            settings.SetupGet(s => s.NumberOfAttempts).Returns(attempts);
            var profileService = new Mock<IProfileService>();
            profileService.SetupGet(p => p.ActiveProfile.PlateSolveSettings).Returns(settings.Object);
            return profileService;
        }

        private static async Task<BatchJobDto> WaitForTerminalAsync(InMemoryBatchJobService jobs, Guid id) {
            for (var i = 0; i < 250; i++) { // up to ~5s
                var job = jobs.GetJob(id);
                if (job is not null && job.State is "complete" or "failed" or "cancelled") return job;
                await Task.Delay(20);
            }
            return jobs.GetJob(id)!;
        }

        // ─── the endpoint wiring ───

        [Test]
        public void An_out_of_range_target_is_rejected_before_anything_is_enqueued() {
            var jobs = new Mock<IBatchJobService>(MockBehavior.Strict); // any Enqueue = test failure
            foreach (var request in new CenterRequestDto?[] {
                null,
                new(RaHours: -0.1, DecDegrees: 0),
                new(RaHours: 24.0, DecDegrees: 0),
                new(RaHours: 5, DecDegrees: 90.5),
                new(RaHours: 5, DecDegrees: -90.5),
                new(RaHours: double.NaN, DecDegrees: 0),
                new(RaHours: 5, DecDegrees: double.NaN),
            }) {
                var result = PlateSolveEndpoints.CenterAsync(request,
                    Mock.Of<ICenteringService>(), jobs.Object, Profile(attempts: 3).Object);
                Assert.That(result, Is.InstanceOf<ProblemHttpResult>(), $"request {request} must be rejected");
                Assert.That(((ProblemHttpResult)result).StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
            }
        }

        [Test]
        public void A_valid_target_enqueues_a_center_job_sized_by_the_profiles_attempt_budget() {
            var jobs = new InMemoryBatchJobService(null);
            var result = PlateSolveEndpoints.CenterAsync(new CenterRequestDto(5.5, 20.0),
                Centering(_ => Task.FromResult(new PlateSolveResult())).Object, jobs, Profile(attempts: 4).Object);

            Assert.That(result, Is.InstanceOf<Accepted<BatchJobDto>>());
            var accepted = (Accepted<BatchJobDto>)result;
            Assert.That(accepted.Value!.JobType, Is.EqualTo("center"));
            Assert.That(accepted.Value.Total, Is.EqualTo(4), "job total = the profile's solve-attempt budget");
            Assert.That(accepted.Location, Is.EqualTo($"/api/v1/jobs/{accepted.Value.JobId}"),
                "the 202 must point at the pollable job resource");
        }

        [Test]
        public void A_nonsense_attempt_budget_still_yields_a_sane_one_step_job() {
            var jobs = new InMemoryBatchJobService(null);
            var result = PlateSolveEndpoints.CenterAsync(new CenterRequestDto(5.5, 20.0),
                Centering(_ => Task.FromResult(new PlateSolveResult())).Object, jobs, Profile(attempts: 0).Object);
            Assert.That(((Accepted<BatchJobDto>)result).Value!.Total, Is.EqualTo(1));
        }

        // ─── the job body ───

        [Test]
        public async Task A_converged_center_completes_the_job() {
            var jobs = new InMemoryBatchJobService(null);
            var centering = Centering(_ => Task.FromResult(new PlateSolveResult())); // Success = true
            var job = jobs.Enqueue("center", 3, PlateSolveEndpoints.CenterWork(centering.Object, AnyTarget, 3));

            var final = await WaitForTerminalAsync(jobs, job.JobId);
            Assert.That(final.State, Is.EqualTo("complete"));
            Assert.That(final.Done, Is.EqualTo(3), "a finished center settles the bar at total");
        }

        [Test]
        public async Task A_center_that_never_converges_fails_the_job_with_an_honest_reason() {
            var jobs = new InMemoryBatchJobService(null);
            var centering = Centering(_ => Task.FromResult(new PlateSolveResult { Success = false }));
            var job = jobs.Enqueue("center", 3, PlateSolveEndpoints.CenterWork(centering.Object, AnyTarget, 3));

            var final = await WaitForTerminalAsync(jobs, job.JobId);
            Assert.That(final.State, Is.EqualTo("failed"));
            Assert.That(final.ErrorMessage, Does.Contain("did not converge"));
        }

        [Test]
        public async Task A_solver_configuration_fault_fails_the_job_instead_of_stranding_it() {
            // PlateSolverConfigurationException is NOT on the job runner's failure allow-list —
            // unwrapped it would fault the worker task and leave the job "running" forever. The
            // work body must map it (and its user-facing message) onto a recorded failure.
            var jobs = new InMemoryBatchJobService(null);
            var centering = Centering(_ => throw new PlateSolverConfigurationException(
                "Cannot centre: no active profile is loaded."));
            var job = jobs.Enqueue("center", 3, PlateSolveEndpoints.CenterWork(centering.Object, AnyTarget, 3));

            var final = await WaitForTerminalAsync(jobs, job.JobId);
            Assert.That(final.State, Is.EqualTo("failed"));
            Assert.That(final.ErrorMessage, Does.Contain("no active profile"));
        }

        [Test]
        public async Task A_missing_solver_binary_fails_with_a_path_free_message() {
            var jobs = new InMemoryBatchJobService(null);
            var centering = Centering(_ => throw new System.IO.FileNotFoundException(
                "Could not find file '/home/joey/.local/astap/astap'."));
            var job = jobs.Enqueue("center", 3, PlateSolveEndpoints.CenterWork(centering.Object, AnyTarget, 3));

            var final = await WaitForTerminalAsync(jobs, job.JobId);
            Assert.That(final.State, Is.EqualTo("failed"));
            Assert.That(final.ErrorMessage, Does.Contain("solver path"));
            Assert.That(final.ErrorMessage, Does.Not.Contain("/home/"), "server-side paths must not leak to the wire");
        }

        [Test]
        public async Task A_cancelled_center_records_cancelled_not_failed() {
            var jobs = new InMemoryBatchJobService(null);
            var started = new TaskCompletionSource();
            var centering = Centering(async ct => {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return new PlateSolveResult();
            });
            var job = jobs.Enqueue("center", 3, PlateSolveEndpoints.CenterWork(centering.Object, AnyTarget, 3));

            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            jobs.TryCancel(job.JobId);
            var final = await WaitForTerminalAsync(jobs, job.JobId);
            Assert.That(final.State, Is.EqualTo("cancelled"));
        }

        [Test]
        public async Task Each_completed_solve_attempt_ticks_the_jobs_progress() {
            var jobs = new InMemoryBatchJobService(null);
            IProgress<PlateSolveProgress>? captured = null;
            var release = new TaskCompletionSource();
            var centering = Centering(async _ => {
                await release.Task;
                return new PlateSolveResult { Success = false };
            }, sp => captured = sp);
            var job = jobs.Enqueue("center", 3, PlateSolveEndpoints.CenterWork(centering.Object, AnyTarget, 3));

            for (var i = 0; i < 250 && captured is null; i++) await Task.Delay(20);
            Assert.That(captured, Is.Not.Null, "the work body must hand the loop a solve-progress sink");

            // Two solve attempts report in (the thumbnail-only interim reports don't count).
            captured!.Report(new PlateSolveProgress { Thumbnail = null });
            captured.Report(new PlateSolveProgress { PlateSolveResult = new PlateSolveResult { Success = false } });
            captured.Report(new PlateSolveProgress { PlateSolveResult = new PlateSolveResult { Success = false } });
            for (var i = 0; i < 250 && (jobs.GetJob(job.JobId)!.Done < 2); i++) await Task.Delay(20);
            Assert.That(jobs.GetJob(job.JobId)!.Done, Is.EqualTo(2), "one tick per completed solve attempt");

            release.TrySetResult();
            await WaitForTerminalAsync(jobs, job.JobId);
        }
    }
}
