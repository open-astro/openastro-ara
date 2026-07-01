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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Test {

    /// <summary>
    /// NEXTGEN §1 — the filter/emission-aware advice: OpenNGC-type → emission-class table, the
    /// advice matrix (target class × declared filters × Bortle), the never-guess rule (no filter
    /// set / unknown type → null), and the representative-filter pick the Optimal-Sub figure uses.
    /// </summary>
    [TestFixture]
    public class FilterAdviceTest {

        private static PlanningFilterDto F(string name, FilterKind kind, double bw = 0) =>
            new(Name: name, Kind: kind, BandwidthNm: bw);

        private static FilterSetDto MonoNbSet() => new([
            F("L", FilterKind.L), F("Ha 7nm", FilterKind.Ha), F("OIII", FilterKind.Oiii)]);

        private static FilterSetDto OscDuoSet() => new([
            F("OSC", FilterKind.Osc), F("L-eXtreme", FilterKind.Duo)]);

        private static FilterSetDto BroadbandOnlySet() => new([
            F("L", FilterKind.L), F("R", FilterKind.R), F("G", FilterKind.G), F("B", FilterKind.B)]);

        [Test]
        public void Emission_classes_cover_the_openngc_type_codes_and_starter_names() {
            // Emission-line: HII regions, emission nebulae, planetary nebulae, supernova remnants.
            foreach (var t in new[] { "HII", "EmN", "PN", "SNR" }) {
                Assert.That(FilterAdvice.ClassifyEmission(t), Is.EqualTo(EmissionClass.EmissionLine), t);
            }
            // Continuum: galaxies, clusters, reflection nebulae (+ the starter catalog's plain names).
            foreach (var t in new[] { "G", "GPair", "GTrpl", "GGroup", "OCl", "GCl", "RfN", "galaxy", "cluster" }) {
                Assert.That(FilterAdvice.ClassifyEmission(t), Is.EqualTo(EmissionClass.Continuum), t);
            }
            // Mixed: generic nebulae + cluster-with-nebulosity.
            foreach (var t in new[] { "Neb", "Cl+N", "nebula" }) {
                Assert.That(FilterAdvice.ClassifyEmission(t), Is.EqualTo(EmissionClass.Mixed), t);
            }
            // Anything else — including null/blank — is Unknown (→ no advice, never guess).
            foreach (var t in new[] { "Star", "Dup", "Other", "", "   ", null }) {
                Assert.That(FilterAdvice.ClassifyEmission(t), Is.EqualTo(EmissionClass.Unknown), t ?? "<null>");
            }
        }

        [Test]
        public void No_filter_set_or_unknown_type_yields_no_advice() {
            Assert.That(FilterAdvice.Advise(EmissionClass.EmissionLine, new FilterSetDto([]), 5), Is.Null,
                "empty filter set → we don't know what the user owns → say nothing");
            Assert.That(FilterAdvice.Advise(EmissionClass.Unknown, MonoNbSet(), 5), Is.Null,
                "unknown emission character → never guess");
        }

        [Test]
        public void An_emission_target_with_mono_narrowband_recommends_narrowband() {
            var advice = FilterAdvice.Advise(EmissionClass.EmissionLine, MonoNbSet(), 5);
            Assert.That(advice, Is.Not.Null);
            Assert.That(advice!.Value.Approach, Is.EqualTo(FilterApproach.Narrowband));
            Assert.That(advice.Value.Reason, Does.Contain("Ha 7nm").And.Contain("~14×"),
                "the reason names the user's filters and the 100/7 sky-flux ratio");
        }

        [Test]
        public void An_emission_target_with_only_osc_plus_duoband_recommends_duoband() {
            var advice = FilterAdvice.Advise(EmissionClass.EmissionLine, OscDuoSet(), 5);
            Assert.That(advice!.Value.Approach, Is.EqualTo(FilterApproach.Duoband));
            Assert.That(advice.Value.Reason, Does.Contain("L-eXtreme"));
        }

        [Test]
        public void An_emission_target_with_broadband_only_warns_harder_under_a_bright_sky() {
            var dark = FilterAdvice.Advise(EmissionClass.EmissionLine, BroadbandOnlySet(), 3);
            var bright = FilterAdvice.Advise(EmissionClass.EmissionLine, BroadbandOnlySet(), 7);
            Assert.That(dark!.Value.Approach, Is.EqualTo(FilterApproach.Broadband));
            Assert.That(bright!.Value.Approach, Is.EqualTo(FilterApproach.Broadband));
            Assert.That(bright.Value.Reason, Does.Contain("expect many hours"),
                "Bortle ≥ 5 sharpens the time-cost warning");
            Assert.That(dark.Value.Reason, Does.Not.Contain("expect many hours"),
                "a dark site keeps the gentler phrasing");
        }

        [Test]
        public void A_continuum_target_recommends_broadband_with_a_bright_sky_caveat() {
            var dark = FilterAdvice.Advise(EmissionClass.Continuum, MonoNbSet(), 4);
            var bright = FilterAdvice.Advise(EmissionClass.Continuum, MonoNbSet(), 8);
            Assert.That(dark!.Value.Approach, Is.EqualTo(FilterApproach.Broadband),
                "narrowband would starve a continuum target even when the user owns NB filters");
            Assert.That(bright!.Value.Reason, Does.Contain("Bortle 8"),
                "Bortle ≥ 6 appends the darker-site caveat");
            Assert.That(dark.Value.Reason, Does.Not.Contain("darker site"));
        }

        [Test]
        public void A_mixed_target_tips_to_narrowband_only_under_a_bright_sky() {
            Assert.That(FilterAdvice.Advise(EmissionClass.Mixed, MonoNbSet(), 6)!.Value.Approach,
                Is.EqualTo(FilterApproach.Narrowband));
            Assert.That(FilterAdvice.Advise(EmissionClass.Mixed, OscDuoSet(), 6)!.Value.Approach,
                Is.EqualTo(FilterApproach.Duoband));
            Assert.That(FilterAdvice.Advise(EmissionClass.Mixed, MonoNbSet(), 4)!.Value.Approach,
                Is.EqualTo(FilterApproach.Broadband), "dark sky → broadband captures both components");
            Assert.That(FilterAdvice.Advise(EmissionClass.Mixed, BroadbandOnlySet(), 8)!.Value.Approach,
                Is.EqualTo(FilterApproach.Broadband), "no NB/duo filters → broadband is all there is");
        }

        [Test]
        public void Representative_filters_prefer_ha_then_l_and_report_effective_bandwidth() {
            var set = new FilterSetDto([
                F("OIII", FilterKind.Oiii), F("Ha 3nm", FilterKind.Ha, bw: 3),
                F("OSC", FilterKind.Osc), F("L", FilterKind.L), F("L-eXtreme", FilterKind.Duo)]);

            var nb = FilterAdvice.RepresentativeFilter(set, FilterApproach.Narrowband);
            Assert.That(nb!.Name, Is.EqualTo("Ha 3nm"), "Hα preferred over OIII");
            Assert.That(FilterAdvice.EffectiveBandwidthNm(nb), Is.EqualTo(3), "explicit bandwidth wins");

            var bb = FilterAdvice.RepresentativeFilter(set, FilterApproach.Broadband);
            Assert.That(bb!.Name, Is.EqualTo("L"), "L preferred over OSC");
            Assert.That(FilterAdvice.EffectiveBandwidthNm(bb),
                Is.EqualTo(OptimalSubCalculator.DefaultBandwidthNm(FilterKind.L)), "0 → kind default");

            Assert.That(FilterAdvice.RepresentativeFilter(set, FilterApproach.Duoband)!.Name,
                Is.EqualTo("L-eXtreme"));
            Assert.That(FilterAdvice.RepresentativeFilter(new FilterSetDto([F("L", FilterKind.L)]),
                FilterApproach.Narrowband), Is.Null, "no filter of that approach → null");
        }
    }
}
