/*
    §38 NINA import fidelity — verifies that the high-frequency NINA Advanced Sequencer
    container/trigger types real exports are built from (DeepSkyObjectContainer,
    SmartExposure, DitherAfterExposures) resolve to their first-class ported types
    instead of degrading to Unknown* placeholders, which previously gutted the bulk of
    every imported plan. The fixture mirrors the exact `$type`/structure NINA emits
    (a per-target DSO holding a Smart Exposure with a filter switch, a loop count and a
    dither-after-exposures trigger), so a regression that drops a type fails here.
*/

using NUnit.Framework;
using OpenAstroAra.Astrometry;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.SequenceItem.Imaging;
using OpenAstroAra.Sequencer.Serialization;
using OpenAstroAra.Sequencer.Trigger.Guider;
using OpenAstroAra.Server.Services;
using System;
using System.Linq;

namespace OpenAstroAra.Test {

    [TestFixture]
    public class NinaImportFidelityTest {

        [OneTimeSetUp]
        public void RequireAstrometryNatives() {
            // Deserializing an InputTarget with real coordinates eagerly computes the target's
            // horizon/transit via the SOFA/NOVAS natives (§14e). The SequenceJsonConverter swallows
            // a native-load failure and degrades the target to UnknownSequenceContainer, so probe the
            // natives up front and self-ignore when they're absent (a dev box that hasn't run
            // scripts/build-astrometry-natives.sh) — the analyzer-gate CI job stages them and runs
            // this fixture for real, exactly like AstrometryNativeTransformTest.
            try {
                _ = new Coordinates(Angle.ByHours(6.0), Angle.ByDegree(45.0), Epoch.J2000).Transform(Epoch.JNOW);
            } catch (DllNotFoundException) {
                Assert.Ignore("SOFA/NOVAS natives not present — run scripts/build-astrometry-natives.sh into the test bin dir to exercise NINA import.");
            }
        }

        // A minimal but structurally faithful NINA export: SequentialContainer root →
        // DeepSkyObjectContainer "T Cas" → SmartExposure { SwitchFilter, LoopCondition×3,
        // DitherAfterExposures(5) }. Uses NINA's `$type`/assembly strings verbatim so the
        // NinaTypeRemapper (NINA.* → OpenAstroAra.*) is exercised exactly as on real import.
        private const string NinaSequenceJson = """
        {
          "$type": "NINA.Sequencer.Container.SequentialContainer, NINA.Sequencer",
          "Strategy": { "$type": "NINA.Sequencer.Container.ExecutionStrategy.SequentialStrategy, NINA.Sequencer" },
          "Name": "Root",
          "Conditions": [],
          "Items": [
            {
              "$type": "NINA.Sequencer.Container.DeepSkyObjectContainer, NINA.Sequencer",
              "Strategy": { "$type": "NINA.Sequencer.Container.ExecutionStrategy.SequentialStrategy, NINA.Sequencer" },
              "Target": {
                "$type": "NINA.Astrometry.InputTarget, NINA.Astrometry",
                "TargetName": "T Cas",
                "PositionAngle": 12.5,
                "InputCoordinates": {
                  "$type": "NINA.Astrometry.InputCoordinates, NINA.Astrometry",
                  "RAHours": 0, "RAMinutes": 23, "RASeconds": 14.0,
                  "NegativeDec": false, "DecDegrees": 55, "DecMinutes": 47, "DecSeconds": 33.0
                }
              },
              "ExposureInfoList": [],
              "Conditions": [],
              "Items": [
                {
                  "$type": "NINA.Sequencer.SequenceItem.Imaging.SmartExposure, NINA.Sequencer",
                  "Strategy": { "$type": "NINA.Sequencer.Container.ExecutionStrategy.SequentialStrategy, NINA.Sequencer" },
                  "Conditions": [
                    { "$type": "NINA.Sequencer.Conditions.LoopCondition, NINA.Sequencer", "Iterations": 3, "CompletedIterations": 0 }
                  ],
                  "Items": [
                    { "$type": "NINA.Sequencer.SequenceItem.FilterWheel.SwitchFilter, NINA.Sequencer" },
                    { "$type": "NINA.Sequencer.SequenceItem.Imaging.TakeExposure, NINA.Sequencer", "ExposureTime": 60.0, "Gain": 100, "Offset": 10, "Binning": { "$type": "NINA.Core.Model.Equipment.BinningMode, NINA.Core", "X": 1, "Y": 1 }, "ImageType": "LIGHT", "ExposureCount": 0 }
                  ],
                  "Triggers": [
                    {
                      "$type": "NINA.Sequencer.Trigger.Guider.DitherAfterExposures, NINA.Sequencer",
                      "AfterExposures": 5,
                      "TriggerRunner": {
                        "$type": "NINA.Sequencer.Container.SequentialContainer, NINA.Sequencer",
                        "Strategy": { "$type": "NINA.Sequencer.Container.ExecutionStrategy.SequentialStrategy, NINA.Sequencer" },
                        "Items": [], "Conditions": [], "Triggers": []
                      }
                    }
                  ]
                }
              ],
              "Triggers": []
            }
          ],
          "Triggers": []
        }
        """;

