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
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §63.9 fork identification — <see cref="PHD2Guider.IdentifyGuiderFork"/>, the pure precedence rule
    /// the connect handshake uses to decide openastro-guider vs stock PHD2: get_version RPC fork →
    /// "Version" event fork → legacy version/subver substring. Guards the regression that ARA's old check
    /// only matched the pre-rename "openastro-phd2" string and mis-flagged the released "openastro-guider".
    /// </summary>
    [TestFixture]
    public class PHD2GuiderForkTest {

        [Test]
        public void Rpc_fork_openastro_guider_is_identified() {
            var id = PHD2Guider.IdentifyGuiderFork(
                rpcFork: "openastro-guider", rpcOverlapSupport: true,
                eventFork: null, eventOverlapSupport: null,
                phdVersion: "2.6.11dev5", phdSubver: "");
            Assert.That(id.IsOpenAstroGuider, Is.True);
            Assert.That(id.ForkName, Is.EqualTo("openastro-guider"));
            Assert.That(id.OverlapSupport, Is.True);
        }

        [Test]
        public void Event_fork_is_used_when_the_rpc_fork_is_absent() {
            // A pre-#57 handshake where the RPC failed but the catch-up Version event carries the fork key.
            var id = PHD2Guider.IdentifyGuiderFork(
                rpcFork: null, rpcOverlapSupport: null,
                eventFork: "openastro-guider", eventOverlapSupport: true,
                phdVersion: "2.6.11", phdSubver: "");
            Assert.That(id.IsOpenAstroGuider, Is.True);
            Assert.That(id.ForkName, Is.EqualTo("openastro-guider"));
            Assert.That(id.OverlapSupport, Is.True);
        }

        [Test]
        public void Rpc_fork_takes_precedence_over_the_event_fork() {
            // RPC is authoritative: its fork + overlap value win even when the event disagrees.
            var id = PHD2Guider.IdentifyGuiderFork(
                rpcFork: "openastro-guider", rpcOverlapSupport: false,
                eventFork: "phd2", eventOverlapSupport: true,
                phdVersion: null, phdSubver: null);
            Assert.That(id.IsOpenAstroGuider, Is.True);
            Assert.That(id.ForkName, Is.EqualTo("openastro-guider"));
            Assert.That(id.OverlapSupport, Is.False, "the RPC overlap value must win over the event's");
        }

        [Test]
        public void Legacy_subver_marker_identifies_a_pre_57_fork_daemon() {
            // No fork key on either source (older build) → fall back to the PHDSubver marker.
            var id = PHD2Guider.IdentifyGuiderFork(
                rpcFork: null, rpcOverlapSupport: null,
                eventFork: null, eventOverlapSupport: null,
                phdVersion: "2.6.11", phdSubver: "openastroara-1");
            Assert.That(id.IsOpenAstroGuider, Is.True);
            Assert.That(id.ForkName, Is.EqualTo("openastro-guider"), "inferred canonical name when no explicit fork key");
            Assert.That(id.OverlapSupport, Is.False);
        }

        [Test]
        public void Legacy_pre_rename_fork_string_is_still_accepted() {
            var id = PHD2Guider.IdentifyGuiderFork(
                rpcFork: "openastro-phd2", rpcOverlapSupport: true,
                eventFork: null, eventOverlapSupport: null,
                phdVersion: null, phdSubver: null);
            Assert.That(id.IsOpenAstroGuider, Is.True);
            Assert.That(id.ForkName, Is.EqualTo("openastro-phd2"), "the daemon's own fork string is reported verbatim");
        }

        [Test]
        public void Upstream_phd2_is_not_identified_as_the_fork() {
            var id = PHD2Guider.IdentifyGuiderFork(
                rpcFork: null, rpcOverlapSupport: null,
                eventFork: null, eventOverlapSupport: null,
                phdVersion: "2.6.11", phdSubver: "");
            Assert.That(id.IsOpenAstroGuider, Is.False);
            Assert.That(id.ForkName, Is.EqualTo("PHD2"));
            Assert.That(id.OverlapSupport, Is.False);
        }

        [Test]
        public void An_explicit_non_openastro_fork_string_is_authoritative() {
            // If the daemon explicitly reports a non-OpenAstro fork, that wins — we do NOT fall through to
            // the legacy substring even when the subver happens to contain an openastro marker.
            var id = PHD2Guider.IdentifyGuiderFork(
                rpcFork: "phd2", rpcOverlapSupport: null,
                eventFork: null, eventOverlapSupport: null,
                phdVersion: null, phdSubver: "openastroara-leftover");
            Assert.That(id.IsOpenAstroGuider, Is.False);
            Assert.That(id.ForkName, Is.EqualTo("phd2"));
        }

        [Test]
        public void Overlap_support_prefers_rpc_then_event_then_false() {
            Assert.That(PHD2Guider.IdentifyGuiderFork("openastro-guider", null, null, true, null, null).OverlapSupport,
                Is.True, "falls back to the event value when the RPC omits it");
            Assert.That(PHD2Guider.IdentifyGuiderFork("openastro-guider", null, null, null, null, null).OverlapSupport,
                Is.False, "defaults to false when neither source reports it");
        }
    }
}
