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

        // ─── §35 auto-resume pointing — the active-target walk ───

        [Test]
        public void FindActiveDeepSkyTarget_prefers_the_running_target_over_the_first() {
            var root = new OpenAstroAra.Sequencer.Container.SequentialContainer();
            var first = new DeepSkyObjectContainer(new HeadlessProfileService());
            var running = new DeepSkyObjectContainer(new HeadlessProfileService());
            running.Status = OpenAstroAra.Core.Enums.SequenceEntityStatus.RUNNING;
            var middle = new OpenAstroAra.Sequencer.Container.SequentialContainer();
            middle.Items.Add(running); // nested one level down — the walk must recurse
            root.Items.Add(first);
            root.Items.Add(middle);

            var found = OpenAstroAra.Server.Services.SequencerService.FindActiveDeepSkyTarget(root);

            Assert.That(found, Is.SameAs(running),
                "a multi-target plan re-centers the target the pause interrupted, not the plan's first");
        }

        [Test]
        public void FindActiveDeepSkyTarget_falls_back_to_the_first_when_none_is_running() {
            var root = new OpenAstroAra.Sequencer.Container.SequentialContainer();
            var first = new DeepSkyObjectContainer(new HeadlessProfileService());
            root.Items.Add(first);
            root.Items.Add(new DeepSkyObjectContainer(new HeadlessProfileService()));

            Assert.That(OpenAstroAra.Server.Services.SequencerService.FindActiveDeepSkyTarget(root),
                Is.SameAs(first));
        }

        [Test]
        public void FindActiveDeepSkyTarget_returns_null_for_a_targetless_plan() {
            var root = new OpenAstroAra.Sequencer.Container.SequentialContainer();
            root.Items.Add(new OpenAstroAra.Sequencer.Container.SequentialContainer());

            Assert.That(OpenAstroAra.Server.Services.SequencerService.FindActiveDeepSkyTarget(root), Is.Null);
        }

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
            dso.Target.Expanded = true;

            var clone = (DeepSkyObjectContainer)dso.Clone();
            Assert.That(clone.Target, Is.Not.SameAs(dso.Target));
            Assert.That(clone.Target.TargetName, Is.EqualTo("M31"));
            Assert.That(clone.Target.PositionAngle, Is.EqualTo(33.0));
            Assert.That(clone.Target.Expanded, Is.True, "the full InputTarget serializable state must survive clone");
        }
    }
}
