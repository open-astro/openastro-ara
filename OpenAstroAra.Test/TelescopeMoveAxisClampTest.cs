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
using OpenAstroAra.Server;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System.Collections.Generic;
using System.Text.Json;

namespace OpenAstroAra.Test {

    // #1064/#1072/#1078 — the daemon, not the client's speed picker, is the guard on MoveAxis
    // rates. These pin the band-snap rule the endpoint relies on and the cache's settle rule.
    [TestFixture]
    public class TelescopeMoveAxisClampTest {

        private static readonly IReadOnlyList<(double Min, double Max)> OneBand = [(0.001, 6.016)];
        private static readonly double[] LegacyFloorAndCeiling = [0.5, 4.0];
        // A discrete-rate mount: 0.5×, 2×, 8× sidereal-ish steps, then a slew band.
        private static readonly IReadOnlyList<(double Min, double Max)> Discrete = [(0.002, 0.002), (0.008, 0.008), (0.033, 0.033), (1.0, 4.0)];
        private static readonly double[] DiscreteEndpoints = [0.002, 0.008, 0.033, 1.0, 4.0];
        private static readonly double[] SixOnly = [6.0];

        [Test]
        public void Stop_passes_through_without_a_rate_read() {
            // rate 0 must never be gated — even when no bands are known.
            Assert.That(TelescopeService.SnapMoveAxisRate(0, null), Is.EqualTo(0));
            Assert.That(TelescopeService.SnapMoveAxisRate(0, OneBand), Is.EqualTo(0));
            Assert.That(TelescopeService.SnapMoveAxisRate(double.NaN, OneBand), Is.EqualTo(0));
        }

        [Test]
        public void Rate_inside_a_band_is_unchanged_with_sign_preserved() {
            Assert.That(TelescopeService.SnapMoveAxisRate(1.5, OneBand), Is.EqualTo(1.5));
            Assert.That(TelescopeService.SnapMoveAxisRate(-1.5, OneBand), Is.EqualTo(-1.5));
            Assert.That(TelescopeService.SnapMoveAxisRate(6.016, OneBand), Is.EqualTo(6.016));
            Assert.That(TelescopeService.SnapMoveAxisRate(2.5, Discrete), Is.EqualTo(2.5));
        }

        [Test]
        public void Rate_over_the_top_band_is_capped_and_below_the_lowest_is_raised() {
            Assert.That(TelescopeService.SnapMoveAxisRate(50, OneBand), Is.EqualTo(6.016));
            Assert.That(TelescopeService.SnapMoveAxisRate(-50, OneBand), Is.EqualTo(-6.016));
            // #1072 — the client's 1 % preset can land below the lowest band's minimum; #1085 — it
            // is raised only while within SnapUpBoundFactor (4x) of that minimum.
            Assert.That(TelescopeService.SnapMoveAxisRate(0.0005, OneBand), Is.EqualTo(0.001));
            Assert.That(TelescopeService.SnapMoveAxisRate(0.00025, OneBand), Is.EqualTo(0.001), "exactly 4x slower is still raised");
            Assert.That(TelescopeService.SnapMoveAxisRate(-0.001, Discrete), Is.EqualTo(-0.002));
        }

        [Test]
        public void Rate_more_than_the_bound_factor_below_the_lowest_band_is_refused_not_raised() {
            // #1085 — the exact case from the issue: a mount whose only band starts at 2 deg/s and the
            // picker's 1 % preset of its 6 deg/s max (0.06). Snapping would drive 2 deg/s, 33x faster
            // than picked; the daemon refuses instead (409 at the endpoint).
            IReadOnlyList<(double Min, double Max)> highFloor = [(2.0, 6.0)];
            var ex = Assert.Throws<System.InvalidOperationException>(() => TelescopeService.SnapMoveAxisRate(0.06, highFloor));
            Assert.That(ex!.Message, Does.Contain("4x slower").And.Contain("2 deg/s"));
            // A tiny request is still printed as a number the user can act on, never "0 deg/s".
            var tiny = Assert.Throws<System.InvalidOperationException>(() => TelescopeService.SnapMoveAxisRate(0.00001, OneBand));
            Assert.That(tiny!.Message, Does.Contain("1E-05 deg/s").And.Not.Contain("rate 0 deg/s"));
            Assert.Throws<System.InvalidOperationException>(() => TelescopeService.SnapMoveAxisRate(-0.06, highFloor));
            // Within the bound (0.5 = min / 4) it is still raised, sign preserved.
            Assert.That(TelescopeService.SnapMoveAxisRate(0.5, highFloor), Is.EqualTo(2.0));
            Assert.That(TelescopeService.SnapMoveAxisRate(-0.6, highFloor), Is.EqualTo(-2.0));
            // Just past the bound is refused, on a discrete ladder too.
            Assert.Throws<System.InvalidOperationException>(() => TelescopeService.SnapMoveAxisRate(0.0001, OneBand));
            Assert.Throws<System.InvalidOperationException>(() => TelescopeService.SnapMoveAxisRate(-0.0001, Discrete));
        }

