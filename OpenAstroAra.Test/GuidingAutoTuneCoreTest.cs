using NUnit.Framework;
using Newtonsoft.Json;
using OpenAstroAra.Core.Guiding;
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2.PhdEvents;
using OpenAstroAra.Server.Services;
using OpenAstroAra.Server.Services.Guiding;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test;

[TestFixture]
public sealed class GuidingAutoTuneCoreTest {
    private static readonly int[] SupportedExposures = { 500, 1000 };
    private static readonly int[] ManySupportedExposures = { 250, 500, 750, 1000, 1500, 2000 };
    private static readonly string[] TestWarnings = { "test" };

    [Test]
    public void GuideScale_UsesMicronsBinningAndFocalLength() {
        var scale = GuidingPlanner.GuideScaleArcsecPerPixel(3.76, 1, 242);
        Assert.That(scale, Is.EqualTo(3.204).Within(.01));
    }

    [Test]
    public void PulseEstimate_ScalesWithRateAndFrameInterval() {
        var pulse = GuidingPlanner.ExpectedPeriodicErrorPulseMilliseconds(.22, .5, 7.5205);
        Assert.That(pulse, Is.EqualTo(14.63).Within(.1));
        Assert.That(GuidingPlanner.ExpectedPeriodicErrorPulseMilliseconds(.22, 1, 15.041),
            Is.EqualTo(pulse).Within(.1));
    }

    [Test]
    public void Analyzer_ReportsHarmonicLikeSignal() {
        var samples = new List<GuidingTelemetrySample>();
        for (var i = 0; i < 500; i++) {
            var t = i * .5;
            var value = .5 * Math.Sin(2 * Math.PI * t / 60) + .28 * Math.Sin(2 * Math.PI * t / 30);
            samples.Add(Sample(i, t, value, value * .1));
        }
        var characterization = GuidingSignalAnalyzer.Analyze(Fingerprint(), Window(samples), 1);
        Assert.That(characterization.BehaviorClass, Is.EqualTo(GuidingMountBehaviorClass.HarmonicLike));
        Assert.That(characterization.RightAscension.DominantPeriodSeconds, Is.Not.Null);
        Assert.That(characterization.RightAscension.HarmonicPowerRatio, Is.GreaterThan(.1));
        Assert.That(characterization.Confidence, Is.GreaterThan(.5));
    }

    [Test]
    public void Analyzer_ReturnsUnknownForShortWindow() {
        var samples = Enumerable.Range(0, 4).Select(i => Sample(i, i, .1, .1)).ToArray();
        var characterization = GuidingSignalAnalyzer.Analyze(Fingerprint(), Window(samples), 1);
        Assert.That(characterization.BehaviorClass, Is.EqualTo(GuidingMountBehaviorClass.Unknown));
        Assert.That(characterization.Confidence, Is.EqualTo(0));
    }

    [Test]
    public void Analyzer_ReportsLinearDriftBeforeDetrending() {
        var samples = Enumerable.Range(0, 40)
            .Select(i => Sample(i, i, .02 * i, -.01 * i)).ToArray();
        var characterization = GuidingSignalAnalyzer.Analyze(Fingerprint(), Window(samples), 1);

        Assert.That(characterization.RightAscension.DriftArcsecPerSecond, Is.EqualTo(.02).Within(.001));
        Assert.That(characterization.Declination.DriftArcsecPerSecond, Is.EqualTo(-.01).Within(.001));
    }

    [Test]
    public void Planner_BlocksUnknownImageScale() {
        var c = GuidingSignalAnalyzer.Analyze(Fingerprint(), Window(Enumerable.Range(0, 20)
            .Select(i => Sample(i, i, .01, .01)).ToArray()), null);
        var plan = GuidingPlanner.CreatePlan(new GuidingPlanningContext(c, SupportedExposures,
            new GuideStarQualityMetrics(20, 20, 20, 18, 0, 1, 8, 0, 0, true), 0, null, 1, 7.52,
            GuidingTuneDepth.Standard, Current(), false, false));
        Assert.That(plan.Candidates, Is.Empty);
        Assert.That(plan.CanAutoApply, Is.False);
    }

