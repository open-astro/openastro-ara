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
        public void Factory_registers_the_slice2_prototypes() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            Assert.That(factory.Items.Select(i => i.GetType().Name), Does.Contain("RunAutofocus"));
            Assert.That(factory.Items.Select(i => i.GetType().Name), Does.Contain("CenterAndRotate"));
            Assert.That(factory.Triggers.Select(t => t.GetType().Name), Does.Contain("AutofocusAfterExposures"));
        }
    }
}
