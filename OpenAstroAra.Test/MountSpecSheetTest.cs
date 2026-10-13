#region "copyright"
/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors
    This file is part of OpenAstro Ara (forked from N.I.N.A.).
    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/
#endregion "copyright"

using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Test {

    /// <summary>#1264 stage 1 — the bridge's mount spec sheet: parsing and the prior it implies.</summary>
    [TestFixture]
    public class MountSpecSheetTest {

        private const string Eqm35 = """
            {
              "schema": "openastro.mount-specs/1",
              "driver_id": "SkyWatcher_EQM35",
              "model": "Sky-Watcher EQM-35 Pro",
              "source": "manufacturer",
              "updated": "2026-10-03",
              "worm_period_s": 638,
              "periodic_error_pp_arcsec": 25,
              "has_encoders": false,
              "has_pec": true,
              "mount_type": "worm_eq",
              "max_payload_kg": 10,
              "notes": "Light GEM; keep subs short unguided."
            }
            """;

        [Test]
        public void Parses_the_bridge_sheet_and_tolerates_unknown_fields() {
            var sheet = MountSpecSheet.Parse(Eqm35);
            Assert.That(sheet, Is.Not.Null);
            Assert.That(sheet!.Model, Is.EqualTo("Sky-Watcher EQM-35 Pro"));
            Assert.That(sheet.Source, Is.EqualTo("manufacturer"));
            Assert.That(sheet.WormPeriodS, Is.EqualTo(638));
            Assert.That(sheet.PeriodicErrorPpArcsec, Is.EqualTo(25));
            Assert.That(sheet.HasEncoders, Is.False);
            Assert.That(sheet.HasPec, Is.True);
            Assert.That(sheet.MountType, Is.EqualTo("worm_eq"));

            var sparse = MountSpecSheet.Parse("""{"model":"ZWO AM5","mount_type":"strain_wave","worm_period_s":null,"periodic_error_pp_arcsec":null}""");
            Assert.That(sparse, Is.Not.Null, "a model with unknown figures is still a sheet");
            Assert.That(sparse!.WormPeriodS, Is.Null);
            Assert.That(sparse.Source, Is.Null, "an unknown source stays unknown — never a guess");
        }

        [Test]
        public void Junk_is_no_sheet() {
            Assert.That(MountSpecSheet.Parse(null), Is.Null);
            Assert.That(MountSpecSheet.Parse(""), Is.Null);
            Assert.That(MountSpecSheet.Parse("not json"), Is.Null);
            Assert.That(MountSpecSheet.Parse("[1,2]"), Is.Null);
            Assert.That(MountSpecSheet.Parse("""{"schema":"openastro.mount-specs/1"}"""), Is.Null, "nothing Ara can use");
        }

        [Test]
        public void The_EQM35_example_from_the_issue_gives_a_short_prior() {
            var sheet = MountSpecSheet.Parse(Eqm35)!;
            // RedCat at 2.3"/px, seeing 3": tolerance = max(2.3, 1.5) = 2.3"; pe_rate = 2π·12.5/638 = 0.1231"/s.
            var prior = MountSpecSheet.UnguidedPriorSeconds(sheet, pixelScaleArcsec: 2.3, seeingFwhmArcsec: 3.0, polarAlignErrorArcmin: null);
            Assert.That(prior, Is.EqualTo(2.3 / (2 * Math.PI * 12.5 / 638)).Within(1e-6));
            Assert.That(prior, Is.EqualTo(18.7).Within(0.1));
            // Seeing-limited tolerance when the pixels are small.
            var fine = MountSpecSheet.UnguidedPriorSeconds(sheet, pixelScaleArcsec: 0.8, seeingFwhmArcsec: 3.0, polarAlignErrorArcmin: null);
            Assert.That(fine, Is.EqualTo(1.5 / (2 * Math.PI * 12.5 / 638)).Within(1e-6));
        }

        [Test]
        public void Encoders_drop_the_periodic_error_and_polar_misalignment_adds_drift() {
            var encoders = MountSpecSheet.Parse(Eqm35)! with { HasEncoders = true };
            Assert.That(MountSpecSheet.UnguidedPriorSeconds(encoders, 2.3, 3.0, null), Is.Null, "no PE term, no PA term: nothing to bound");
            var withPa = MountSpecSheet.UnguidedPriorSeconds(encoders, 2.3, 3.0, polarAlignErrorArcmin: 10);
            var paRate = 15.04 * Math.Sin(10.0 / 60 * Math.PI / 180);
            Assert.That(withPa, Is.EqualTo(2.3 / paRate).Within(1e-6));
            var both = MountSpecSheet.UnguidedPriorSeconds(MountSpecSheet.Parse(Eqm35), 2.3, 3.0, 10);
            Assert.That(both, Is.LessThan(withPa), "two drift terms add");
        }

        [Test]
        public void No_tolerance_or_a_wild_mount_still_floor_at_five_seconds() {
            var sheet = MountSpecSheet.Parse(Eqm35)!;
            Assert.That(MountSpecSheet.UnguidedPriorSeconds(sheet, null, null, null), Is.Null, "no pixel scale and no seeing: no tolerance");
            var wild = sheet with { PeriodicErrorPpArcsec = 5000, WormPeriodS = 10 };
            Assert.That(MountSpecSheet.UnguidedPriorSeconds(wild, 2.3, 3.0, null), Is.EqualTo(MountSpecSheet.MinPriorSeconds));
        }

        private sealed class FixedSpecs : IMountSpecSheetSource {
            public MountSpecSheetDto? CurrentMountSpecs { get; set; }
        }

        private sealed class NoFrames : IAnalysisFrameSource {
            public Task<AnalysisFrame> CaptureForAnalysisAsync(double exposureSec, int binning, CancellationToken ct) =>
                throw new InvalidOperationException("the prior never captures");
        }

        [Test]
        public async Task The_probe_prior_falls_back_to_the_sheet_and_says_so() {
            var store = new InMemoryProfileStore();
            store.PutOpticsSettings(new OpticsSettingsDto(447, 1.0, 6248, 4176, 3.76, 91));
            var specs = new FixedSpecs { CurrentMountSpecs = MountSpecSheet.Parse(Eqm35) };
            var svc = new TrackingProbeService(store, new NoFrames(), specs: specs);

            var (seconds, source) = await svc.ResolvePriorWithSourceAsync(null, CancellationToken.None);
            Assert.That(seconds, Is.Not.Null.And.GreaterThan(MountSpecSheet.MinPriorSeconds));
            Assert.That(source, Is.EqualTo("the mount's spec sheet (manufacturer)"));

            store.PutTrackingCalibration(new TrackingCalibrationDto(45, DateTimeOffset.UtcNow, null, null, null, null, "measured", Array.Empty<TrackingProbeRungDto>()));
            var stored = await svc.ResolvePriorWithSourceAsync(null, CancellationToken.None);
            Assert.That(stored, Is.EqualTo((45.0, "the last probe")), "a measurement beats the sheet");

            var body = await svc.ResolvePriorWithSourceAsync(20, CancellationToken.None);
            Assert.That(body, Is.EqualTo((20.0, "the sequence's own prior")));

            specs.CurrentMountSpecs = null;
            store.PutTrackingCalibration(null);
            Assert.That(await svc.ResolvePriorWithSourceAsync(null, CancellationToken.None), Is.EqualTo(((double?)null, (string?)null)), "no sheet, never probed");
        }

        private sealed class OneResidual : IPolarAlignmentLog {
            public Task InsertAsync(PolarAlignmentRecord record, CancellationToken ct) => Task.CompletedTask;
            public Task<PolarAlignmentRecord?> GetLatestMeasuredAsync(DateTimeOffset since, CancellationToken ct) =>
                Task.FromResult<PolarAlignmentRecord?>(new PolarAlignmentRecord(since, since, 10, 6, 8, 2, "complete"));
        }

        [Test]
        public async Task A_sheet_that_contributes_nothing_is_not_credited_for_a_polar_align_prior() {
            var store = new InMemoryProfileStore();
            store.PutOpticsSettings(new OpticsSettingsDto(447, 1.0, 6248, 4176, 3.76, 91));
            var specs = new FixedSpecs { CurrentMountSpecs = MountSpecSheet.Parse("{\"model\":\"ZWO AM5\",\"has_encoders\":true}") };
            var svc = new TrackingProbeService(store, new NoFrames(), specs: specs, polarAlignLog: new OneResidual());
            var (seconds, source) = await svc.ResolvePriorWithSourceAsync(null, CancellationToken.None);
            Assert.That(seconds, Is.Not.Null);
            Assert.That(source, Is.EqualTo("the latest polar alignment"));
        }
    }
}