        [Test]
        public void Rate_in_a_gap_between_bands_snaps_to_the_nearest_edge() {
            // Between 0.033 and 1.0: nearer the lower edge → 0.033; nearer the upper → 1.0.
            Assert.That(TelescopeService.SnapMoveAxisRate(0.05, Discrete), Is.EqualTo(0.033));
            Assert.That(TelescopeService.SnapMoveAxisRate(0.9, Discrete), Is.EqualTo(1.0));
            Assert.That(TelescopeService.SnapMoveAxisRate(-0.9, Discrete), Is.EqualTo(-1.0));
            // Between two discrete steps.
            Assert.That(TelescopeService.SnapMoveAxisRate(0.003, Discrete), Is.EqualTo(0.002));
        }

        [Test]
        public void Nonzero_rate_with_no_known_bands_is_refused() {
            Assert.Throws<System.InvalidOperationException>(() => TelescopeService.SnapMoveAxisRate(1.0, null));
            Assert.Throws<System.InvalidOperationException>(() => TelescopeService.SnapMoveAxisRate(1.0, []));
        }

        [Test]
        public void Rate_cache_settles_on_completed_reads_CanMoveAxis_false_or_the_time_window() {
            Assert.That(TelescopeService.ShouldSettleAxisRates(readsCompleted: true, mountCannotMoveAxis: false, windowElapsed: false), Is.True);
            Assert.That(TelescopeService.ShouldSettleAxisRates(readsCompleted: false, mountCannotMoveAxis: true, windowElapsed: false), Is.True);
            Assert.That(TelescopeService.ShouldSettleAxisRates(readsCompleted: false, mountCannotMoveAxis: false, windowElapsed: false), Is.False, "a thrown read retries");
            Assert.That(TelescopeService.ShouldSettleAxisRates(readsCompleted: false, mountCannotMoveAxis: false, windowElapsed: true), Is.True, "bounded by wall clock, not by command-triggered passes");
        }

        [Test]
        public void Secondary_axis_borrows_the_primary_bands_only_when_its_own_never_answered() {
            IReadOnlyList<(double Min, double Max)>?[] unanswered = [OneBand, null, null];
            Assert.That(TelescopeService.BandsForAxis(unanswered, 1, out var borrowed), Is.SameAs(OneBand));
            Assert.That(borrowed, Is.True, "AxisRates(Secondary) threw all session → N/S uses the primary's bands");
            IReadOnlyList<(double Min, double Max)>?[] honestlyEmpty = [OneBand, [], null];
            Assert.That(TelescopeService.BandsForAxis(honestlyEmpty, 1, out borrowed), Is.Empty, "the mount said 'no rates' → stays refused");
            Assert.That(borrowed, Is.False);
            IReadOnlyList<(double Min, double Max)>?[] known = [OneBand, Discrete, null];
            Assert.That(TelescopeService.BandsForAxis(known, 1, out borrowed), Is.SameAs(Discrete));
            Assert.That(borrowed, Is.False);
            IReadOnlyList<(double Min, double Max)>?[] primaryUnknown = [null, null, null];
            Assert.That(TelescopeService.BandsForAxis(primaryUnknown, 1, out borrowed), Is.Null);
            Assert.That(borrowed, Is.False);
            Assert.That(TelescopeService.BandsForAxis(known, 0, out borrowed), Is.SameAs(OneBand), "the primary never borrows");
            Assert.That(TelescopeService.BandsForAxis(null, 0, out _), Is.Null, "nothing read yet");
        }

        [Test]
        public void Picker_endpoints_are_both_ends_of_every_positive_band_deduped_ascending() {
            var endpoints = TelescopeService.EndpointsOf(Discrete);
            Assert.That(endpoints, Is.EqualTo(DiscreteEndpoints));
        }

