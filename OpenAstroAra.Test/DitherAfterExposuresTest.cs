/*
    §38 — DitherAfterExposures cadence + reset behavior. Guards the review-found bug where the
    internal exposure tally carried across a sequence reset/replay and fired the dither off-cadence.
*/

using NUnit.Framework;
using OpenAstroAra.Sequencer.SequenceItem.Imaging;
using OpenAstroAra.Sequencer.Trigger.Guider;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Test {

    [TestFixture]
    public class DitherAfterExposuresTest {

        // A real exposure instruction (IExposureItem) from the headless factory — what the trigger
        // counts. No equipment/natives needed: the prototype is never executed here.
        private static TakeExposure Exposure() =>
            HeadlessSequencerFactory.WithDefaults().GetItem<TakeExposure>()!;

        [Test]
        public void Fires_every_Nth_exposure() {
            var trigger = new DitherAfterExposures { AfterExposures = 3 };
            var exp = Exposure();

            Assert.That(trigger.ShouldTrigger(exp, null), Is.False); // 1
            Assert.That(trigger.ShouldTrigger(exp, null), Is.False); // 2
            Assert.That(trigger.ShouldTrigger(exp, null), Is.True);  // 3 → fire
            Assert.That(trigger.ShouldTrigger(exp, null), Is.False); // 4
        }

        [Test]
        public void Non_exposure_items_do_not_advance_the_tally() {
            var trigger = new DitherAfterExposures { AfterExposures = 1 };
            // A SmartExposure container is not itself an IExposureItem, so it must not count.
            Assert.That(trigger.ShouldTrigger(new SmartExposure(), null), Is.False);
        }

        [Test]
        public void SequenceBlockInitialize_resets_the_tally_for_a_replay() {
            var trigger = new DitherAfterExposures { AfterExposures = 5 };
            var exp = Exposure();

            // Simulate a first run of 12 exposures.
            for (var i = 0; i < 12; i++) {
                trigger.ShouldTrigger(exp, null);
            }

            // Reset + replay: the next exposure must be #1, not #13 — so it must NOT fire until #5.
            trigger.SequenceBlockInitialize();
            Assert.That(trigger.ShouldTrigger(exp, null), Is.False, "exposure 1 of the replay must not dither");
            for (var i = 0; i < 3; i++) {
                Assert.That(trigger.ShouldTrigger(exp, null), Is.False); // 2,3,4
            }
            Assert.That(trigger.ShouldTrigger(exp, null), Is.True, "the dither must fire on exposure 5 of the replay");
        }
    }
}
