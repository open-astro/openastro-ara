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
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// The flat solve against a simulated camera + panel: <c>level = pedestal + rate × (b/b₀)^γ × t</c>,
    /// clipped at 16-bit full scale. γ ≠ 1 models a panel whose brightness steps aren't linear.
    /// </summary>
    [TestFixture]
    public class FlatExposureSolverTest {

        private sealed class Rig {
            public double Pedestal { get; init; } = 800;
            public double RatePerSec { get; init; } = 5000;   // at ReferenceBrightness
            public int ReferenceBrightness { get; init; } = 128;
            public double Gamma { get; init; } = 1;
            public List<(double T, int? B, double Level)> Probes { get; } = new();

            public Task<double> Probe(double t, int? b, CancellationToken ct) {
                var scale = b is int bb ? Math.Pow((double)bb / ReferenceBrightness, Gamma) : 1;
                var level = Math.Min(ushort.MaxValue, Pedestal + (RatePerSec * scale * t));
                Probes.Add((t, b, level));
                return Task.FromResult(level);
            }
        }

        private static FlatSolveLimits Limits(int maxBrightness = 0, double maxExposure = 10, double target = 32768) =>
            new(TargetLevel: target, ToleranceLevel: target * 0.05, MinExposureSec: 0.001, MaxExposureSec: maxExposure,
                SaturationLevel: ushort.MaxValue * 0.9, MaxBrightness: maxBrightness);

        [Test]
        public async Task Two_points_fit_the_pedestal_and_one_check_lands_on_target() {
            var rig = new Rig();
            var result = await FlatExposureSolver.SolveAsync(Limits(), 1.0, null, rig.Probe, CancellationToken.None);

            Assert.That(result.Failure, Is.Null);
            Assert.That(result.ExposureSec, Is.EqualTo((32768 - 800) / 5000.0).Within(1e-6),
                "(target − pedestal) / slope — the offset is measured, not ignored");
            Assert.That(result.Probes, Is.EqualTo(3));
        }

        [Test]
        public async Task A_start_already_on_target_takes_one_frame() {
            var rig = new Rig();
            var remembered = (32768 - 800) / 5000.0;
            var result = await FlatExposureSolver.SolveAsync(Limits(), remembered, null, rig.Probe, CancellationToken.None);

            Assert.That(result.ExposureSec, Is.EqualTo(remembered));
            Assert.That(result.Probes, Is.EqualTo(1), "a remembered solution needs only the check frame");
        }

        [Test]
        public async Task A_too_bright_panel_is_dimmed_to_keep_the_exposure_above_a_second() {
            // At brightness 128 the target needs 0.16 s — flicker territory. γ 1.4: the dimming is
            // not proportional, the check frames absorb it.
            var rig = new Rig { RatePerSec = 200_000, Gamma = 1.4 };
            var result = await FlatExposureSolver.SolveAsync(Limits(maxBrightness: 255), 1.0, 128, rig.Probe, CancellationToken.None);

            Assert.That(result.Failure, Is.Null);
            Assert.That(result.Brightness, Is.LessThan(128));
            Assert.That(result.ExposureSec, Is.GreaterThanOrEqualTo(FlatExposureSolver.PreferredMinExposureSec));
            Assert.That(Math.Abs(result.Level - 32768), Is.LessThanOrEqualTo(32768 * 0.05));
            Assert.That(result.Probes, Is.LessThanOrEqualTo(FlatExposureSolver.MaxProbes));
        }

        [Test]
        public async Task A_too_dim_panel_is_brightened_instead_of_running_past_the_maximum() {
            // Narrowband: 1500 ADU/s at brightness 50 would need ~21 s against a 10 s ceiling.
            var rig = new Rig { RatePerSec = 1500, ReferenceBrightness = 50 };
            var result = await FlatExposureSolver.SolveAsync(Limits(maxBrightness: 255), 1.0, 50, rig.Probe, CancellationToken.None);

            Assert.That(result.Failure, Is.Null);
            Assert.That(result.Brightness, Is.GreaterThan(50));
            Assert.That(result.ExposureSec, Is.LessThanOrEqualTo(10));
            Assert.That(Math.Abs(result.Level - 32768), Is.LessThanOrEqualTo(32768 * 0.05));
        }

        [Test]
        public async Task A_saturated_first_probe_backs_off_before_fitting() {
            var rig = new Rig { RatePerSec = 400_000 };
            var result = await FlatExposureSolver.SolveAsync(Limits(), 1.0, null, rig.Probe, CancellationToken.None);

            Assert.That(rig.Probes[0].Level, Is.EqualTo(ushort.MaxValue), "the first frame clipped");
            Assert.That(result.Failure, Is.Null);
            Assert.That(Math.Abs(result.Level - 32768), Is.LessThanOrEqualTo(32768 * 0.05));
        }

        [Test]
        public async Task No_light_fails_with_the_reason() {
            var rig = new Rig { RatePerSec = 0 };
            var result = await FlatExposureSolver.SolveAsync(Limits(), 1.0, null, rig.Probe, CancellationToken.None);

            Assert.That(result.ExposureSec, Is.Null);
            Assert.That(result.Failure, Does.Contain("no light"));
        }

        [Test]
        public async Task A_fixed_panel_too_dim_at_the_maximum_fails_naming_it() {
            var rig = new Rig { RatePerSec = 100 };
            var result = await FlatExposureSolver.SolveAsync(Limits(), 1.0, null, rig.Probe, CancellationToken.None);

            Assert.That(result.ExposureSec, Is.Null);
            Assert.That(result.Failure, Does.Contain("too dim"));
        }

        [Test]
        public async Task A_fixed_panel_only_moves_the_exposure_and_accepts_a_short_one() {
            // No brightness control (manual panel): a 0.3 s flat beats no flat.
            var rig = new Rig { RatePerSec = 100_000 };
            var result = await FlatExposureSolver.SolveAsync(Limits(), 1.0, null, rig.Probe, CancellationToken.None);

            Assert.That(result.Failure, Is.Null);
            Assert.That(result.Brightness, Is.Null);
            Assert.That(result.ExposureSec, Is.LessThan(1));
        }

        [Test]
        public void Level_is_the_median_of_the_central_half() {
            // 16×16 frame: centre 8×8 at 30000, everything else 60000, and two hot pixels in the centre.
            var pixels = new ushort[16 * 16];
            for (var y = 0; y < 16; y++) {
                for (var x = 0; x < 16; x++) {
                    pixels[(y * 16) + x] = (ushort)(x is >= 4 and < 12 && y is >= 4 and < 12 ? 30000 : 60000);
                }
            }
            pixels[(5 * 16) + 5] = 65535;
            pixels[(6 * 16) + 9] = 65535;

            var level = FlatLevel.Measure(pixels, 16, 16, bayerPattern: null);
            Assert.That(level.Level, Is.EqualTo(30000), "corners and hot pixels don't move a central median");
            Assert.That(level.Brightest, Is.EqualTo(65535));
        }

        [Test]
        public void A_colour_sensor_is_measured_by_its_brightest_channel() {
            // RGGB: R 20000, G 40000, B 10000 — the mean (27500) would let green clip first.
            var pixels = new ushort[16 * 16];
            for (var y = 0; y < 16; y++) {
                for (var x = 0; x < 16; x++) {
                    var even = (x % 2 == 0, y % 2 == 0);
                    pixels[(y * 16) + x] = even switch {
                        (true, true) => 20000,
                        (false, false) => 10000,
                        _ => 40000,
                    };
                }
            }
            Assert.That(FlatLevel.Measure(pixels, 16, 16, "RGGB").Level, Is.EqualTo(40000));
        }
    }
}