        private static ISequenceContainer Deserialize(string json) {
            var factory = HeadlessSequencerFactory.WithDefaults();
            var converter = new SequenceJsonConverter(factory);
            return converter.Deserialize(json);
        }

        [Test]
        public void DeepSkyObjectContainer_resolves_with_target_identity() {
            var root = Deserialize(NinaSequenceJson);

            Assert.That(root, Is.Not.Null);
            var dso = root.Items.OfType<DeepSkyObjectContainer>().SingleOrDefault();
            Assert.That(dso, Is.Not.Null, "the DeepSkyObjectContainer must resolve to its ported type, not UnknownSequenceContainer");
            Assert.That(dso!.Target, Is.Not.Null);
            Assert.That(dso.Target.TargetName, Is.EqualTo("T Cas"), "the target name must survive import");
            Assert.That(dso.Target.PositionAngle, Is.EqualTo(12.5), "the framing rotation must survive import");
            // 0h23m14s → 5.808°, +55°47'33"
            Assert.That(dso.Target.InputCoordinates.Coordinates.RADegrees, Is.EqualTo(5.808).Within(0.01));
            Assert.That(dso.Target.InputCoordinates.Coordinates.Dec, Is.EqualTo(55.792).Within(0.01));
        }

        [Test]
        public void SmartExposure_resolves_with_children_intact() {
            var root = Deserialize(NinaSequenceJson);
            var dso = root.Items.OfType<DeepSkyObjectContainer>().Single();

            var smart = dso.Items.OfType<SmartExposure>().SingleOrDefault();
            Assert.That(smart, Is.Not.Null, "the SmartExposure must resolve to its ported type, not UnknownSequenceContainer");
            Assert.That(smart!.Items, Has.Count.EqualTo(2), "the embedded SwitchFilter + TakeExposure must survive import");
            Assert.That(smart.Items[0].GetType().Name, Is.EqualTo("SwitchFilter"));
            Assert.That(smart.Items[1].GetType().Name, Is.EqualTo("TakeExposure"));
            Assert.That(smart.Conditions.Any(c => c.GetType().Name == "LoopCondition"), Is.True, "the exposure-count loop must survive import");
        }

        [Test]
        public void Imported_dither_fires_on_the_imported_exposure() {
            // End-to-end: the TakeExposure deserialized inside the SmartExposure is a real
            // IExposureItem, so the imported DitherAfterExposures (AfterExposures=5) actually
            // reaches its cadence when fed that exposure — i.e. dithering survives import, not
            // just type resolution.
            var smart = Deserialize(NinaSequenceJson)
                .Items.OfType<DeepSkyObjectContainer>().Single()
                .Items.OfType<SmartExposure>().Single();
            var exposure = smart.Items.OfType<OpenAstroAra.Sequencer.Interfaces.IExposureItem>().Single();
            var dither = smart.Triggers.OfType<DitherAfterExposures>().Single();

            for (var i = 1; i < dither.AfterExposures; i++) {
                Assert.That(dither.ShouldTrigger(exposure, null), Is.False, $"must not dither on imported exposure {i}");
            }
            Assert.That(dither.ShouldTrigger(exposure, null), Is.True, "the imported dither must fire on its Nth imported exposure");
        }

        [Test]
        public void DitherAfterExposures_trigger_resolves_with_cadence() {
            var root = Deserialize(NinaSequenceJson);
            var smart = root.Items.OfType<DeepSkyObjectContainer>().Single()
                            .Items.OfType<SmartExposure>().Single();

            var dither = smart.Triggers.OfType<DitherAfterExposures>().SingleOrDefault();
            Assert.That(dither, Is.Not.Null, "the DitherAfterExposures trigger must resolve to its ported type, not UnknownSequenceTrigger");
            Assert.That(dither!.AfterExposures, Is.EqualTo(5), "the dither cadence must survive import");
        }

        [Test]
        public void Imported_tree_round_trips_without_losing_types() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            var converter = new SequenceJsonConverter(factory);

            var first = converter.Deserialize(NinaSequenceJson);
            var json = converter.Serialize(first);
            var second = converter.Deserialize(json);

            // Re-serializing then re-parsing keeps every ported type — no degradation to Unknown*.
            var dso = second.Items.OfType<DeepSkyObjectContainer>().Single();
            Assert.That(dso.Target.TargetName, Is.EqualTo("T Cas"));
            var smart = dso.Items.OfType<SmartExposure>().Single();
            Assert.That(smart.Triggers.OfType<DitherAfterExposures>().Single().AfterExposures, Is.EqualTo(5));
        }
    }
}