        [Test]
        public void Endpoints_cannot_tell_a_discrete_rate_from_a_band_but_the_band_dtos_can() {
            // #1126 — the legacy endpoint list flattens (0, 6) and (6, 6) to the same [6] (a zero
            // minimum is dropped), so the client could not tell "any speed up to 6" from "6 only".
            IReadOnlyList<(double Min, double Max)> continuous = [(0.0, 6.0)];
            IReadOnlyList<(double Min, double Max)> discrete = [(6.0, 6.0)];
            Assert.That(TelescopeService.EndpointsOf(continuous), Is.EqualTo(SixOnly));
            Assert.That(TelescopeService.EndpointsOf(discrete), Is.EqualTo(SixOnly));
            Assert.That(TelescopeService.BandDtosOf(continuous), Is.EqualTo(new[] { new MoveAxisRateBandDto(0.0, 6.0) }));
            Assert.That(TelescopeService.BandDtosOf(discrete), Is.EqualTo(new[] { new MoveAxisRateBandDto(6.0, 6.0) }));
            // Two discrete rates vs one band spanning them.
            IReadOnlyList<(double Min, double Max)> twoSteps = [(0.004, 0.004), (2.0, 2.0)];
            IReadOnlyList<(double Min, double Max)> oneSpan = [(0.004, 2.0)];
            Assert.That(TelescopeService.EndpointsOf(twoSteps), Is.EqualTo(TelescopeService.EndpointsOf(oneSpan)));
            Assert.That(TelescopeService.BandDtosOf(twoSteps), Has.Count.EqualTo(2));
            Assert.That(TelescopeService.BandDtosOf(oneSpan), Is.EqualTo(new[] { new MoveAxisRateBandDto(0.004, 2.0) }));
        }

        [Test]
        public void Bands_ride_the_wire_as_min_max_pairs_beside_the_legacy_endpoint_list() {
            var caps = new TelescopeCapabilitiesDto(
                CanSlew: true, CanSync: false, CanPark: false, CanUnpark: false,
                CanSetTracking: true, CanPulseGuide: false, CanFindHome: false,
                SupportedSiderealRates: new List<string>(),
                CanMoveAxis: true,
                MoveAxisRatesDegPerSec: [6.0],
                MoveAxisRateBandsDegPerSec: [new MoveAxisRateBandDto(0.0, 6.0)]);
            var json = JsonSerializer.Serialize(caps, AraJsonSerializerContext.Default.TelescopeCapabilitiesDto);
            Assert.That(json, Does.Contain("\"move_axis_rates_deg_per_sec\":[6]"), "the endpoint list stays for older clients");
            Assert.That(json, Does.Contain("\"move_axis_rate_bands_deg_per_sec\":[{\"min\":0,\"max\":6}]"));
            var back = JsonSerializer.Deserialize(json, AraJsonSerializerContext.Default.TelescopeCapabilitiesDto)!;
            Assert.That(back.MoveAxisRateBandsDegPerSec, Is.EqualTo(new[] { new MoveAxisRateBandDto(0.0, 6.0) }));
        }

        [Test]
        public void Pad_bands_are_the_primary_bands_clipped_to_the_secondary_floor_and_ceiling() {
            // #1126 — the pad drives both axes with one rate. The secondary's ceiling was already
            // applied; its FLOOR was not, so a slow diagonal press moved E/W and 409'd N/S.
            IReadOnlyList<(double Min, double Max)> highFloor = [(2.0, 6.0)];
            Assert.That(TelescopeService.PadBandsFrom((OneBand, highFloor)), Is.EqualTo(new[] { (2.0, 6.0) }));
            IReadOnlyList<(double Min, double Max)> lowCeiling = [(0.001, 4.0)];
            Assert.That(TelescopeService.PadBandsFrom((OneBand, lowCeiling)), Is.EqualTo(new[] { (0.001, 4.0) }));
            // A discrete primary ladder loses the steps outside the secondary's range and the slew
            // band is clipped, not dropped.
            IReadOnlyList<(double Min, double Max)> midRange = [(0.01, 2.0)];
            Assert.That(TelescopeService.PadBandsFrom((Discrete, midRange)), Is.EqualTo(new[] { (0.033, 0.033), (1.0, 2.0) }));
            // A discrete secondary (#1230): the intersections, i.e. the secondary's own steps — not the
            // [floor, ceiling] window, inside which a 0.2 °/s chip snapped to 0.033 on N/S.
            Assert.That(TelescopeService.PadBandsFrom((OneBand, Discrete)), Is.EqualTo(Discrete));
            // A thrown (null) or honestly-empty secondary applies no clip.
            Assert.That(TelescopeService.PadBandsFrom((OneBand, null)), Is.EqualTo(OneBand));
            Assert.That(TelescopeService.PadBandsFrom((OneBand, [])), Is.EqualTo(OneBand));
            // Nothing survives the clip → the primary set as-is rather than no speeds at all.
            IReadOnlyList<(double Min, double Max)> slowOnly = [(0.001, 0.5)];
            Assert.That(TelescopeService.PadBandsFrom((slowOnly, highFloor)), Is.EqualTo(slowOnly));
            // No primary → no speeds.
            Assert.That(TelescopeService.PadBandsFrom((null, highFloor)), Is.Empty);
            Assert.That(TelescopeService.PadBandsFrom(([], highFloor)), Is.Empty);
            // Two primary bands clipped to the same secondary window collapse to one on the wire.
            IReadOnlyList<(double Min, double Max)> overlapping = [(0.5, 3.0), (1.0, 5.0)];
            IReadOnlyList<(double Min, double Max)> narrow = [(2.0, 2.5)];
            Assert.That(TelescopeService.PadBandsFrom((overlapping, narrow)), Is.EqualTo(new[] { (2.0, 2.5) }));
        }

