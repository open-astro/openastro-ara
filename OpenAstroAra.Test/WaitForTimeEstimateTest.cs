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
using OpenAstroAra.Sequencer;
using OpenAstroAra.Sequencer.Utility.DateTimeProvider;
using OpenAstroAra.Sequencer.SequenceItem.Utility;
using System;
using System.Collections.Generic;

namespace OpenAstroAra.Test {

    // #1080 — GetEstimatedDuration is read from the run-state / WS publish / checkpoint paths, so
    // it must consult the provider without writing the live tree.
    [TestFixture]
    public class WaitForTimeEstimateTest {

        [Test]
        public void GetEstimatedDuration_consults_the_provider_but_never_assigns_RolloverTime() {
            var rollover = new TimeOnly(12, 0);
            var provider = new Mock<IDateTimeProvider>();
            provider.Setup(p => p.Name).Returns("test");
            provider.Setup(p => p.GetDateTime(It.IsAny<ISequenceEntity>())).Returns(() => DateTime.Now.AddHours(1));
            provider.Setup(p => p.GetRolloverTime(It.IsAny<ISequenceEntity>())).Returns(() => rollover);
            var item = new WaitForTime(new List<IDateTimeProvider> { provider.Object }, provider.Object);
            Assert.That(item.RolloverTime, Is.EqualTo(new TimeOnly(12, 0)), "UpdateTime() (the provider setter) is the writer");

            rollover = new TimeOnly(6, 0); // the provider moves on; an estimate must not copy it in
            provider.Invocations.Clear();
            var estimate = item.GetEstimatedDuration();

            provider.Verify(p => p.GetRolloverTime(It.IsAny<ISequenceEntity>()), Times.AtLeastOnce, "the estimate still uses the provider's rollover");
            Assert.That(item.RolloverTime, Is.EqualTo(new TimeOnly(12, 0)), "an estimate is not a write to the live tree");
            Assert.That(estimate, Is.GreaterThanOrEqualTo(TimeSpan.Zero));
        }
    }
}
