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
using OpenAstroAra.Sequencer.SequenceItem.Autofocus;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1299 — while the Bahtinov readout runs the mask is on: an autofocus run and a sequence start are
    /// refused with 409 <c>bahtinov_focus_active</c>, and go ahead once it stops.</summary>
    [TestFixture]
    public class BahtinovRunGuardTest {

        private static Mock<IBahtinovFocusService> Readout(bool active) {
            var readout = new Mock<IBahtinovFocusService>();
            readout.SetupGet(r => r.IsActive).Returns(active);
            return readout;
        }

        private static void AssertMaskOn(IResult result, string action) {
            var problem = result as ProblemHttpResult;
            Assert.That(problem, Is.Not.Null, "a Problem response");
            Assert.That(problem!.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict));
            Assert.That(problem.ProblemDetails.Title, Is.EqualTo(BahtinovFocusEndpoints.ActiveProblemTitle));
            Assert.That(BahtinovFocusEndpoints.ActiveProblemTitle, Is.EqualTo("bahtinov_focus_active"), "the wire token");
            Assert.That(problem.ProblemDetails.Detail, Does.Contain(action));
        }

        [Test]
        public void Autofocus_is_refused_while_the_readout_runs_and_never_enqueued() {
            var jobs = new Mock<IBatchJobService>(MockBehavior.Strict);
            var result = EquipmentEndpoints.RunAutofocus(new Mock<IAutofocusExecutor>().Object, jobs.Object,
                new InMemoryProfileStore(), new AutofocusRunTracker(), Readout(active: true).Object);
            AssertMaskOn(result, "running autofocus");
            jobs.VerifyNoOtherCalls(); // no sweep was queued
        }

        [Test]
        public void Autofocus_runs_once_the_readout_has_stopped() {
            var executor = new Mock<IAutofocusExecutor>();
            executor.Setup(e => e.RunAutofocusAsync(It.IsAny<IProgress<OpenAstroAra.Core.Model.ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            var result = EquipmentEndpoints.RunAutofocus(executor.Object, new InMemoryBatchJobService(null),
                new InMemoryProfileStore(), new AutofocusRunTracker(), Readout(active: false).Object);
            Assert.That(result, Is.InstanceOf<Accepted<BatchJobDto>>());
        }

        [Test]
        public async Task A_sequence_start_is_refused_while_the_readout_runs() {
            var sequencer = new Mock<ISequencerService>(MockBehavior.Strict);
            var result = await SequenceEndpoints.StartSequenceAsync(Guid.NewGuid(), new SequenceStartRequestDto(DryRun: false, StartFromInstructionIndex: null, ContinueOnRecoverableErrors: false), null,
                sequencer.Object, Readout(active: true).Object, CancellationToken.None);
            AssertMaskOn(result, "starting a sequence");
            sequencer.VerifyNoOtherCalls();
        }

        [Test]
        public async Task A_sequence_starts_once_the_readout_has_stopped() {
            var id = Guid.NewGuid();
            var accepted = new OperationAcceptedDto(Guid.NewGuid(), "sequence.start", DateTimeOffset.UtcNow, null);
            var sequencer = new Mock<ISequencerService>();
            sequencer.Setup(s => s.StartAsync(id, It.IsAny<SequenceStartRequestDto>(), null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(accepted);
            var result = await SequenceEndpoints.StartSequenceAsync(id, new SequenceStartRequestDto(DryRun: false, StartFromInstructionIndex: null, ContinueOnRecoverableErrors: false), null,
                sequencer.Object, Readout(active: false).Object, CancellationToken.None);
            Assert.That((result as Accepted<OperationAcceptedDto>)?.Value, Is.SameAs(accepted));
        }
    }
}