        [Test]
        public void Legacy_rate_list_is_the_endpoints_of_the_clipped_bands() {
            // #1126 — the capabilities compose EndpointsOf(PadBandsFrom(pad)): the legacy list
            // carries the secondary's floor too, not just its ceiling (a value that is not an
            // endpoint of any primary band).
            IReadOnlyList<(double Min, double Max)> secondary = [(0.5, 4.0)];
            Assert.That(TelescopeService.EndpointsOf(TelescopeService.PadBandsFrom((OneBand, secondary))),
                Is.EqualTo(LegacyFloorAndCeiling));
        }

        [Test]
        public void Any_rate_inside_a_pad_band_is_accepted_by_both_axes_of_an_asymmetric_mount() {
            // The diagonal press: the same rate goes to both axes, and neither may refuse OR snap it (#1230).
            IReadOnlyList<(double Min, double Max)> primary = [(0.001, 6.016)];
            IReadOnlyList<(double Min, double Max)> secondary = [(0.5, 0.5), (2.0, 4.0)];
            var pad = TelescopeService.PadBandsFrom((primary, secondary));
            Assert.That(pad, Is.EqualTo(new[] { (0.5, 0.5), (2.0, 4.0) }));
            foreach (var (min, max) in pad) {
                foreach (var rate in new[] { min, (min + max) / 2, max }) {
                    Assert.That(TelescopeService.SnapMoveAxisRate(rate, primary), Is.EqualTo(rate).Within(1e-12), $"primary snapped {rate}");
                    Assert.That(TelescopeService.SnapMoveAxisRate(rate, secondary), Is.EqualTo(rate).Within(1e-12), $"secondary snapped {rate}");
                    Assert.That(TelescopeService.SnapMoveAxisRate(-rate, secondary), Is.EqualTo(-rate).Within(1e-12), $"secondary snapped {-rate}");
                }
            }
            // The unclipped primary floor is what used to be offered — and it 409s the secondary.
            Assert.Throws<System.InvalidOperationException>(() => TelescopeService.SnapMoveAxisRate(0.06, secondary));
        }
    

        [Test]
        public void Pad_bands_are_the_intersections_with_the_secondary_bands_not_its_window() {
            // #1230 — the issue's case: a continuous primary over a discrete secondary used to publish
            // (0.002, 4.0); the 5 %/10 % chips of 4.0 (0.2 / 0.4 °/s) then snapped to 0.033 on N/S, so a
            // diagonal press ran E/W 6–12× faster than N/S. Now only the secondary's steps are offered.
            IReadOnlyList<(double Min, double Max)> continuous = [(0.002, 4.0)];
            var pad = TelescopeService.PadBandsFrom((continuous, Discrete));
            Assert.That(pad, Is.EqualTo(Discrete));
            foreach (var chip in new[] { 0.2, 0.4 }) {
                Assert.That(pad.Any(b => chip >= b.Min && chip <= b.Max), Is.False, $"{chip} is not offered");
            }
            Assert.That(TelescopeService.SnapMoveAxisRate(0.2, Discrete), Is.EqualTo(0.033).Within(1e-12), "what 0.2 would have become on N/S");
            // Two discrete ladders intersect on their common steps only.
            IReadOnlyList<(double Min, double Max)> other = [(0.008, 0.008), (0.5, 0.5), (1.0, 4.0)];
            Assert.That(TelescopeService.PadBandsFrom((Discrete, other)), Is.EqualTo(new[] { (0.008, 0.008), (1.0, 4.0) }));
            // Disjoint everywhere → the primary as-is (better a rate the secondary may snap than none).
            IReadOnlyList<(double Min, double Max)> disjoint = [(5.0, 6.0)];
            Assert.That(TelescopeService.PadBandsFrom((Discrete, disjoint)), Is.EqualTo(Discrete));
        }
    }
}
