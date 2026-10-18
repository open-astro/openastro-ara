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

    /// <summary>Review of #1389: the Guiding Assistant's start went out with the live view's 5 s
    /// bound, so a guider that took longer to check the star and park its output had its run reported
    /// as failed. The start has its own bound, longer than a poll's and shorter than the client's 30 s
    /// so the daemon's refusal is the one the user reads.</summary>
    [TestFixture]
    public class PHD2GuidingAssistantTimeoutTest {

        [Test]
        public void StartTimeout_IsLongerThanALivePoll_AndUnderTheClients30s() {
            Assert.That(PHD2Guider.GuidingAssistantStartTimeoutMs, Is.GreaterThan(PHD2Guider.LiveRpcTimeoutMs));
            Assert.That(PHD2Guider.GuidingAssistantStartTimeoutMs, Is.GreaterThanOrEqualTo(20000));
            Assert.That(PHD2Guider.GuidingAssistantStartTimeoutMs, Is.LessThan(30000));
        }
    }
}
