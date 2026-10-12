#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Moq;
using NUnit.Framework;
using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.Interfaces;
using OpenAstroAra.Sequencer.SequenceItem;
using OpenAstroAra.Sequencer.SequenceItem.Imaging;
using OpenAstroAra.Sequencer.SequenceItem.Platesolving;
using OpenAstroAra.Sequencer.Serialization;
using OpenAstroAra.Sequencer.Trigger.Platesolving;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using OpenAstroAra.PlateSolving;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1195 — NINA's Center After Drift, re-ported on the saved-light solve feed.</summary>
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class CenterAfterDriftTriggerTest {

        private static readonly Coordinates Target = new(Angle.ByHours(5.5), Angle.ByDegree(-5.4), Epoch.J2000);

        /// <summary>A feed whose solves are scripted per frame id: a coordinate, or null for "did not solve".</summary>
        private sealed class ScriptedSolveFeed : IFrameSolveFeed {
            private long _seq;
            private readonly Dictionary<Guid, Coordinates?> _solves = new();
            public SavedLight? LatestLight { get; private set; }
            public List<Guid> SolveRequests { get; } = new();

            public Guid Save(Coordinates? solvesTo) {
                var id = Guid.NewGuid();
                _solves[id] = solvesTo;
                LatestLight = new SavedLight(id, ++_seq, DateTimeOffset.UtcNow);
                return id;
            }

            public Task<Coordinates?> SolveFrameAsync(Guid frameId, CancellationToken ct) {
                SolveRequests.Add(frameId);
                return Task.FromResult(_solves.TryGetValue(frameId, out var c) ? c : null);
            }
        }

        private static Coordinates OffsetBy(double arcMinutesDec) =>
            new(Angle.ByDegree(Target.RADegrees), Angle.ByDegree(Target.Dec + arcMinutesDec / 60.0), Epoch.J2000);

        private static TakeExposure Light() =>
            new(new Mock<ICameraMediator>().Object, new Mock<IImagingMediator>().Object) { ImageType = "LIGHT", ExposureTime = 60 };

        private static TakeExposure Snapshot() =>
            new(new Mock<ICameraMediator>().Object, new Mock<IImagingMediator>().Object) { ImageType = "SNAPSHOT", ExposureTime = 2 };

        private static CenterAfterDriftTrigger Trigger(IFrameSolveFeed? feed, ICenteringExecutor? executor = null, double distance = 10, int after = 1) {
            var trigger = new CenterAfterDriftTrigger(feed, executor) {
                DistanceArcMinutes = distance,
                AfterExposures = after,
                Coordinates = new InputCoordinates(Target),
            };
            trigger.SequenceBlockInitialize();
            return trigger;
        }

        private static async Task SettleAsync(CenterAfterDriftTrigger trigger) {
            var sw = Stopwatch.StartNew();
            while (trigger.SolveInFlight && sw.Elapsed < TimeSpan.FromSeconds(10)) {
                await Task.Delay(5);
            }
            Assert.That(trigger.SolveInFlight, Is.False, "the background solve did not finish");
        }

        [Test]
        public void Defaults_match_nina() {
            var t = new CenterAfterDriftTrigger();
            Assert.That(t.DistanceArcMinutes, Is.EqualTo(10));
            Assert.That(t.AfterExposures, Is.EqualTo(1));
            Assert.That(t.Coordinates, Is.Not.Null);
        }

        [Test]
        public void Clone_carries_the_fields_and_an_own_coordinates_instance() {
            var t = Trigger(null, distance: 3.5, after: 4);
            var clone = (CenterAfterDriftTrigger)t.Clone();
            Assert.That(clone.DistanceArcMinutes, Is.EqualTo(3.5));
            Assert.That(clone.AfterExposures, Is.EqualTo(4));
            Assert.That(clone.Coordinates.Coordinates.RADegrees, Is.EqualTo(Target.RADegrees).Within(1e-9));
            Assert.That(ReferenceEquals(clone.Coordinates, t.Coordinates), Is.False);
            Assert.That(clone.TriggerRunner, Is.Not.Null);
        }

        [Test]
        public void Bad_imports_keep_sane_values() {
            var t = new CenterAfterDriftTrigger { DistanceArcMinutes = 0, AfterExposures = -3 };
            Assert.That(t.DistanceArcMinutes, Is.EqualTo(10), "a non-positive distance keeps the previous value (NINA parity)");
            Assert.That(t.AfterExposures, Is.EqualTo(1), "the cadence floors at one");
        }

        [Test]
        public async Task A_light_within_the_threshold_does_not_trigger() {
            var feed = new ScriptedSolveFeed();
            var t = Trigger(feed);
            feed.Save(OffsetBy(2));

            Assert.That(t.ShouldTrigger(Light(), Light()), Is.False, "the solve has not landed yet");
            await SettleAsync(t);
            Assert.That(t.LastDistanceArcMinutes, Is.EqualTo(2).Within(0.05));
            Assert.That(t.ShouldTrigger(null, Light()), Is.False);
        }

        [Test]
        public async Task A_light_past_the_threshold_triggers_before_the_next_light_only() {
            var feed = new ScriptedSolveFeed();
            var t = Trigger(feed);
            feed.Save(OffsetBy(12));

            t.ShouldTrigger(Light(), Light());
            await SettleAsync(t);
            Assert.That(t.LastDistanceArcMinutes, Is.EqualTo(12).Within(0.05));
            Assert.That(t.ShouldTrigger(null, Snapshot()), Is.False, "only a LIGHT is preceded by a re-centre");
            Assert.That(t.ShouldTrigger(null, Light()), Is.True);
        }

        [Test]
        public async Task Only_every_nth_light_is_solved() {
            var feed = new ScriptedSolveFeed();
            var t = Trigger(feed, after: 3);
            var first = feed.Save(OffsetBy(12));
            t.ShouldTrigger(Light(), Light());
            var second = feed.Save(OffsetBy(12));
            t.ShouldTrigger(Light(), Light());
            await SettleAsync(t);
            Assert.That(feed.SolveRequests, Is.Empty, "two lights at a cadence of three: nothing solved yet");
            var third = feed.Save(OffsetBy(12));
            t.ShouldTrigger(Light(), Light());
            await SettleAsync(t);
            Assert.That(feed.SolveRequests, Is.EqualTo(new[] { third }), "the third light is the one solved");
            Assert.That(first, Is.Not.EqualTo(second));
        }

        [Test]
        public async Task The_same_saved_frame_is_counted_once() {
            var feed = new ScriptedSolveFeed();
            var t = Trigger(feed, after: 2);
            feed.Save(OffsetBy(12));
            t.ShouldTrigger(Light(), Light());
            t.ShouldTrigger(Light(), Snapshot()); // same LatestLight: not a second light
            await SettleAsync(t);
            Assert.That(feed.SolveRequests, Is.Empty);
        }

        [Test]
        public async Task Lights_saved_before_the_block_started_are_ignored() {
            var feed = new ScriptedSolveFeed();
            feed.Save(OffsetBy(30)); // the previous target's last light
            var t = Trigger(feed); // SequenceBlockInitialize runs here, after the save
            Assert.That(t.ShouldTrigger(Light(), Light()), Is.False);
            await SettleAsync(t);
            Assert.That(feed.SolveRequests, Is.Empty);
        }

        [Test]
        public async Task A_frame_that_does_not_solve_keeps_the_trigger_quiet() {
            var feed = new ScriptedSolveFeed();
            var t = Trigger(feed);
            feed.Save(null);
            t.ShouldTrigger(Light(), Light());
            await SettleAsync(t);
            Assert.That(t.LastDistanceArcMinutes, Is.EqualTo(0));
            Assert.That(t.ShouldTrigger(null, Light()), Is.False);
        }

        [Test]
        public async Task Execute_recentres_on_the_target_and_clears_the_measurement() {
            var feed = new ScriptedSolveFeed();
            var executor = new Mock<ICenteringExecutor>();
            Coordinates? seen = null;
            executor.Setup(e => e.CenterAsync(It.IsAny<Coordinates>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .Callback<Coordinates, IProgress<ApplicationStatus>, CancellationToken>((c, _, _) => seen = c)
                .ReturnsAsync(true);
            var t = Trigger(feed, executor.Object);
            feed.Save(OffsetBy(15));
            t.ShouldTrigger(Light(), Light());
            await SettleAsync(t);
            Assert.That(t.ShouldTrigger(null, Light()), Is.True);

            await t.Execute(new SequentialContainer(), new Progress<ApplicationStatus>(), CancellationToken.None);

            Assert.That(seen, Is.Not.Null);
            Assert.That(seen!.RADegrees, Is.EqualTo(Target.RADegrees).Within(1e-6));
            Assert.That(seen.Dec, Is.EqualTo(Target.Dec).Within(1e-6));
            Assert.That(t.LastDistanceArcMinutes, Is.EqualTo(0));
            Assert.That(t.ShouldTrigger(null, Light()), Is.False, "the stale measurement is gone until the next solve");
        }

        [Test]
        public async Task Execute_without_an_executor_fails_loudly() {
            var t = Trigger(new ScriptedSolveFeed(), executor: null);
            await Assert.ThrowsAsync<SequenceEntityFailedException>(
                () => t.Execute(new SequentialContainer(), new Progress<ApplicationStatus>(), CancellationToken.None));
        }

        [Test]
        public async Task A_centre_that_does_not_converge_fails_the_trigger() {
            var executor = new Mock<ICenteringExecutor>();
            executor.Setup(e => e.CenterAsync(It.IsAny<Coordinates>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            var t = Trigger(new ScriptedSolveFeed(), executor.Object);
            await Assert.ThrowsAsync<SequenceEntityFailedException>(
                () => t.Execute(new SequentialContainer(), new Progress<ApplicationStatus>(), CancellationToken.None));
        }

        [Test]
        public void Validate_needs_coordinates() {
            var t = new CenterAfterDriftTrigger { Coordinates = null! };
            Assert.That(t.Validate(), Is.False);
            Assert.That(t.Issues, Has.Count.EqualTo(1));
            var ok = Trigger(null);
            Assert.That(ok.Validate(), Is.True);
        }

        [Test]
        public void Teardown_discards_a_solve_that_lands_late() {
            var feed = new ScriptedSolveFeed();
            var t = Trigger(feed);
            feed.Save(OffsetBy(20));
            t.ShouldTrigger(Light(), Light());
            t.SequenceBlockTeardown();
            // A late result for the torn-down block must not survive into the next one.
            SpinWait.SpinUntil(() => !t.SolveInFlight, TimeSpan.FromSeconds(10));
            Assert.That(t.LastDistanceArcMinutes, Is.EqualTo(0));
        }

        [Test]
        public async Task A_solve_of_the_frame_taken_before_the_recentre_is_discarded() {
            // AfterExposures = 1: the boundary that fires also starts a solve of the light just saved —
            // a frame taken BEFORE the re-centre. Landing after Execute it must not re-arm the trigger.
            var gate = new TaskCompletionSource<Coordinates?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var feed = new Mock<IFrameSolveFeed>();
            var seq = 0L;
            var latest = (SavedLight?)null;
            feed.SetupGet(f => f.LatestLight).Returns(() => latest);
            var calls = 0;
            feed.Setup(f => f.SolveFrameAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns(() => ++calls == 1 ? Task.FromResult<Coordinates?>(OffsetBy(15)) : gate.Task);
            var executor = new Mock<ICenteringExecutor>();
            executor.Setup(e => e.CenterAsync(It.IsAny<Coordinates>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            var t = Trigger(feed.Object, executor.Object);

            latest = new SavedLight(Guid.NewGuid(), ++seq, DateTimeOffset.UtcNow);
            t.ShouldTrigger(Light(), Light());
            await SettleAsync(t);
            latest = new SavedLight(Guid.NewGuid(), ++seq, DateTimeOffset.UtcNow);
            Assert.That(t.ShouldTrigger(Light(), Light()), Is.True, "drift measured, and the pre-re-centre frame's solve is now in flight");
            await t.Execute(new SequentialContainer(), new Progress<ApplicationStatus>(), CancellationToken.None);
            gate.SetResult(OffsetBy(15)); // the slow solve lands after the re-centre
            await SettleAsync(t);

            Assert.That(t.LastDistanceArcMinutes, Is.EqualTo(0), "a stale solve must not write the old drift back");
            Assert.That(t.ShouldTrigger(null, Light()), Is.False);
        }

        // ── factory + import ──

        [Test]
        public void WithDefaults_registers_the_trigger() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            Assert.That(factory.Triggers.Select(x => x.GetType().Name), Does.Contain(nameof(CenterAfterDriftTrigger)));
        }

        [Test]
        public void A_nina_export_resolves_the_trigger_with_its_fields() {
            const string json = """
            {
              "$type": "NINA.Sequencer.Container.SequentialContainer, NINA.Sequencer",
              "Strategy": { "$type": "NINA.Sequencer.Container.ExecutionStrategy.SequentialStrategy, NINA.Sequencer" },
              "Items": [], "Conditions": [],
              "Triggers": [
                {
                  "$type": "NINA.Sequencer.Trigger.Platesolving.CenterAfterDriftTrigger, NINA.Sequencer",
                  "Coordinates": {
                    "$type": "NINA.Astrometry.InputCoordinates, NINA.Astrometry",
                    "RAHours": 5, "RAMinutes": 30, "RASeconds": 0.0,
                    "NegativeDec": true, "DecDegrees": -5, "DecMinutes": 24, "DecSeconds": 0.0
                  },
                  "DistanceArcMinutes": 7.5,
                  "AfterExposures": 3,
                  "TriggerRunner": {
                    "$type": "NINA.Sequencer.Container.SequentialContainer, NINA.Sequencer",
                    "Strategy": { "$type": "NINA.Sequencer.Container.ExecutionStrategy.SequentialStrategy, NINA.Sequencer" },
                    "Items": [], "Conditions": [], "Triggers": []
                  }
                }
              ]
            }
            """;
            var converter = new SequenceJsonConverter(HeadlessSequencerFactory.WithDefaults());
            var root = converter.Deserialize(json);
            var trigger = ((OpenAstroAra.Sequencer.Trigger.ITriggerable)root).GetTriggersSnapshot().OfType<CenterAfterDriftTrigger>().SingleOrDefault();
            Assert.That(trigger, Is.Not.Null, "the trigger must resolve to its ported type, not UnknownSequenceTrigger");
            Assert.That(trigger!.DistanceArcMinutes, Is.EqualTo(7.5));
            Assert.That(trigger.AfterExposures, Is.EqualTo(3));
            Assert.That(trigger.Coordinates.Coordinates.RADegrees, Is.EqualTo(82.5).Within(1e-6));
            Assert.That(trigger.Coordinates.Coordinates.Dec, Is.EqualTo(-5.4).Within(1e-6));
        }

        // ── the daemon-side feed ──

        private static FrameDto Frame(Guid id, string path) => new(
            Id: id, SessionId: Guid.NewGuid(), TargetName: "M42", FrameType: FrameType.Light,
            FilterName: null, ExposureSeconds: 60, Gain: 100, Offset: 10, TemperatureC: null,
            CapturedUtc: DateTimeOffset.UtcNow, FilePath: path, FileSizeBytes: 4, Width: 4, Height: 4, BitDepth: 16,
            Hfr: null, StarCount: null, Eccentricity: null, GuidingRmsArcsec: null, SnrEstimate: null,
            QualityScore: null, Rating: 0, Tags: []);

        [Test]
        public void Saving_a_light_advances_the_sequence() {
            using var feed = new FrameSolveFeed();
            Assert.That(feed.LatestLight, Is.Null);
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            feed.RecordLightSaved(a);
            feed.RecordLightSaved(b);
            Assert.That(feed.LatestLight!.FrameId, Is.EqualTo(b));
            Assert.That(feed.LatestLight.Sequence, Is.EqualTo(2));
        }

        [Test]
        public async Task Solving_reads_the_catalogued_file_and_returns_the_solved_centre() {
            var id = Guid.NewGuid();
            var frames = new Mock<IFrameRepository>();
            frames.Setup(f => f.GetAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(Frame(id, "/captures/m42.fits"));
            var solver = new Mock<IPlateSolveService>();
            solver.Setup(s => s.SolveImage(It.IsAny<OpenAstroAra.Image.Interfaces.IImageData>(), It.IsAny<Coordinates?>(), null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PlateSolveResult { Success = true, Coordinates = Target });
            using var feed = new FrameSolveFeed(frames.Object, solver.Object, profileService: new HeadlessProfileService()) {
                PixelLoader = (path, _) => {
                    Assert.That(path, Is.EqualTo("/captures/m42.fits"));
                    return Task.FromResult((new ushort[16], 4, 4));
                },
            };

            var solved = await feed.SolveFrameAsync(id, CancellationToken.None);

            Assert.That(solved, Is.Not.Null);
            Assert.That(solved!.RADegrees, Is.EqualTo(Target.RADegrees).Within(1e-9));
        }

        [Test]
        public async Task A_failed_solve_or_a_missing_frame_reads_as_null() {
            var id = Guid.NewGuid();
            var frames = new Mock<IFrameRepository>();
            frames.Setup(f => f.GetAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(Frame(id, "/captures/m42.fits"));
            var solver = new Mock<IPlateSolveService>();
            solver.Setup(s => s.SolveImage(It.IsAny<OpenAstroAra.Image.Interfaces.IImageData>(), It.IsAny<Coordinates?>(), null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PlateSolveResult { Success = false });
            using var feed = new FrameSolveFeed(frames.Object, solver.Object, profileService: new HeadlessProfileService()) {
                PixelLoader = (_, _) => Task.FromResult((new ushort[16], 4, 4)),
            };

            Assert.That(await feed.SolveFrameAsync(id, CancellationToken.None), Is.Null, "solver said no");
            Assert.That(await feed.SolveFrameAsync(Guid.NewGuid(), CancellationToken.None), Is.Null, "unknown frame");

            using var unwired = new FrameSolveFeed();
            Assert.That(await unwired.SolveFrameAsync(id, CancellationToken.None), Is.Null, "no repository/solver wired");
        }

        [Test]
        public async Task A_loader_fault_reads_as_null_not_an_exception() {
            var id = Guid.NewGuid();
            var frames = new Mock<IFrameRepository>();
            frames.Setup(f => f.GetAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(Frame(id, "/captures/gone.fits"));
            using var feed = new FrameSolveFeed(frames.Object, new Mock<IPlateSolveService>().Object, profileService: new HeadlessProfileService()) {
                PixelLoader = (_, _) => throw new System.IO.FileNotFoundException(),
            };
            Assert.That(await feed.SolveFrameAsync(id, CancellationToken.None), Is.Null);
        }
    }
}
