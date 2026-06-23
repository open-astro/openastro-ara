/*
    §38 — DeepSkyObjectContainer clone isolation. Guards the review-found aliasing bug where a
    cloned target shared its (mutable) ExposureInfo entries with the original. Uses the default
    empty target, so no SOFA/NOVAS natives are touched (UpdateHorizonAndTransit early-returns on
    zero coordinates).
*/

using NUnit.Framework;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.Utility;
using OpenAstroAra.Server.Services;
using System.Linq;

namespace OpenAstroAra.Test {

    [TestFixture]
    public class DeepSkyObjectContainerTest {

        [Test]
        public void Clone_does_not_share_mutable_ExposureInfo_entries() {
            var dso = new DeepSkyObjectContainer(new HeadlessProfileService());
            dso.ExposureInfoList.Add(new ExposureInfo("L", 60.0, 100, 10, "LIGHT", 1, 1, 1.0) { Count = 3 });

            var clone = (DeepSkyObjectContainer)dso.Clone();
            Assert.That(clone.ExposureInfoList, Has.Count.EqualTo(1));
            Assert.That(clone.ExposureInfoList[0], Is.Not.SameAs(dso.ExposureInfoList[0]), "clone must hold its own ExposureInfo, not the original's");
            Assert.That(clone.ExposureInfoList[0].Count, Is.EqualTo(3), "the cloned count must be copied across");

            // Mutating the clone's exposure bookkeeping must not bleed into the original.
            clone.ExposureInfoList[0].Increment();
            Assert.That(clone.ExposureInfoList[0].Count, Is.EqualTo(4));
            Assert.That(dso.ExposureInfoList[0].Count, Is.EqualTo(3), "the original's count must be unaffected by the clone");
        }

        [Test]
        public void Clone_copies_target_identity_without_sharing_the_target() {
            var dso = new DeepSkyObjectContainer(new HeadlessProfileService());
            dso.Target.TargetName = "M31";
            dso.Target.PositionAngle = 33.0;

            var clone = (DeepSkyObjectContainer)dso.Clone();
            Assert.That(clone.Target, Is.Not.SameAs(dso.Target));
            Assert.That(clone.Target.TargetName, Is.EqualTo("M31"));
            Assert.That(clone.Target.PositionAngle, Is.EqualTo(33.0));
        }
    }
}
