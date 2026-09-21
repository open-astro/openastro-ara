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
using System.Collections.Generic;

namespace OpenAstroAra.Test {

    // #1064/#1072/#1078 — the daemon, not the client's speed picker, is the guard on MoveAxis
    // rates. These pin the band-snap rule the endpoint relies on and the cache's settle rule.
    [TestFixture]
    public class TelescopeMoveAxisClampTest {

        private static readonly IReadOnlyList<(double Min, double Max)> OneBand = [(0.001, 6.016)];
        // A discrete-rate mount: 0.5×, 2×, 8× sidereal-ish steps, then a slew band.
        private static readonly IReadOnlyList<(double Min, double Max)> Discrete = [(0.002, 0.002), (0.008, 0.008), (0.033, 0.033), (1.0, 4.0)];
        private static readonly double[] DiscreteEndpoints = [0.002, 0.008, 0.033, 1.0, 4.0];

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
            // #1072 — the client's 1 % preset can land below the lowest band's minimum.
            Assert.That(TelescopeService.SnapMoveAxisRate(0.0001, OneBand), Is.EqualTo(0.001));
            Assert.That(TelescopeService.SnapMoveAxisRate(-0.0001, Discrete), Is.EqualTo(-0.002));
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
    }
}