    [Test]
    public void Planner_BlocksWhenSupportedExposureListHasNoPositiveValues() {
        var characterization = GuidingSignalAnalyzer.Analyze(Fingerprint(), Window(
            Enumerable.Range(0, 20).Select(i => Sample(i, i, .01, .01)).ToArray()), 1);
        var plan = GuidingPlanner.CreatePlan(new GuidingPlanningContext(characterization,
            new[] { 0, -1 },
            new GuideStarQualityMetrics(20, 20, 20, 18, 0, 1, 8, 0, 0, true), 1, null, 1, 7.52,
            GuidingTuneDepth.Standard, Current(), false, false));

        Assert.That(plan.Candidates, Is.Empty);
        Assert.That(plan.CanAutoApply, Is.False);
        Assert.That(plan.Reasons, Has.Some.Contains("no supported exposure"));
    }

    [Test]
    public void StarQuality_RequiresMultiStarFieldWhenTelemetryProvidesCount() {
        var samples = Enumerable.Range(0, 12).Select(i => new GuidingTelemetrySample(
            i + 1, DateTimeOffset.UtcNow.AddSeconds(i), 0, 0, 0, 0, 0, 0,
            null, null, 500, 500, 20, 3, 0, 3, 0, false, false, false, false, false, false,
            true, 1, i)).ToArray();

        var quality = GuidingSignalAnalyzer.AnalyzeStarQuality(samples, 500);

        Assert.That(quality.MedianMultiStarCount, Is.EqualTo(3));
        Assert.That(quality.MeetsMinimumQuality, Is.False);
    }

    [Test]
    public void ExposureProbe_SelectsShortestReliableSupportedExposure() {
        var poor = new GuideStarQualityMetrics(20, 20, 5, 2, 0, 1, 2, 0, 0, false);
        var good = poor with { MedianMultiStarCount = 6, MeetsMinimumQuality = true };
        var probes = new[] {
            new GuidingExposureProbeResult(1000, poor, 1000, true, "too few stars"),
            new GuidingExposureProbeResult(500, good, 510, true),
            new GuidingExposureProbeResult(250, good, 260, true),
        };

        Assert.That(GuidingSignalAnalyzer.SelectShortestReliableExposure(probes), Is.EqualTo(250));
    }

