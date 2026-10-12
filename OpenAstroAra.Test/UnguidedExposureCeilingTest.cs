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
using OpenAstroAra.Core.Model;
using OpenAstroAra.Sequencer.Conditions;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.Interfaces;
using OpenAstroAra.Sequencer.SequenceItem;
using OpenAstroAra.Sequencer.SequenceItem.Imaging;
using OpenAstroAra.Sequencer.SequenceItem.Telescope;
using OpenAstroAra.Sequencer.Trigger.Autofocus;
using OpenAstroAra.Sequencer.Trigger.Telescope;
using OpenAstroAra.Sequencer.Utility;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1264 — the unguided exposure ceiling's sequencer half: the ladder, the write-back into the
    /// imaging loops, the TrackingProbe instruction and the UnguidedExposureGuard trigger, on real factory
    /// prototypes with a scripted executor / frame-quality feed.</summary>
    [TestFixture]
    public class UnguidedExposureCeilingTest {

        private sealed class ScriptedProbe : ITrackingProbeExecutor {
            public TrackingProbeRequest? Request;
            public TrackingProbeResult Result = new(60, 3, "scripted");
            public Task<TrackingProbeResult> ProbeAsync(TrackingProbeRequest request, IProgress<ApplicationStatus> progress, CancellationToken token) {
                Request = request;
                return Task.FromResult(Result);
            }
        }

        private sealed class ScriptedFeed : IFrameQualityFeed {
            private long _seq;
            public FrameQuality? LatestLight { get; private set; }
            public List<(Guid, string)> Flags { get; } = new();
            public List<string> Notifications { get; } = new();
            public Guid Push(double eccentricity, double exposure = 120) {
                var id = Guid.NewGuid();
                LatestLight = new FrameQuality(id, ++_seq, eccentricity, 3.0, exposure, DateTimeOffset.UtcNow);
                return id;
            }
            public Task FlagFrameAsync(Guid frameId, string tag, CancellationToken ct) { Flags.Add((frameId, tag)); return Task.CompletedTask; }
            public Task NotifyAsync(string title, string message, CancellationToken ct) { Notifications.Add(title); return Task.CompletedTask; }
        }

        private static HeadlessSequencerFactory Factory(ITrackingProbeExecutor? probe = null, IFrameQualityFeed? feed = null) =>
            HeadlessSequencerFactory.WithDefaults(trackingProbeExecutor: probe, frameQualityFeed: feed);

        /// <summary>The client's target block: [goToTarget-ish item] → probe → "Imaging" loop (TakeExposure × N).</summary>
        private static (SequentialContainer Block, TrackingProbe Probe, SequentialContainer Loop, TakeExposure Exposure, LoopCondition Count)
                Tree(HeadlessSequencerFactory factory, double exposureSeconds, int frames, double maxExposure = 0, double? prior = null) {
            var block = factory.GetContainer<SequentialContainer>()!;
            var probe = (TrackingProbe)factory.GetItem<TrackingProbe>()!.Clone();
            probe.MaxExposure = maxExposure;
            probe.PriorSeconds = prior;
            var loop = (SequentialContainer)factory.GetContainer<SequentialContainer>()!.Clone();
            loop.Name = "Imaging";
            var exposure = (TakeExposure)factory.GetItem<TakeExposure>()!.Clone();
            exposure.ExposureTime = exposureSeconds;
            exposure.ImageType = "LIGHT";
            var count = (LoopCondition)factory.GetCondition<LoopCondition>()!.Clone();
            count.Iterations = frames;
            loop.Add(exposure);
            loop.Add(count);
            block.Add((ISequenceItem)factory.GetItem<TakeExposure>()!.Clone()); // a stand-in for the slew, BEFORE the probe
            ((IExposureItem)block.Items[0]).ImageType = "SNAPSHOT";
            block.Add(probe);
            block.Add(loop);
            return (block, probe, loop, exposure, count);
        }

        // ── ladder ──

        private static readonly double[] MessyRungs = { 120.0, 5, 5, -1, 60 };
        private static readonly double[] OnlyEight = { 8.0 };

        [Test]
        public void The_ladder_is_capped_at_the_planned_exposure_and_twice_the_prior() {
            Assert.That(UnguidedExposureLadder.Build(null, 300, null), Is.EqualTo(new[] { 10.0, 30, 60, 120, 300 }), "a planned sub above the ladder top is the final rung");
            Assert.That(UnguidedExposureLadder.Build(null, 120, null), Is.EqualTo(new[] { 10.0, 30, 60, 120 }));
            Assert.That(UnguidedExposureLadder.Build(null, 300, 100), Is.EqualTo(new[] { 10.0, 30, 60, 120 }), "the 2 × prior cap (200 s) still keeps the 300 s plan off the ladder");
            Assert.That(UnguidedExposureLadder.Build(null, 45, null), Is.EqualTo(new[] { 10.0, 30, 45 }), "planned 45 s: the rungs below it, then the plan itself as the final rung");
            Assert.That(UnguidedExposureLadder.Build(null, 300, 20), Is.EqualTo(new[] { 10.0, 30 }), "2 × a 20 s prior = 40 s cap");
            Assert.That(UnguidedExposureLadder.Build(null, 8, null), Is.EqualTo(OnlyEight), "planned shorter than every rung: measure at the plan itself");
            Assert.That(UnguidedExposureLadder.Build(MessyRungs, 0, null), Is.EqualTo(new[] { 5.0, 60, 120 }), "sorted, distinct, positive");
        }

        [Test]
        public void Steps_move_one_rung_and_stop_at_the_ends() {
            var ladder = UnguidedExposureLadder.DefaultRungs;
            Assert.That(UnguidedExposureLadder.StepDown(ladder, 120), Is.EqualTo(60));
            Assert.That(UnguidedExposureLadder.StepDown(ladder, 45), Is.EqualTo(30), "an off-ladder exposure steps to the rung below");
            Assert.That(UnguidedExposureLadder.StepDown(ladder, 10), Is.EqualTo(10), "lowest rung: no change");
            Assert.That(UnguidedExposureLadder.StepUp(ladder, 30, 300), Is.EqualTo(60));
            Assert.That(UnguidedExposureLadder.StepUp(ladder, 60, 90), Is.EqualTo(60), "the next rung would exceed the planned 90 s");
            Assert.That(UnguidedExposureLadder.StepUp(ladder, 120, 300), Is.EqualTo(120), "top rung");
        }

        // ── write-back ──

        [Test]
        public void The_ceiling_shortens_later_light_loops_and_holds_the_planned_integration() {
            var (block, probe, loop, exposure, count) = Tree(Factory(), exposureSeconds: 300, frames: 10);

            var changes = UnguidedExposureWriter.ApplyCeiling(block, probe, 60);

            Assert.That(changes, Has.Count.EqualTo(1));
            Assert.That(exposure.ExposureTime, Is.EqualTo(60));
            Assert.That(count.Iterations, Is.EqualTo(50), "300 s × 10 = 3000 s of integration → 60 s × 50");
            Assert.That(((IExposureItem)block.Items[0]).ExposureTime, Is.Not.EqualTo(60), "items before the probe are untouched");
        }

        [Test]
        public void A_ceiling_above_the_planned_exposure_changes_nothing() {
            var (block, probe, _, exposure, count) = Tree(Factory(), exposureSeconds: 60, frames: 10);
            Assert.That(UnguidedExposureWriter.ApplyCeiling(block, probe, 120), Is.Empty);
            Assert.That(exposure.ExposureTime, Is.EqualTo(60));
            Assert.That(count.Iterations, Is.EqualTo(10));
        }

        [Test]
        public void A_guard_step_mid_loop_rederives_only_the_remaining_frames() {
            var (_, _, loop, exposure, count) = Tree(Factory(), exposureSeconds: 120, frames: 20);
            count.CompletedIterations = 5; // 5 taken at 120 s; 15 × 120 s = 1800 s still owed

            var changes = UnguidedExposureWriter.SetLoopExposure(loop, 60);

            Assert.That(changes.Single().NewIterations, Is.EqualTo(5 + 30));
            Assert.That(exposure.ExposureTime, Is.EqualTo(60));
            Assert.That(count.Iterations, Is.EqualTo(35));

            // …and back up: 30 remaining × 60 s = 1800 s → 15 at 120 s.
            UnguidedExposureWriter.SetLoopExposure(loop, 120);
            Assert.That(count.Iterations, Is.EqualTo(5 + 15));
        }

        [Test]
        public void The_rewrite_rescales_the_loops_autofocus_cadence_so_the_time_cadence_holds() {
            // Review of #1318: the builder derives AfterExposures for the PLANNED sub (AF every 30 min =
            // every 6 × 300 s). Shortened to 45 s subs, the same 6 would refocus every 4.5 minutes.
            var factory = Factory();
            var (block, probe, loop, _, _) = Tree(factory, exposureSeconds: 300, frames: 10);
            var af = (AutofocusAfterExposures)factory.GetTrigger<AutofocusAfterExposures>()!.Clone();
            af.AfterExposures = 6;
            loop.Add(af);

            UnguidedExposureWriter.ApplyCeiling(block, probe, 45);
            Assert.That(af.AfterExposures, Is.EqualTo(40), "6 × 300 / 45");

            // …and a guard step back up rescales it the other way.
            UnguidedExposureWriter.SetLoopExposure(loop, 90);
            Assert.That(af.AfterExposures, Is.EqualTo(20), "40 × 45 / 90");
        }

        [Test]
        public void A_loop_with_two_light_exposures_has_its_count_scaled_once() {
            var factory = Factory();
            var (_, _, loop, exposure, count) = Tree(factory, exposureSeconds: 300, frames: 10);
            var second = (TakeExposure)factory.GetItem<TakeExposure>()!.Clone();
            second.ExposureTime = 300;
            second.ImageType = "LIGHT";
            loop.Add(second);

            var changes = UnguidedExposureWriter.CapLoop(loop, 60);

            Assert.That(changes, Has.Count.EqualTo(1), "one rewrite per loop");
            Assert.That(exposure.ExposureTime, Is.EqualTo(60));
            Assert.That(second.ExposureTime, Is.EqualTo(60));
            Assert.That(count.Iterations, Is.EqualTo(50), "not 250");
        }

        [Test]
        public void Only_light_exposures_are_rewritten() {
            var factory = Factory();
            var (_, _, loop, exposure, _) = Tree(factory, exposureSeconds: 300, frames: 4);
            var dark = (TakeExposure)factory.GetItem<TakeExposure>()!.Clone();
            dark.ExposureTime = 300;
            dark.ImageType = "DARK";
            loop.Add(dark);

            UnguidedExposureWriter.CapLoop(loop, 60);

            Assert.That(exposure.ExposureTime, Is.EqualTo(60));
            Assert.That(dark.ExposureTime, Is.EqualTo(300));
        }

        // ── TrackingProbe instruction ──

        [Test]
        public async Task The_probe_asks_for_the_capped_ladder_and_writes_the_measured_ceiling_into_the_loop() {
            var executor = new ScriptedProbe { Result = new TrackingProbeResult(45, 3, "measured") };
            var (_, probe, _, exposure, count) = Tree(Factory(executor), exposureSeconds: 300, frames: 10, maxExposure: 300, prior: 20);

            await probe.Execute(new Progress<ApplicationStatus>(), CancellationToken.None);

            Assert.That(executor.Request!.RungsSeconds, Is.EqualTo(new[] { 10.0, 30 }), "capped at 2 × the 20 s prior");
            Assert.That(executor.Request.PriorSeconds, Is.EqualTo(20));
            Assert.That(probe.CeilingSeconds, Is.EqualTo(45));
            Assert.That(exposure.ExposureTime, Is.EqualTo(45));
            Assert.That(count.Iterations, Is.EqualTo((int)Math.Ceiling(3000.0 / 45)));
        }

        [Test]
        public async Task An_inconclusive_probe_applies_the_prior_and_leaves_the_loop_alone_without_one() {
            var executor = new ScriptedProbe { Result = new TrackingProbeResult(null, 1, "one usable rung") };
            var withPrior = Tree(Factory(executor), exposureSeconds: 300, frames: 10, prior: 30);
            await withPrior.Probe.Execute(new Progress<ApplicationStatus>(), CancellationToken.None);
            Assert.That(withPrior.Probe.CeilingSeconds, Is.Null);
            Assert.That(withPrior.Exposure.ExposureTime, Is.EqualTo(30), "the spec-sheet prior stands");

            var noPrior = Tree(Factory(executor), exposureSeconds: 300, frames: 10);
            await noPrior.Probe.Execute(new Progress<ApplicationStatus>(), CancellationToken.None);
            Assert.That(noPrior.Exposure.ExposureTime, Is.EqualTo(300), "unknown mount, inconclusive probe: planned exposure kept");
        }

        [Test]
        public async Task An_unwired_probe_fails_loudly_instead_of_silently_skipping() {
            var (_, probe, _, _, _) = Tree(Factory(), exposureSeconds: 300, frames: 10);
            await Assert.ThatAsync(() => probe.Execute(new Progress<ApplicationStatus>(), CancellationToken.None), Throws.TypeOf<SequenceEntityFailedException>());
        }

        [Test]
        public void The_probe_estimates_its_own_duration_from_the_ladder() {
            var probe = new TrackingProbe { MaxExposure = 120 };
            Assert.That(probe.GetEstimatedDuration(), Is.EqualTo(TimeSpan.FromSeconds(10 + 30 + 60 + 120 + 4 * 5)));
        }

        // ── UnguidedExposureGuard trigger ──

        [Test]
        public async Task Two_trailed_frames_step_the_exposure_down_flag_them_and_notify_once() {
            var feed = new ScriptedFeed();
            var factory = Factory(feed: feed);
            var (_, _, loop, exposure, count) = Tree(factory, exposureSeconds: 120, frames: 10);
            var guard = (UnguidedExposureGuard)factory.GetTrigger<UnguidedExposureGuard>()!.Clone();
            guard.MaxExposure = 120;
            guard.SequenceBlockInitialize();

            var first = feed.Push(0.7);
            Assert.That(guard.ShouldTrigger(exposure, exposure), Is.False, "one trailed frame is not yet a trend");
            var second = feed.Push(0.65);
            Assert.That(guard.ShouldTrigger(exposure, exposure), Is.True);
            await guard.Execute(loop, new Progress<ApplicationStatus>(), CancellationToken.None);

            Assert.That(exposure.ExposureTime, Is.EqualTo(60));
            Assert.That(count.Iterations, Is.EqualTo(20), "10 × 120 s → 20 × 60 s");
            Assert.That(feed.Flags.Select(f => f.Item1), Is.EquivalentTo(new[] { first, second }));
            Assert.That(feed.Flags.Select(f => f.Item2), Is.All.EqualTo("trailed"));
            Assert.That(feed.Notifications, Has.Count.EqualTo(1));

            // A second step-down does not notify again.
            feed.Push(0.8, exposure: 60); guard.ShouldTrigger(exposure, exposure);
            feed.Push(0.8, exposure: 60); Assert.That(guard.ShouldTrigger(exposure, exposure), Is.True);
            await guard.Execute(loop, new Progress<ApplicationStatus>(), CancellationToken.None);
            Assert.That(exposure.ExposureTime, Is.EqualTo(30));
            Assert.That(feed.Notifications, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task Ten_round_frames_step_up_but_never_past_the_planned_exposure() {
            var feed = new ScriptedFeed();
            var factory = Factory(feed: feed);
            var (_, _, loop, exposure, _) = Tree(factory, exposureSeconds: 30, frames: 10);
            var guard = (UnguidedExposureGuard)factory.GetTrigger<UnguidedExposureGuard>()!.Clone();
            guard.MaxExposure = 60;
            guard.SequenceBlockInitialize();

            for (var i = 0; i < 9; i++) {
                feed.Push(0.2, exposure: 30);
                Assert.That(guard.ShouldTrigger(exposure, exposure), Is.False, $"frame {i + 1}");
            }
            feed.Push(0.2, exposure: 30);
            Assert.That(guard.ShouldTrigger(exposure, exposure), Is.True);
            await guard.Execute(loop, new Progress<ApplicationStatus>(), CancellationToken.None);
            Assert.That(exposure.ExposureTime, Is.EqualTo(60));

            for (var i = 0; i < 10; i++) {
                feed.Push(0.1, exposure: 60);
                Assert.That(guard.ShouldTrigger(exposure, exposure), Is.False, "already at the planned 60 s: no step past it");
            }
        }

        [Test]
        public async Task A_reading_taken_at_the_previous_exposure_length_joins_no_streak() {
            // Analysis lags a frame: right after a step-down the newest reading is still a 120 s frame.
            var feed = new ScriptedFeed();
            var factory = Factory(feed: feed);
            var (_, _, loop, exposure, _) = Tree(factory, exposureSeconds: 120, frames: 10);
            var guard = (UnguidedExposureGuard)factory.GetTrigger<UnguidedExposureGuard>()!.Clone();
            guard.MaxExposure = 120;
            guard.SequenceBlockInitialize();
            feed.Push(0.7, exposure: 120); guard.ShouldTrigger(exposure, exposure);
            feed.Push(0.7, exposure: 120); Assert.That(guard.ShouldTrigger(exposure, exposure), Is.True);
            await guard.Execute(loop, new Progress<ApplicationStatus>(), CancellationToken.None);
            Assert.That(exposure.ExposureTime, Is.EqualTo(60));

            feed.Push(0.7, exposure: 120); // the straggler from the old length
            Assert.That(guard.ShouldTrigger(exposure, exposure), Is.False);
            feed.Push(0.7, exposure: 60);
            Assert.That(guard.ShouldTrigger(exposure, exposure), Is.False, "first trailed frame at the NEW length — the straggler did not count");
            feed.Push(0.7, exposure: 60);
            Assert.That(guard.ShouldTrigger(exposure, exposure), Is.True);
        }

        [Test]
        public void A_stale_or_missing_analysis_is_not_judged_twice_and_a_so_so_frame_resets_both_streaks() {
            var feed = new ScriptedFeed();
            var factory = Factory(feed: feed);
            var (_, _, _, exposure, _) = Tree(factory, exposureSeconds: 120, frames: 10);
            var guard = (UnguidedExposureGuard)factory.GetTrigger<UnguidedExposureGuard>()!.Clone();
            guard.SequenceBlockInitialize();

            Assert.That(guard.ShouldTrigger(exposure, exposure), Is.False, "no analysis yet");
            feed.Push(0.7);
            Assert.That(guard.ShouldTrigger(exposure, exposure), Is.False);
            Assert.That(guard.ShouldTrigger(exposure, exposure), Is.False, "the same reading again (analysis lagging) does not count twice");
            feed.Push(0.4); // between good (0.3) and tolerance (0.5)
            Assert.That(guard.ShouldTrigger(exposure, exposure), Is.False);
            feed.Push(0.7);
            Assert.That(guard.ShouldTrigger(exposure, exposure), Is.False, "the bad streak restarted after the so-so frame");
            Assert.That(guard.ShouldTrigger(new SequentialContainer(), exposure), Is.False, "a non-exposure item never judges");
        }

        // ── registration / JSON ──

        [Test]
        public void Both_types_are_registered_and_round_trip_through_the_body_deserializer() {
            var factory = Factory();
            Assert.That(factory.Items.Select(i => i.GetType().Name), Does.Contain(nameof(TrackingProbe)));
            Assert.That(factory.Triggers.Select(t => t.GetType().Name), Does.Contain(nameof(UnguidedExposureGuard)));

            var deserializer = new SequenceBodyDeserializer(factory, logger: null);
            var body = JsonDocument.Parse("""
                {
                    "schemaVersion": "openastroara-sequence-v1",
                    "$type": "OpenAstroAra.Sequencer.Container.SequentialContainer, OpenAstroAra.Sequencer",
                    "Items": { "$type": "System.Collections.ObjectModel.ObservableCollection`1[[OpenAstroAra.Sequencer.SequenceItem.ISequenceItem, OpenAstroAra.Sequencer]], System.ObjectModel", "$values": [
                        { "$type": "OpenAstroAra.Sequencer.SequenceItem.Telescope.TrackingProbe, OpenAstroAra.Sequencer", "Rungs": [15, 45], "MaxExposure": 180, "PriorSeconds": 40 }
                    ] },
                    "Triggers": { "$type": "System.Collections.ObjectModel.ObservableCollection`1[[OpenAstroAra.Sequencer.Trigger.ISequenceTrigger, OpenAstroAra.Sequencer]], System.ObjectModel", "$values": [
                        { "$type": "OpenAstroAra.Sequencer.Trigger.Telescope.UnguidedExposureGuard, OpenAstroAra.Sequencer", "MaxExposure": 180, "EccentricityTolerance": 0.45 }
                    ] }
                }
                """).RootElement.Clone();

            var ok = deserializer.TryDeserialize(body, out var container, out var error);

            Assert.That(ok, Is.True, $"unexpected error: {error}");
            var probe = container!.Items.OfType<TrackingProbe>().Single();
            Assert.That(probe.Rungs, Is.EqualTo(new[] { 15.0, 45 }), "saved rungs replace the defaults");
            Assert.That(probe.MaxExposure, Is.EqualTo(180));
            Assert.That(probe.PriorSeconds, Is.EqualTo(40));
            var guard = ((SequentialContainer)container).Triggers.OfType<UnguidedExposureGuard>().Single();
            Assert.That(guard.EccentricityTolerance, Is.EqualTo(0.45));
            Assert.That(guard.Rungs, Is.EqualTo(UnguidedExposureLadder.DefaultRungs), "unspecified rungs keep the defaults");
        }
    }
}
