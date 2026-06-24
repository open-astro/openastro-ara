/*
    §38.4 — NINA import $type normalization. Verifies imported NINA bodies are rewritten to
    canonical OpenAstroAra type names (so the editor + client catalog match) while leaving
    unsupported types — and all node data — untouched.
*/

using NUnit.Framework;
using OpenAstroAra.Server.Services;
using System.Linq;
using System.Text.Json;

namespace OpenAstroAra.Test {

    [TestFixture]
    public class NinaImportTypeNormalizerTest {

        // A NINA export fragment: a SequentialContainer holding a TakeExposure (resolvable) and a
        // made-up unsupported instruction, plus a Smart Exposure block (resolvable). NINA $type
        // strings verbatim, with the $values-wrapped collections NINA emits.
        private const string NinaJson = """
        {
          "$type": "NINA.Sequencer.Container.SequentialContainer, NINA.Sequencer",
          "Strategy": { "$type": "NINA.Sequencer.Container.ExecutionStrategy.SequentialStrategy, NINA.Sequencer" },
          "Name": "Root",
          "Items": {
            "$type": "System.Collections.ObjectModel.ObservableCollection`1[[NINA.Sequencer.ISequenceItem, NINA.Sequencer]], System.ObjectModel",
            "$values": [
              { "$type": "NINA.Sequencer.SequenceItem.Imaging.TakeExposure, NINA.Sequencer", "ExposureTime": 60.0, "Gain": 100, "ImageType": "LIGHT" },
              { "$type": "NINA.Sequencer.SequenceItem.Imaging.SmartExposure, NINA.Sequencer", "Items": { "$values": [] } },
              { "$type": "NINA.Sequencer.SequenceItem.Totally.MadeUp, NINA.Sequencer", "SomeField": 7 }
            ]
          }
        }
        """;

        private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

        private static string? TypeAt(JsonElement root, int itemIndex) =>
            root.GetProperty("Items").GetProperty("$values")[itemIndex].GetProperty("$type").GetString();

        [Test]
        public void Resolvable_types_are_canonicalized_to_OpenAstroAra() {
            var result = NinaImportTypeNormalizer.Normalize(Parse(NinaJson));

            Assert.That(result.Body.GetProperty("$type").GetString(),
                Is.EqualTo("OpenAstroAra.Sequencer.Container.SequentialContainer, OpenAstroAra.Sequencer"));
            Assert.That(TypeAt(result.Body, 0),
                Is.EqualTo("OpenAstroAra.Sequencer.SequenceItem.Imaging.TakeExposure, OpenAstroAra.Sequencer"));
            Assert.That(TypeAt(result.Body, 1),
                Is.EqualTo("OpenAstroAra.Sequencer.SequenceItem.Imaging.SmartExposure, OpenAstroAra.Sequencer"));
        }

        [Test]
        public void Unsupported_types_are_left_untouched_and_reported() {
            var result = NinaImportTypeNormalizer.Normalize(Parse(NinaJson));

            // The made-up type can't resolve, so it stays exactly as NINA wrote it…
            Assert.That(TypeAt(result.Body, 2),
                Is.EqualTo("NINA.Sequencer.SequenceItem.Totally.MadeUp, NINA.Sequencer"));
            // …its data is preserved…
            Assert.That(result.Body.GetProperty("Items").GetProperty("$values")[2].GetProperty("SomeField").GetInt32(),
                Is.EqualTo(7));
            // …and it's reported so the import result can warn the user.
            Assert.That(result.UnsupportedTypes, Has.Count.EqualTo(1));
            Assert.That(result.UnsupportedTypes[0], Is.EqualTo("MadeUp"));
        }

        [Test]
        public void Node_data_survives_normalization() {
            var result = NinaImportTypeNormalizer.Normalize(Parse(NinaJson));
            var takeExposure = result.Body.GetProperty("Items").GetProperty("$values")[0];

            Assert.That(takeExposure.GetProperty("ExposureTime").GetDouble(), Is.EqualTo(60.0));
            Assert.That(takeExposure.GetProperty("Gain").GetInt32(), Is.EqualTo(100));
            Assert.That(takeExposure.GetProperty("ImageType").GetString(), Is.EqualTo("LIGHT"));
            Assert.That(result.Body.GetProperty("Name").GetString(), Is.EqualTo("Root"));
        }
    }
}