    [Test]
    public void CalibrationAnalyzer_ValidatesRatesAxesAndParity() {
        var result = GuidingCalibrationAnalyzer.Analyze(
            "{\"xRate\":2.0,\"yRate\":1.8,\"xAngle\":0,\"yAngle\":1.57079632679,\"declination\":12,\"xParity\":\"odd\",\"yParity\":\"even\"}");

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.OrthogonalityErrorDegrees, Is.LessThan(.01));
        Assert.That(result.CalibrationDeclinationDegrees, Is.EqualTo(12));
    }

    [Test]
    public void CalibrationAnalyzer_RejectsMissingCalibration() {
        var result = GuidingCalibrationAnalyzer.Analyze("null");

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Reasons, Has.Some.Contains("no calibration"));
    }

    [Test]
    public void Planner_EmitsCoordinateCandidatesBeyondExposureAndPulseCap() {
        var characterization = GuidingSignalAnalyzer.Analyze(Fingerprint(), Window(
            Enumerable.Range(0, 200).Select(i => Sample(i, i * .5,
                .5 * Math.Sin(i * .08) + .2 * Math.Sin(i * .16), .1)).ToArray()), 1);
        var plan = GuidingPlanner.CreatePlan(new GuidingPlanningContext(characterization, SupportedExposures,
            new GuideStarQualityMetrics(200, 200, 20, 18, 0, 1, 8, 0, 0, true), 1, null, .5, 7.52,
            GuidingTuneDepth.Deep, Current(), false, false));
        Assert.That(plan.Candidates, Is.Not.Empty);
        Assert.That(plan.Candidates.Select(c => c.RaAggressiveness).Distinct().Count(), Is.GreaterThan(1));
        Assert.That(plan.Candidates.Select(c => c.RaMinimumMovePixels).Distinct().Count(), Is.GreaterThan(1));
    }

    [Test]
    public void Planner_StandardBudgetTestsCoordinateFamiliesAfterExposureCandidates() {
        var characterization = GuidingSignalAnalyzer.Analyze(Fingerprint(), Window(
            Enumerable.Range(0, 240).Select(i => Sample(i, i * .5,
                .5 * Math.Sin(i * .08) + .2 * Math.Sin(i * .16), .1)).ToArray()), 1);
        var plan = GuidingPlanner.CreatePlan(new GuidingPlanningContext(characterization,
            ManySupportedExposures,
            new GuideStarQualityMetrics(240, 240, 20, 18, 0, 1, 8, 0, 0, true), 1, null, .5, 7.52,
            GuidingTuneDepth.Standard, Current(), false, false));

        Assert.That(plan.Candidates, Has.Count.EqualTo(8));
        Assert.That(plan.Candidates.Select(c => c.RaAggressiveness).Distinct().Count(), Is.GreaterThan(1));
        Assert.That(plan.Candidates.Select(c => c.RaMinimumMovePixels).Distinct().Count(), Is.GreaterThan(1));
        Assert.That(plan.Candidates.Select(c => c.RaMaximumPulseMilliseconds).Distinct().Count(), Is.GreaterThan(1));
    }

    [Test]
    public void Planner_PreservesIndependentGuideRates() {
        var characterization = GuidingSignalAnalyzer.Analyze(Fingerprint(), Window(
            Enumerable.Range(0, 200).Select(i => Sample(i, i * .5, .1, .1)).ToArray()), 1);
        var plan = GuidingPlanner.CreatePlan(new GuidingPlanningContext(characterization,
            SupportedExposures,
            new GuideStarQualityMetrics(200, 200, 20, 18, 0, 1, 8, 0, 0, true), 1, null, .5, 7.52,
            GuidingTuneDepth.Standard, Current() with {
                GuideRateRightAscensionDegreesPerSecond = .001,
                GuideRateDeclinationDegreesPerSecond = .002,
            }, true, false, GuideRateCandidatePairs: new[] {
                new GuidingGuideRateCandidate(.001, .002),
                new GuidingGuideRateCandidate(.0005, .001),
            }));

        Assert.That(plan.Candidates, Has.Some.Matches<GuidingParameterSet>(candidate =>
            candidate.GuideRateRightAscensionDegreesPerSecond == .0005
            && candidate.GuideRateDeclinationDegreesPerSecond == .001));
    }

    [Test]
    public void StarQuality_UsesCollectorCadenceOverWireTimestamp() {
        var samples = Enumerable.Range(0, 12).Select(i => Sample(i, i,
            .01, .01) with { ActualFrameIntervalMilliseconds = 500 }).ToArray();

        var quality = GuidingSignalAnalyzer.AnalyzeStarQuality(samples, 500);

        Assert.That(quality.ActualCadenceRatio, Is.EqualTo(1).Within(.001));
    }

    [Test]
    public void MountKnowledgeBase_ProvidesVersionedPrior() {
        var prior = MountKnowledgeBase.Find("", "ZWO AM5N");
        Assert.That(prior?.DeclaredDriveType, Is.EqualTo("strain-wave"));
        Assert.That(prior?.PriorBehaviorClass, Is.EqualTo(GuidingMountBehaviorClass.HarmonicLike));
    }

    [Test]
    public void ResponseAnalyzer_ReportsDeclinationReversalDelay() {
        var samples = new[] {
            Sample(1, 0, 0, .10) with { DecPulseDirection = "North" },
            Sample(2, 1, 0, .10) with { DecPulseDirection = "South" },
            Sample(3, 2, 0, .10) with { DecPulseDirection = "South" },
            Sample(4, 3, 0, .30) with { DecPulseDirection = "South" },
        };
        Assert.That(GuidingResponseAnalyzer.EstimateDeclinationReversalDelayMilliseconds(samples),
            Is.EqualTo(2000).Within(.001));
    }

    [Test]
    public void ResponseAnalyzer_ReportsLatencyAndCorrectionAuthority() {
        var samples = new[] {
            Sample(1, 0, .4, 0) with { RaPulseMilliseconds = 20, RaPulseDirection = "West" },
            Sample(2, 1, .2, 0),
            Sample(3, 2, .1, 0),
        };

        Assert.That(GuidingResponseAnalyzer.EstimateObservedResponseLatencyMilliseconds(samples),
            Is.EqualTo(1000).Within(.001));
        Assert.That(GuidingResponseAnalyzer.EstimateEffectiveCorrectionAuthorityArcsecPerSecond(samples, 1),
            Is.EqualTo(10).Within(.001));
    }

    [Test]
    public void Planner_SelectsUnidirectionalDeclinationForLargeBacklash() {
        var characterization = GuidingSignalAnalyzer.Analyze(Fingerprint(), Window(
            Enumerable.Range(0, 200).Select(i => Sample(i, i * .5, .1, .1)).ToArray()), 1);
        var plan = GuidingPlanner.CreatePlan(new GuidingPlanningContext(characterization, SupportedExposures,
            new GuideStarQualityMetrics(200, 200, 20, 18, 0, 1, 8, 0, 0, true), 1, null, .5, 7.52,
            GuidingTuneDepth.Deep, Current(), false, false, DeclinationReversalDelayMilliseconds: 4001));

        Assert.That(plan.Candidates.Select(candidate => candidate.DecGuideMode),
            Does.Contain("north").And.Contain("south"));
        Assert.That(plan.Reasons, Has.Some.Contains("unidirectional"));
    }

    [Test]
    public void Scorer_RejectsCriticalRegression() {
        var baseline = new GuidingCandidateResult(Current(), Metrics(.4, .5, false),
            GuidingScorer.CalculateScore(Metrics(.4, .5, false)), true, string.Empty);
        var candidateMetrics = Metrics(.2, .5, true);
        var candidate = new GuidingCandidateResult(Current(), candidateMetrics,
            GuidingScorer.CalculateScore(candidateMetrics), false, "critical");
        Assert.That(GuidingScorer.IsAutomaticWinner(baseline, candidate), Is.False);
    }

    [Test]
    public void Scorer_PenalizesMainCameraStarShapeRegression() {
        var metrics = Metrics(.2, .01, false) with { MainCameraEccentricity = .35 };

        var score = GuidingScorer.CalculateScore(metrics, .20);

        Assert.That(score.MainImage, Is.EqualTo(.30).Within(.001));
        Assert.That(score.Warnings, Is.Empty);
    }

    [Test]
    public async Task Replay_RoundTripsJsonLines() {
        var samples = new[] { Sample(1, 0, .1, -.2), Sample(2, .5, .2, -.1) };
        await using var output = new MemoryStream();
        await GuidingTelemetryReplay.WriteJsonLinesAsync(output, samples);
        output.Position = 0;
        var window = await GuidingTelemetryReplay.ReadWindowAsync(output);
        Assert.That(window.Samples.Select(s => s.Sequence), Is.EqualTo(new long[] { 1, 2 }));
        Assert.That(window.DurationSeconds, Is.EqualTo(.5).Within(.001));
    }

    [Test]
    public async Task MainCameraValidator_UsesBoundedAnalysisFramesAndReturnsEccentricity() {
        var source = new SyntheticAnalysisFrames();
        var validator = new MainCameraGuidingValidator(source);

        var eccentricity = await validator.CaptureMedianEccentricityAsync(1, 1, 3, CancellationToken.None);

        Assert.That(source.Calls, Is.EqualTo(3));
        Assert.That(eccentricity, Is.InRange(0, 1));
    }

    [Test]
    public void MainCameraValidator_RejectsFramesWithoutStars() {
        var source = new SyntheticAnalysisFrames(flat: true);
        var validator = new MainCameraGuidingValidator(source);

        Assert.ThrowsAsync<InvalidOperationException>(() =>
            validator.CaptureMedianEccentricityAsync(1, 1, 1, CancellationToken.None));
    }

    [Test]
    public void GuideStepParser_AcceptsOptionalMultiStarFieldsAndWireTime() {
        var step = JsonConvert.DeserializeObject<PhdEventGuideStep>("""
            {"Event":"GuideStep","Time":123.5,"RADistanceRaw":0.1,"DECDistanceRaw":-0.2,
             "MultiStarCount":7,"RejectedStarCount":2}
            """)!;

        Assert.That(step.Time, Is.EqualTo(123.5));
        Assert.That(step.MultiStarCount, Is.EqualTo(7));
        Assert.That(step.RejectedStarCount, Is.EqualTo(2));
    }

    [Test]
    public void Scorer_UsesPulseDirectionsForOscillation() {
        var samples = new[] {
            Sample(1, 0, .1, 0) with { RaPulseMilliseconds = 10, RaPulseDirection = "East" },
            Sample(2, 1, -.1, 0) with { RaPulseMilliseconds = 10, RaPulseDirection = "West" },
            Sample(3, 2, .1, 0) with { RaPulseMilliseconds = 10, RaPulseDirection = "East" },
        };
        var metrics = GuidingScorer.CalculateMetrics(samples, 1);
        Assert.That(metrics.OscillationRate, Is.EqualTo(1).Within(.001));
    }

    [Test]
    public void Scorer_BootstrapConfidenceFavorsLowerResidualCandidate() {
        var baseline = Enumerable.Range(0, 64)
            .Select(i => Sample(i, i * .5, .45 * Math.Sin(i), .15 * Math.Cos(i))).ToArray();
        var candidate = Enumerable.Range(0, 64)
            .Select(i => Sample(i, i * .5, .05 * Math.Sin(i), .02 * Math.Cos(i))).ToArray();
        var confidence = GuidingScorer.EstimateImprovementConfidence(baseline, candidate, 1);
        Assert.That(confidence, Is.GreaterThanOrEqualTo(.8));
    }

    [Test]
    public async Task Repository_PersistsAndReloadsCompleteSession() {
        var directory = Path.Combine(Path.GetTempPath(), "ara-guiding-" + Guid.NewGuid().ToString("N"));
        try {
            var database = new SqliteAraDatabase(directory, null);
            await database.InitializeAsync(CancellationToken.None);
            var repository = new GuidingAutoTuneRepository(database);
            var session = new GuidingAutoTuneSession(Guid.NewGuid(), GuidingAutoTuneState.Proposed,
                .75, "proposal", GuidingMountBehaviorClass.WormLike, .8, null, null, null,
                TestWarnings, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                new GuidingCandidateResult(Current(), Metrics(.4, .01, false),
                    GuidingScorer.CalculateScore(Metrics(.4, .01, false)), true, string.Empty),
                Array.Empty<GuidingCandidateResult>(), null);
            await repository.SaveAsync(session, CancellationToken.None);
            await repository.SaveTelemetryWindowAsync(session.Id, "test", Window(new[] { Sample(1, 0, .1, .1) }), CancellationToken.None);
            var loaded = repository.Load();
            Assert.That(loaded?.Id, Is.EqualTo(session.Id));
            Assert.That(loaded?.BaselineResult?.Settings.ExposureMilliseconds, Is.EqualTo(1000));
            using var connection = database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT sample_count FROM guiding_autotune_telemetry_windows WHERE session_id = $id AND phase = 'test'";
            command.Parameters.AddWithValue("$id", session.Id.ToString("D"));
            var sampleCount = await command.ExecuteScalarAsync();
            Assert.That(Convert.ToInt32(sampleCount, CultureInfo.InvariantCulture), Is.EqualTo(1));
        } finally {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Test]
    public async Task Repository_marks_interrupted_session_failed_and_keeps_snapshot() {
        var directory = Path.Combine(Path.GetTempPath(), "ara-guiding-recovery-" + Guid.NewGuid().ToString("N"));
        try {
            var database = new SqliteAraDatabase(directory, null);
            await database.InitializeAsync(CancellationToken.None);
            var repository = new GuidingAutoTuneRepository(database);
            var session = new GuidingAutoTuneSession(Guid.NewGuid(), GuidingAutoTuneState.EvaluatingCandidate,
                .5, "evaluating", GuidingMountBehaviorClass.Unknown, .2, null, null,
                new GuidingSettingsSnapshot(Current(), true, true, "Sidereal", "profile",
                    DateTimeOffset.UtcNow, "hash", "Hysteresis", "ResistSwitch", new Dictionary<string, double>(), "null", true),
                TestWarnings, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null,
                Array.Empty<GuidingCandidateResult>(), null, null);
            await repository.SaveAsync(session, CancellationToken.None);

            var recovered = repository.RecoverInterruptedSession();

            Assert.That(recovered?.State, Is.EqualTo(GuidingAutoTuneState.Failed));
            Assert.That(recovered?.Snapshot, Is.Not.Null);
            Assert.That(recovered?.Warnings, Has.Some.Contains("Server restarted"));
            Assert.That(repository.Load()?.State, Is.EqualTo(GuidingAutoTuneState.Failed));
        } finally {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static GuidingRunMetrics Metrics(double rms, double starLoss, bool critical) =>
        new(100, rms, rms * .8, rms * .7, rms * 1.5, rms * 2, .01, .01, .01, starLoss, .01, 0, null, critical);

    private static GuidingParameterSet Current() => new(1000, .15, .15, .7, .7, 500, 500, "Hysteresis", "ResistSwitch", "auto");

    private static MountFingerprint Fingerprint() => new("Test", "Synthetic", "sim", "1", "", null,
        Array.Empty<double>(), 7.52, 7.52, 60, 0, "west");

    private static GuidingTelemetryWindow Window(IReadOnlyList<GuidingTelemetrySample> samples) =>
        new(samples, "test", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(Math.Max(1, samples.Count)));

    private static GuidingTelemetrySample Sample(long sequence, double seconds, double ra, double dec) =>
        new(sequence, DateTimeOffset.UnixEpoch.AddSeconds(seconds), ra, dec, ra, dec, 0, 0,
            null, null, 500, 500, 20, 3, 1000, 8, 0, false, false, false, false, false, false, true, 1);

    private sealed class SyntheticAnalysisFrames : IAnalysisFrameSource {
        private readonly bool _flat;
        public int Calls { get; private set; }

        public SyntheticAnalysisFrames(bool flat = false) => _flat = flat;

        public Task<AnalysisFrame> CaptureForAnalysisAsync(double exposureSec, int binning, CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            Calls++;
            var pixels = new ushort[32 * 32];
            Array.Fill(pixels, (ushort)100);
            if (!_flat) {
                for (var y = -2; y <= 2; y++)
                for (var x = -2; x <= 2; x++) {
                    var radius = Math.Abs(x) + Math.Abs(y);
                    pixels[(16 + y) * 32 + 16 + x] = (ushort)(5000 - radius * 700);
                }
            }
            return Task.FromResult(new AnalysisFrame(pixels, 32, 32, DateTimeOffset.UtcNow));
        }
    }
}
