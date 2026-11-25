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
using OpenAstroAra.Server.Services;
using System;

namespace OpenAstroAra.Test {

    [TestFixture]
    public class RigOccupancyTest {

        [Test]
        public void A_routine_answers_only_for_what_it_holds() {
            var aligning = true;
            var occupancy = new RigOccupancy()
                .Register("polar alignment", RigHolding.Mount | RigHolding.GuideCamera, () => aligning);

            Assert.That(occupancy.Holder(RigHolding.Disk), Is.Null, "polar alignment writes no frames");
            Assert.That(occupancy.Holder(RigHolding.Mount), Is.EqualTo("polar alignment"));
            Assert.That(occupancy.Holder(), Is.EqualTo("polar alignment"));
            aligning = false;
            Assert.That(occupancy.Holder(), Is.Null, "a routine that ended answers nothing");
        }

        [Test]
        public void The_session_holds_the_rig_for_a_run_with_no_catalog_session() {
            var runs = new ActiveRunSessionRegistry();
            var sequencer = new Mock<ISequencerService>();
            var occupancy = new RigOccupancy().RegisterSession(runs, () => sequencer.Object);
            Assert.That(occupancy.Holder(), Is.Null, "nothing runs");

            sequencer.SetupGet(s => s.HasUnfinishedRun).Returns(true);
            Assert.That(occupancy.Holder(RigHolding.Disk), Is.EqualTo(RigOccupancy.Session), "the run is on with no catalog session");

            sequencer.SetupGet(s => s.HasUnfinishedRun).Returns(false);
            var session = Guid.NewGuid();
            runs.Enter(session);
            Assert.That(occupancy.Holder(RigHolding.Mount), Is.EqualTo(RigOccupancy.Session), "an open catalog session counts");
            runs.Exit(session);
            Assert.That(occupancy.Holder(), Is.Null);
        }
    }
}
