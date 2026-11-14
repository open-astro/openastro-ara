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
using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Model.Equipment;
using OpenAstroAra.Sequencer.Conditions;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.SequenceItem.FilterWheel;
using OpenAstroAra.Sequencer.SequenceItem.FlatDevice;
using OpenAstroAra.Sequencer.SequenceItem.Imaging;
using OpenAstroAra.Sequencer.SequenceItem.Utility;
using OpenAstroAra.Sequencer.Serialization;
using OpenAstroAra.Sequencer.Utility.DateTimeProvider;
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

        /// <summary>The text of the Annotation the client puts in a target block set to End of night:
        /// the rig holds that choice nowhere else, and the Flats block must keep the filters of every
        /// such target (ADR 0010, amended 2026-11-11).</summary>
        private const string EndOfNightMarker = "Flats: end of night";

        /// <summary>The NINA <c>$type</c> of the dawn the client stops target blocks at.</summary>
        private static readonly string DawnType = $"{typeof(DawnProvider).FullName}, {typeof(DawnProvider).Assembly.GetName().Name}";

        /// <summary><see cref="SimpleBlock"/> as the client writes it in full: the target's J2000
        /// position on its horizon condition, the dawn stop, the plan's gain on every exposure, and its
        /// flats choice (After target: a flats item after the rows; End of night: the marker).</summary>
        private static SequentialContainer FullBlock(string name, double raDeg, double decDeg, int gain, string flats, bool dawn,
            params (string Filter, double Seconds, int Count)[] rows) {
            var factory = Factory();
            var block = SimpleBlock(name, rows);
            block.Conditions.OfType<AboveHorizonCondition>().Single().Data.Coordinates =
                new InputCoordinates(new Coordinates(Angle.ByDegree(raDeg), Angle.ByDegree(decDeg), Epoch.J2000));
            if (dawn) {
                // One block only: two equal providers in one tree share a serializer reference id here.
                var stop = (TimeCondition)factory.GetCondition<TimeCondition>().Clone();
                stop.SelectedProvider = stop.DateTimeProviders.OfType<DawnProvider>().First();
                block.Add(stop);
            }
            foreach (var exposure in block.Items.OfType<SequentialContainer>().SelectMany(l => l.Items.OfType<TakeExposure>())) {
                exposure.Gain = gain;
            }
            if (flats == "end_of_night") {
                var marker = (Annotation)factory.GetItem<Annotation>().Clone();
                marker.Text = EndOfNightMarker;
                block.Items.Insert(0, marker);
            } else if (flats == "after_target") {
                block.Add((FlatPanelFlats)factory.GetItem<FlatPanelFlats>().Clone());
            }
            return block;
        }

        /// <summary>The queue's end-of-night Flats block: one flat set per filter, after the targets.</summary>
        private static SequentialContainer FlatsBlock(params string[] filters) {
            var factory = Factory();
            var block = new SequentialContainer { Name = "Flats" };
            foreach (var filter in filters) {
                var sw = (SwitchFilter)factory.Items.OfType<SwitchFilter>().First().Clone();
                sw.Filter = new FilterInfo(filter, 0, 0);
                block.Add(sw);
                var set = (FlatPanelFlats)factory.GetItem<FlatPanelFlats>().Clone();
                set.FrameCount = 25;
                block.Add(set);
            }
            return block;
        }

        private sealed class FakeStore : ISequenceService {
            private readonly Guid _id;
            private JsonElement _body;
            private DateTimeOffset _modified = DateTimeOffset.UnixEpoch;
            public FakeStore(Guid id, JsonElement body) { _id = id; _body = body; }
            public Task<SequenceDto?> GetAsync(Guid id, CancellationToken ct) =>
                Task.FromResult<SequenceDto?>(id == _id
                    ? new SequenceDto(id, "Queue", null, DateTimeOffset.UnixEpoch, _modified, _body, null)
                    : null);
            public Task<SequenceDto?> ReplaceRunBodyAsync(Guid id, JsonElement body, CancellationToken ct) {
                _body = body;
                _modified = DateTimeOffset.UtcNow;
                return GetAsync(id, ct);
            }
            public Task<CursorPage<SequenceListItemDto>> ListAsync(int limit, string? cursor, CancellationToken ct) => throw new NotSupportedException();
            public Task<SequenceDto> CreateAsync(SequenceCreateRequestDto request, string? idempotencyKey, CancellationToken ct) => throw new NotSupportedException();
            public Task<SequenceUpdateResult> UpdateAsync(Guid id, SequenceUpdateRequestDto request, CancellationToken ct) => throw new NotSupportedException();
            public Task<SequenceDeleteResult> DeleteAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
            public Task<SequenceShareDto?> ShareExportAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        }

        private static SequencerService Service(Guid id, JsonElement body, FakeStore? store = null) =>
            new(new SequenceBodyDeserializer(Factory()), ws: null,
                sequencesResolver: () => store ?? new FakeStore(id, body), checkpoint: null);

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
        public async Task A_block_serves_what_a_write_sends_again() {
            // A second device's Add, Edit and Undo re-send the block: none of this may read back as
            // "none" or the profile's default because that device did not make it.
            var id = Guid.NewGuid();
            var root = new SequentialContainer { Name = "M31" };
            root.Add(FullBlock("M31", 10.65, 41.27, 200, "end_of_night", dawn: true, ("L", 120, 10), ("R", 120, 10)));
            root.Add(FullBlock("M42", 83.85, -5.39, 150, "after_target", dawn: false, ("L", 300, 6)));
            root.Add(FlatsBlock("L", "R"));
            var svc = Service(id, Serialize(root));

            var plan = await svc.GetPlanAsync(id, CancellationToken.None);

            Assert.That(plan, Is.Not.Null);
            Assert.That(plan!.Targets.Select(t => (t.Name, t.Calibration)),
                Is.EqualTo(new[] { ("M31", false), ("M42", false), ("Flats", true) }), "the Flats block keeps its square");
            var (m31, m42, flats) = (plan.Targets[0], plan.Targets[1], plan.Targets[2]);
            Assert.Multiple(() => {
                Assert.That(m31.RaDeg, Is.EqualTo(10.65).Within(1e-6), "J2000, from the horizon condition");
                Assert.That(m31.DecDeg, Is.EqualTo(41.27).Within(1e-6));
                Assert.That(m31.Gain, Is.EqualTo(200), "the plan's gain, from its exposures");
                Assert.That(m31.Flats, Is.EqualTo(SequencePlanFlats.EndOfNight), "from the marker");
                Assert.That(m31.DawnProvider, Is.EqualTo(DawnType), "the TimeCondition's provider");
                Assert.That(m42.Gain, Is.EqualTo(150));
                Assert.That(m42.Flats, Is.EqualTo(SequencePlanFlats.AfterTarget), "a flats item in the block");
                Assert.That(flats.RaDeg, Is.Null, "no sky");
                Assert.That(flats.Rows.Select(r => (r.FilterName, r.FramesTotal)), Is.EqualTo(new[] { ("L", 25), ("R", 25) }),
                    "one row per flat set, its filter from the SwitchFilter before it");
            });
        }

        [Test]
        public async Task The_run_state_names_the_block_the_daemon_runs_even_one_with_no_target() {
            // A block the horizon or the dawn ends early keeps its rows partial, so the client cannot
            // tell the current block from the counts; and current_target_name names no target-less
            // block (it keeps the last target it saw).
            var id = Guid.NewGuid();
            var root = new SequentialContainer { Name = "IC 5148" };
            root.Add(RunnableTarget("IC 5148", ("L Imaging", 1, 0)));
            var flats = new SequentialContainer { Name = "Flats" };
            flats.Add(new LoopCondition { Iterations = 1 });
            flats.Add(new WaitForTimeSpan { Time = 30 });
            root.Add(flats);
            var svc = Service(id, Serialize(root));
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            try {
                SequenceRunStateDto? state = null;
                for (var i = 0; i < 250 && state?.CurrentBlockName != "Flats"; i++) {
                    await Task.Delay(20);
                    state = await svc.GetRunStateAsync(id, CancellationToken.None);
                }

                Assert.That(state!.CurrentBlockName, Is.EqualTo("Flats"), "the block holding the running leaf");
                Assert.That(state.PlanFramesDone, Is.EqualTo(1), "the plan's loop counts summed: IC 5148's one L frame");
            } finally {
                await svc.AbortAsync(id, null, CancellationToken.None);
            }
        }

        [Test]
        public async Task A_loop_with_a_negative_count_still_serves_a_plan_and_a_run_state() {
            var id = Guid.NewGuid();
            var root = new SequentialContainer { Name = "IC 5148" };
            root.Add(RunnableTarget("IC 5148", ("L Imaging", -1, 30)));
            var svc = Service(id, Serialize(root));
            Assert.That((await svc.GetPlanAsync(id, CancellationToken.None))!.Targets.Single().Rows.Single().FramesTotal, Is.EqualTo(0));
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            try {
                await Task.Delay(300);
                var plan = await svc.GetPlanAsync(id, CancellationToken.None);
                var state = await svc.GetRunStateAsync(id, CancellationToken.None);
                Assert.That(plan, Is.Not.Null);
                Assert.That(state, Is.Not.Null, "the run state must not throw on a negative loop count");
            } finally {
                await svc.AbortAsync(id, null, CancellationToken.None);
            }
        }

        [Test]
        public async Task A_queue_edited_after_its_run_ended_serves_the_edited_body() {
            var id = Guid.NewGuid();
            var root = new SequentialContainer { Name = "IC 5148" };
            root.Add(RunnableTarget("IC 5148", ("L Imaging", 1, 0)));
            var store = new FakeStore(id, Serialize(root));
            var svc = Service(id, Serialize(root), store);
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            SequenceRunStateDto? state = null;
            for (var i = 0; i < 250 && !(state?.State is SequenceRunState.Completed); i++) {
                await Task.Delay(20);
                state = await svc.GetRunStateAsync(id, CancellationToken.None);
            }
            Assert.That(state?.State, Is.EqualTo(SequenceRunState.Completed));
            Assert.That((await svc.GetPlanAsync(id, CancellationToken.None))!.Targets, Has.Count.EqualTo(1));

            await Task.Delay(20);
            root.Add(RunnableTarget("NGC 253", ("R Imaging", 7, 0)));
            await store.ReplaceRunBodyAsync(id, Serialize(root), CancellationToken.None);

            var plan = await svc.GetPlanAsync(id, CancellationToken.None);
            Assert.That(plan!.Targets.Select(t => t.Name), Is.EqualTo(TwoTargets), "a second device's edit shows");
        }

        [Test]
        public async Task An_unknown_sequence_has_no_plan() {
            var svc = Service(Guid.NewGuid(), Serialize(new SequentialContainer()));
            Assert.That(await svc.GetPlanAsync(Guid.NewGuid(), CancellationToken.None), Is.Null, "the route answers 404");
        }
    }
}