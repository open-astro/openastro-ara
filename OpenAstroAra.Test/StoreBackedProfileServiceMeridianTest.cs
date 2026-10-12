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
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Test {

    /// <summary>#1138 — ProfileStoreMapper.ApplyMeridian: the store's meridian policies mirrored onto the NINA-side profile for legacy readers.</summary>
    [TestFixture]
    public class StoreBackedProfileServiceMeridianTest {

        private static SafetyPoliciesDto Safety(string refocus) =>
            new(OnUnsafe: "pause_and_park", AutoResumeWhenSafe: true, ResumeDelayMin: 10,
                MeridianFlipAuto: true, MeridianPauseMin: 7, MeridianRecenter: false,
                MeridianRecalGuider: true, OnAltitudeLimit: "skip_target",
                ParkIfNoMoreTargets: true, OnGuiderLost: "pause_and_retry",
                GuiderRetryTimeoutSec: 60, SkipTargetIfRecoveryFails: true,
                MeridianRefocus: refocus);

        private static (IProfile Profile, Mock<IMeridianFlipSettings> Meridian) Profile() {
            var meridian = new Mock<IMeridianFlipSettings>();
            meridian.SetupAllProperties();
            var profile = new Mock<IProfile>();
            profile.SetupGet(p => p.MeridianFlipSettings).Returns(meridian.Object);
            return (profile.Object, meridian);
        }

        [TestCase("never", false)]
        [TestCase("if_temp_drifted", true)]
        [TestCase("always", true)]
        [TestCase("garbage", true)]
        public void AutoFocusAfterFlip_mirrors_the_refocus_policy(string refocus, bool expected) {
            var (profile, meridian) = Profile();
            ProfileStoreMapper.ApplyMeridian(Safety(refocus), profile);
            Assert.That(meridian.Object.AutoFocusAfterFlip, Is.EqualTo(expected));
            Assert.That(meridian.Object.PauseTimeBeforeMeridian, Is.EqualTo(7));
            Assert.That(meridian.Object.Recenter, Is.False);
        }
    }
}
