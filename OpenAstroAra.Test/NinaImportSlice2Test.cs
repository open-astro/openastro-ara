/*
    §38 NINA import fidelity (slice 2) — RunAutofocus, CenterAndRotate and the AutofocusAfterExposures
    trigger now resolve to their ported types (not Unknown*) on import, with CenterAndRotate's
    coordinates / position-angle / inherited flag preserved.
*/

using NUnit.Framework;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.SequenceItem.Autofocus;
using OpenAstroAra.Sequencer.SequenceItem.Platesolving;
using OpenAstroAra.Sequencer.Serialization;
using OpenAstroAra.Sequencer.Trigger.Autofocus;
using OpenAstroAra.Server.Services;
using System.Linq;

namespace OpenAstroAra.Test {

    [TestFixture]
    public class NinaImportSlice2Test {

        private const string NinaJson = """
        {
          "$type": "NINA.Sequencer.Container.SequentialContainer, NINA.Sequencer",
          "Strategy": { "$type": "NINA.Sequencer.Container.ExecutionStrategy.SequentialStrategy, NINA.Sequencer" },
          "Name": "Root",
          "Conditions": [],
          "Items": [
            { "$type": "NINA.Sequencer.SequenceItem.Autofocus.RunAutofocus, NINA.Sequencer" },
            {
              "$type": "NINA.Sequencer.SequenceItem.Platesolving.CenterAndRotate, NINA.Sequencer",
              "PositionAngle": 137.5,
              "Inherited": true,
              "Coordinates": {
                "$type": "NINA.Astrometry.InputCoordinates, NINA.Astrometry",
                "RAHours": 0, "RAMinutes": 42, "RASeconds": 44.0,
                "NegativeDec": false, "DecDegrees": 41, "DecMinutes": 16, "DecSeconds": 9.0
              }
            }
          ],
          "Triggers": [
            {
              "$type": "NINA.Sequencer.Trigger.Autofocus.AutofocusAfterExposures, NINA.Sequencer",
              "AfterExposures": 10,
              "TriggerRunner": {
                "$type": "NINA.Sequencer.Container.SequentialContainer, NINA.Sequencer",
                "Strategy": { "$type": "NINA.Sequencer.Container.ExecutionStrategy.SequentialStrategy, NINA.Sequencer" },
                "Items": [], "Conditions": [], "Triggers": []
              }
            }
          ]
        }
        """;

        private static ISequenceContainer Deserialize() {
            var converter = new SequenceJsonConverter(HeadlessSequencerFactory.WithDefaults());
            return converter.Deserialize(NinaJson);
        }

        [Test]
        public void RunAutofocus_resolves() {
            var root = Deserialize();
            Assert.That(root.Items.OfType<RunAutofocus>().SingleOrDefault(), Is.Not.Null,
                "RunAutofocus must resolve to its ported type, not UnknownSequenceItem");
        }

        [Test]
        public void CenterAndRotate_resolves_with_fields() {
            var car = Deserialize().Items.OfType<CenterAndRotate>().SingleOrDefault();
            Assert.That(car, Is.Not.Null, "CenterAndRotate must resolve, not UnknownSequenceItem");
            Assert.That(car!.PositionAngle, Is.EqualTo(137.5), "the rotation angle must survive import");
            Assert.That(car.Inherited, Is.True);
            // 0h42m44s → 10.68°, +41°16'09"
            Assert.That(car.Coordinates.Coordinates.RADegrees, Is.EqualTo(10.683).Within(0.01));
            Assert.That(car.Coordinates.Coordinates.Dec, Is.EqualTo(41.269).Within(0.01));
        }

        [Test]
        public void AutofocusAfterExposures_trigger_resolves_with_cadence() {
            var trigger = ((SequenceContainer)Deserialize()).Triggers.OfType<AutofocusAfterExposures>().SingleOrDefault();
            Assert.That(trigger, Is.Not.Null, "the trigger must resolve, not UnknownSequenceTrigger");
            Assert.That(trigger!.AfterExposures, Is.EqualTo(10));
        }

        [Test]
        public void AutofocusAfterExposures_fires_every_Nth_exposure_and_resets_on_replay() {
            var exposure = HeadlessSequencerFactory.WithDefaults()
                .GetItem<OpenAstroAra.Sequencer.SequenceItem.Imaging.TakeExposure>()!;
            var trigger = new AutofocusAfterExposures { AfterExposures = 3 };

            Assert.That(trigger.ShouldTrigger(exposure, null), Is.False); // 1
            Assert.That(trigger.ShouldTrigger(exposure, null), Is.False); // 2
            Assert.That(trigger.ShouldTrigger(exposure, null), Is.True);  // 3 → fire
            // A non-exposure item must not advance the tally (and must not re-fire).
            Assert.That(trigger.ShouldTrigger(new RunAutofocus(), null), Is.False);

            // Reset + replay: the next exposure is #1 again, not #4.
            trigger.SequenceBlockInitialize();
            Assert.That(trigger.ShouldTrigger(exposure, null), Is.False);
            Assert.That(trigger.ShouldTrigger(exposure, null), Is.False);
            Assert.That(trigger.ShouldTrigger(exposure, null), Is.True, "must fire on exposure 3 of the replay");
        }

        [Test]
        public void AutofocusAfterExposures_never_fires_when_AfterExposures_is_zero() {
            var exposure = HeadlessSequencerFactory.WithDefaults()
                .GetItem<OpenAstroAra.Sequencer.SequenceItem.Imaging.TakeExposure>()!;
            var trigger = new AutofocusAfterExposures { AfterExposures = 0 };
            Assert.That(trigger.ShouldTrigger(exposure, null), Is.False, "AfterExposures<=0 must be a safe no-op (no divide-by-zero)");
        }

        [Test]
        public void Factory_registers_the_slice2_prototypes() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            Assert.That(factory.Items.Select(i => i.GetType().Name), Does.Contain("RunAutofocus"));
            Assert.That(factory.Items.Select(i => i.GetType().Name), Does.Contain("CenterAndRotate"));
            Assert.That(factory.Triggers.Select(t => t.GetType().Name), Does.Contain("AutofocusAfterExposures"));
        }
    }
}
