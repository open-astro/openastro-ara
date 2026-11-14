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
using OpenAstroAra.Core.Model.Equipment;
using OpenAstroAra.Sequencer.Conditions;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.SequenceItem.FilterWheel;
using OpenAstroAra.Sequencer.SequenceItem.Imaging;
using OpenAstroAra.Sequencer.SequenceItem.Utility;
using OpenAstroAra.Sequencer.Serialization;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #1875 slice 1 — the daemon serves the queued plan (each target block, its loop rows, and each
    /// loop's own count), so every client rebuilds Simple Mode's queue from the rig instead of from the
    /// memory of the client that queued it. A row's done count is its loop's completed iterations,
    /// never the session's frame count.
    /// </summary>
    [TestFixture]
    public class SequencePlanTest {

        private static readonly string[] TwoTargets = ["IC 5148", "NGC 253"];

        private static readonly SequenceStartRequestDto StartReq = new(DryRun: false, StartFromInstructionIndex: null, ContinueOnRecoverableErrors: false);

        /// <summary>The headless factory over a profile whose wheel holds L, R and G: a SwitchFilter
        /// resolves its filter against the wheel on load, as on the rig.</summary>
        private static HeadlessSequencerFactory Factory() {
            var profile = new HeadlessProfileService();
            var filters = profile.ActiveProfile.FilterWheelSettings.FilterWheelFilters;
            filters.Clear();
            filters.Add(new FilterInfo("L", 0, 0));
            filters.Add(new FilterInfo("R", 0, 1));
            filters.Add(new FilterInfo("G", 0, 2));
            return HeadlessSequencerFactory.WithDefaults(profileService: profile);
        }

        private static JsonElement Serialize(SequentialContainer root) {
            var converter = new SequenceJsonConverter(Factory());
            using var doc = JsonDocument.Parse(converter.Serialize(root));
            return doc.RootElement.Clone();
        }

        /// <summary>A Simple Mode target block as the client builds it: a container named after the
        /// target, run once (LoopCondition 1) and ended by the horizon, holding one
        /// "&lt;filter&gt; Imaging" loop per row, each after its SwitchFilter.</summary>
        private static SequentialContainer SimpleBlock(string name, params (string Filter, double Seconds, int Count)[] rows) {
            var factory = Factory();
            var block = new SequentialContainer { Name = name };
            block.Add(new LoopCondition { Iterations = 1 });
            block.Add((AboveHorizonCondition)factory.GetCondition<AboveHorizonCondition>().Clone());
            foreach (var (filter, seconds, count) in rows) {
                var sw = (SwitchFilter)factory.Items.OfType<SwitchFilter>().First().Clone();
                sw.Filter = new FilterInfo(filter, 0, 0);
                block.Add(sw);
                var exposure = (TakeExposure)factory.Items.OfType<TakeExposure>().First().Clone();
                exposure.ExposureTime = seconds;
                exposure.ImageType = "LIGHT";
                var loop = new SequentialContainer { Name = $"{filter} Imaging" };
                loop.Add(new LoopCondition { Iterations = count });
                loop.Add(exposure);
                block.Add(loop);
            }
            return block;
        }

        /// <summary>A target block that runs with no equipment: a DSO container (named by its target,
        /// no time-dependent condition) whose loops wait instead of exposing.</summary>
        private static DeepSkyObjectContainer RunnableTarget(string name, params (string Row, int Count, int WaitSeconds)[] rows) {
            var dso = new DeepSkyObjectContainer(new HeadlessProfileService());
            dso.Name = name;
            dso.Target.TargetName = name;
            foreach (var (row, count, wait) in rows) {
                var loop = new SequentialContainer { Name = row };
                loop.Add(new LoopCondition { Iterations = count });
                loop.Add(new WaitForTimeSpan { Time = wait });
                dso.Add(loop);
            }
            return dso;
        }

        private sealed class FakeStore : ISequenceService {
            private readonly Guid _id;
            private JsonElement _body;
            public FakeStore(Guid id, JsonElement body) { _id = id; _body = body; }
            public Task<SequenceDto?> GetAsync(Guid id, CancellationToken ct) =>
                Task.FromResult<SequenceDto?>(id == _id
                    ? new SequenceDto(id, "Queue", null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, _body, null)
                    : null);
            public Task<SequenceDto?> ReplaceRunBodyAsync(Guid id, JsonElement body, CancellationToken ct) {
                _body = body;
                return GetAsync(id, ct);
            }
            public Task<CursorPage<SequenceListItemDto>> ListAsync(int limit, string? cursor, CancellationToken ct) => throw new NotSupportedException();
            public Task<SequenceDto> CreateAsync(SequenceCreateRequestDto request, string? idempotencyKey, CancellationToken ct) => throw new NotSupportedException();
            public Task<SequenceUpdateResult> UpdateAsync(Guid id, SequenceUpdateRequestDto request, CancellationToken ct) => throw new NotSupportedException();
            public Task<SequenceDeleteResult> DeleteAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
            public Task<SequenceShareDto?> ShareExportAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        }

        private static SequencerService Service(Guid id, JsonElement body) =>
            new(new SequenceBodyDeserializer(Factory()), ws: null,
                sequencesResolver: () => new FakeStore(id, body), checkpoint: null);

        /// <summary>Polls the served plan until <paramref name="reached"/> holds (at most ~5 s).</summary>
        private static async Task<SequencePlanDto?> PlanWhenAsync(SequencerService svc, Guid id, Func<SequencePlanDto, bool> reached) {
            SequencePlanDto? plan = null;
            for (var i = 0; i < 250; i++) {
                plan = await svc.GetPlanAsync(id, CancellationToken.None);
                if (plan is not null && reached(plan)) return plan;
                await Task.Delay(20);
            }
            return plan;
        }

        [Test]
        public async Task A_queued_plan_is_served_from_the_stored_body_before_it_starts() {
            // Add to queue makes the run without starting it: a second device must still read the plan.
            var id = Guid.NewGuid();
            var root = new SequentialContainer { Name = "IC 5148" };
            root.Add(SimpleBlock("IC 5148", ("L", 60, 240)));
            root.Add(SimpleBlock("NGC 253", ("R", 120, 10), ("G", 120, 12)));
            var svc = Service(id, Serialize(root));

            var plan = await svc.GetPlanAsync(id, CancellationToken.None);

            Assert.That(plan, Is.Not.Null);
            Assert.That(plan!.RunId, Is.Null, "not started: no run");
            Assert.That(plan.Targets.Select(t => t.Name), Is.EqualTo(TwoTargets), "the blocks, in queue order");
            var l = plan.Targets[0].Rows.Single();
            Assert.Multiple(() => {
                Assert.That(l.FilterName, Is.EqualTo("L"), "the row's filter, from its SwitchFilter");
                Assert.That(l.ExposureSeconds, Is.EqualTo(60));
                Assert.That(l.FramesTotal, Is.EqualTo(240), "the loop's own iteration count, as typed");
                Assert.That(l.FramesDone, Is.EqualTo(0));
            });
            Assert.That(plan.Targets[1].Rows.Select(r => (r.FilterName, r.FramesTotal)),
                Is.EqualTo(new[] { ("R", 10), ("G", 12) }));
        }

        [Test]
        public async Task A_row_counts_its_own_loop_not_the_session_frame_count() {
            // Rig night 2026-11-13: the loop read 0/240 while the app showed the session's 16 frames.
            var id = Guid.NewGuid();
            var root = new SequentialContainer { Name = "IC 5148" };
            root.Add(RunnableTarget("IC 5148", ("L Imaging", 2, 0), ("R Imaging", 5, 30)));
            var svc = Service(id, Serialize(root));
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            try {
                var plan = await PlanWhenAsync(svc, id, p => p.Targets.Count == 1 && p.Targets[0].Rows.Count == 2 && p.Targets[0].Rows[0].FramesDone == 2);

                Assert.That(plan, Is.Not.Null);
                Assert.That(plan!.RunId, Is.Not.Null, "a running plan names its run");
                var rows = plan.Targets.Single().Rows;
                Assert.That(rows.Select(r => (r.FramesDone, r.FramesTotal)), Is.EqualTo(new[] { (2, 2), (0, 5) }),
                    "each row's done count is its loop's completed iterations");
                var state = await svc.GetRunStateAsync(id, CancellationToken.None);
                Assert.That(state!.FramesCaptured, Is.EqualTo(0),
                    "no frame was filed, so a count taken from the session would read 0, not 2");
            } finally {
                await svc.AbortAsync(id, null, CancellationToken.None);
            }
        }

        [Test]
        public async Task A_target_added_mid_run_joins_the_served_plan() {
            // Add to queue while imaging is a live edit of the running tree: the plan follows the tree.
            var id = Guid.NewGuid();
            var root = new SequentialContainer { Name = "IC 5148" };
            root.Add(RunnableTarget("IC 5148", ("L Imaging", 3, 30)));
            var svc = Service(id, Serialize(root));
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            try {
                await PlanWhenAsync(svc, id, p => p.RunId is not null);
                var block = Serialize(RunnableTarget("NGC 253", ("R Imaging", 7, 0)));
                var edit = await svc.AddRunItemAsync(id, new SequenceRunItemAddRequestDto(ParentPath: [], Index: null, Item: block), null, CancellationToken.None);
                Assert.That(edit.Outcome, Is.EqualTo(SequenceLiveEditOutcome.Applied), edit.Reason);

                var plan = await svc.GetPlanAsync(id, CancellationToken.None);

                Assert.That(plan!.Targets.Select(t => t.Name), Is.EqualTo(TwoTargets));
                Assert.That(plan.Targets[1].Rows.Single().FramesTotal, Is.EqualTo(7));
                Assert.That(plan.Targets[1].Rows.Single().FramesDone, Is.EqualTo(0));
            } finally {
                await svc.AbortAsync(id, null, CancellationToken.None);
            }
        }

        [Test]
        public async Task An_unknown_sequence_has_no_plan() {
            var svc = Service(Guid.NewGuid(), Serialize(new SequentialContainer()));
            Assert.That(await svc.GetPlanAsync(Guid.NewGuid(), CancellationToken.None), Is.Null, "the route answers 404");
        }
    }
}