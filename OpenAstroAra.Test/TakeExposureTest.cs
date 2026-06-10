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
using OpenAstroAra.Core.Model.Equipment;
using OpenAstroAra.Sequencer.SequenceItem.Imaging;
using OpenAstroAra.Server.Services;
using OpenAstroAra.Server.Services.Equipment;
using System.Linq;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §14e PRb — sim-free coverage for the re-ported <see cref="TakeExposure"/> instruction: the
    /// NINA-verbatim JSON surface round-trips through the real engine (including the §38k-6
    /// namespace remap that resolves NINA-exported <c>$type</c> strings), clone preserves every
    /// JsonProperty, and Validate gates on the camera mediator. The live capture is exercised by
    /// the camera integration test via <c>IImagingMediator.CaptureImage</c>.
    /// </summary>
    [TestFixture]
    public class TakeExposureTest {

        private static TakeExposure NewPrototype() =>
            new(new HeadlessCameraMediator(), new HeadlessImagingMediator());

        [Test]
        public void Validate_fails_when_camera_not_connected() {
            var item = NewPrototype();
            Assert.That(item.Validate(), Is.False);
            Assert.That(item.Issues, Is.Not.Empty);
        }

        [Test]
        public void Clone_preserves_every_json_property() {
            var item = NewPrototype();
            item.ExposureTime = 120.5;
            item.Gain = 101;
            item.Offset = 30;
            item.Binning = new BinningMode(2, 2);
            item.ImageType = "FLAT";
            item.ExposureCount = 7;

            var clone = (TakeExposure)item.Clone();

            Assert.That(clone.ExposureTime, Is.EqualTo(120.5));
            Assert.That(clone.Gain, Is.EqualTo(101));
            Assert.That(clone.Offset, Is.EqualTo(30));
            Assert.That(clone.Binning.X, Is.EqualTo(2));
            Assert.That(clone.ImageType, Is.EqualTo("FLAT"));
            Assert.That(clone.ExposureCount, Is.EqualTo(7));
        }

        [Test]
        public void GetEstimatedDuration_reports_the_exposure_time() {
            var item = NewPrototype();
            item.ExposureTime = 300;
            Assert.That(item.GetEstimatedDuration().TotalSeconds, Is.EqualTo(300));
        }

        [Test]
        public void Nina_verbatim_type_resolves_through_the_engine_with_property_preservation() {
            // Exactly what a NINA export (or a §38.7 starter template) carries — the upstream
            // namespace + assembly, remapped by §38k-6 to the re-ported instruction.
            var deserializer = new SequenceBodyDeserializer(HeadlessSequencerFactory.WithDefaults());
            var body = System.Text.Json.JsonDocument.Parse("""
                {
                  "schemaVersion": "openastroara-sequence-v1",
                  "$type": "OpenAstroAra.Sequencer.Container.SequentialContainer, OpenAstroAra.Sequencer",
                  "Strategy": {"$type": "OpenAstroAra.Sequencer.Container.ExecutionStrategy.SequentialStrategy, OpenAstroAra.Sequencer"},
                  "Name": "capture test",
                  "Items": {
                    "$type": "System.Collections.ObjectModel.ObservableCollection`1[[OpenAstroAra.Sequencer.SequenceItem.ISequenceItem, OpenAstroAra.Sequencer]], System.ObjectModel",
                    "$values": [
                      {
                        "$type": "NINA.Sequencer.SequenceItem.Imaging.TakeExposure, NINA.Sequencer",
                        "ExposureTime": 42.5,
                        "Gain": 139,
                        "Offset": 21,
                        "Binning": {"$type": "OpenAstroAra.Core.Model.Equipment.BinningMode, OpenAstroAra.Core", "X": 2, "Y": 2},
                        "ImageType": "LIGHT",
                        "ExposureCount": 0,
                        "Parent": null, "ErrorBehavior": 0, "Attempts": 1
                      }
                    ]
                  }
                }
                """).RootElement.Clone();

            var ok = deserializer.TryDeserialize(body, out var container, out var error);
            Assert.That(ok, Is.True, error);
            var exposure = container!.Items.OfType<TakeExposure>().FirstOrDefault();
            Assert.That(exposure, Is.Not.Null, "the NINA-verbatim $type must remap to the re-ported TakeExposure");
            Assert.That(exposure!.ExposureTime, Is.EqualTo(42.5));
            Assert.That(exposure.Gain, Is.EqualTo(139));
            Assert.That(exposure.Offset, Is.EqualTo(21));
            Assert.That(exposure.Binning.X, Is.EqualTo(2));
            Assert.That(exposure.ImageType, Is.EqualTo("LIGHT"));
        }

        [Test]
        public void MapFrameType_covers_the_nina_image_types() {
            Assert.That(CameraService.MapFrameType("LIGHT"), Is.EqualTo(OpenAstroAra.Server.Contracts.FrameType.Light));
            Assert.That(CameraService.MapFrameType("SNAPSHOT"), Is.EqualTo(OpenAstroAra.Server.Contracts.FrameType.Light));
            Assert.That(CameraService.MapFrameType("FLAT"), Is.EqualTo(OpenAstroAra.Server.Contracts.FrameType.Flat));
            Assert.That(CameraService.MapFrameType("DARK"), Is.EqualTo(OpenAstroAra.Server.Contracts.FrameType.Dark));
            Assert.That(CameraService.MapFrameType("DARKFLAT"), Is.EqualTo(OpenAstroAra.Server.Contracts.FrameType.Dark));
            Assert.That(CameraService.MapFrameType("BIAS"), Is.EqualTo(OpenAstroAra.Server.Contracts.FrameType.Bias));
        }
    }
}
