#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Test {

    /// <summary>#1689 — the dome block (geometry and Follow the mount) belongs to its profile: it is saved
    /// with the profile, profile select replaces it, and the file store keeps it across a daemon restart.</summary>
    [TestFixture]
    public class DomeSettingsStoreTest {

        private static readonly DomeSettingsDto RigA = new(FollowMount: true, DomeRadiusMm: 1500, ScopePositionNorthSouthMm: 120);
        private static readonly DomeSettingsDto RigB = new(DomeRadiusMm: 2200, AzimuthToleranceDegrees: 4);

        [Test]
        public void A_saved_profile_carries_its_dome_block() {
            // The multi-profile bridge saves the live store into the active profile with Capture.
            var live = new InMemoryProfileStore();
            live.PutDomeSettings(RigA);
            Assert.That(ProfileStoreSnapshot.Capture(live).Dome, Is.EqualTo(RigA));
        }

        [Test]
        public void Selecting_a_profile_replaces_the_previous_rigs_dome_block() {
            // Profile select loads the saved profile with Apply; rig A's geometry and follow flag must not stay on.
            var live = new InMemoryProfileStore();
            live.PutDomeSettings(RigA);
            var rigB = ProfileStoreSnapshot.Capture(new InMemoryProfileStore()) with { Dome = RigB };

            ProfileStoreSnapshot.Apply(live, rigB);
            Assert.That(live.GetDomeSettings(), Is.EqualTo(RigB));

            live.PutDomeSettings(RigA);
            ProfileStoreSnapshot.Apply(live, rigB with { Dome = null });
            Assert.That(live.GetDomeSettings(), Is.EqualTo(DomeSettingsDto.Default), "a profile saved before the dome block: no geometry, not following");
        }

        [Test]
        [Category("IO")] // #1265 — real disk, loopback HTTP or a simulator: not part of the quick unit run
        public void The_dome_block_survives_a_daemon_restart() {
            var dir = Path.Combine(Path.GetTempPath(), "ara-dome-settings-" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            try {
                new FileProfileStore(dir).PutDomeSettings(RigA);
                var reopened = new FileProfileStore(dir); // read back from profile.json
                Assert.That(reopened.GetDomeSettings(), Is.EqualTo(RigA));
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}