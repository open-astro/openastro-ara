#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §45 — wire-shape coverage for the polar-alignment RPC requests (the surface ARA's PA
    /// routine drives per openastro-guider design/POLAR_ALIGNMENT_DESIGN.md §8): the extended
    /// <c>capture_single_frame</c> form, <c>get_star_centroids</c>, and the PA session lease.
    /// Same serialization-locked pattern as the dark-library methods.
    /// </summary>
    [TestFixture]
    public class Phd2PolarAlignMethodsTest {

        private static JObject Serialize(Phd2Method msg) =>
            JObject.Parse(JsonConvert.SerializeObject(msg));

        private static readonly int[] SampleRoi = { 10, 20, 640, 480 };

        [Test]
        public void CaptureSolverFrame_serializes_the_forks_full_parameter_surface() {
            var json = Serialize(new Phd2CaptureSolverFrame {
                Parameters = new() {
                    ExposureMs = 1500, Binning = 2, Gain = 60,
                    Path = "/tmp/ara-pa/frame-a.fits", Save = true,
                },
            });
            Assert.That(json["method"]!.Value<string>(), Is.EqualTo("capture_single_frame"));
            var p = json["params"]!;
            Assert.That(p["exposure"]!.Value<int>(), Is.EqualTo(1500), "the daemon reads MILLISECONDS");
            Assert.That(p["binning"]!.Value<int>(), Is.EqualTo(2));
            Assert.That(p["gain"]!.Value<int>(), Is.EqualTo(60));
            Assert.That(p["path"]!.Value<string>(), Is.EqualTo("/tmp/ara-pa/frame-a.fits"));
            Assert.That(p["save"]!.Value<bool>(), Is.True);
            Assert.That(p["subframe"], Is.Null, "unset optionals stay off the wire");
        }

        [Test]
        public void CaptureSolverFrame_with_nothing_set_sends_an_empty_params_object() {
            // Every field inherits the daemon's current camera settings when omitted.
            var json = Serialize(new Phd2CaptureSolverFrame { Parameters = new() });
            Assert.That(((JObject)json["params"]!).Count, Is.EqualTo(0));
        }

        [Test]
        public void GetStarCentroids_serializes_roi_and_max_stars() {
            var json = Serialize(new Phd2GetStarCentroids {
                Parameters = new() { Roi = SampleRoi, MaxStars = 25 },
            });
            Assert.That(json["method"]!.Value<string>(), Is.EqualTo("get_star_centroids"));
            var p = json["params"]!;
            Assert.That(p["roi"]!.ToObject<int[]>(), Is.EqualTo(SampleRoi),
                "[x, y, width, height], the daemon's parse_rect order");
            Assert.That(p["max_stars"]!.Value<int>(), Is.EqualTo(25));
        }

        [Test]
        public void SetPaSession_serializes_the_lease_start_and_end_forms() {
            var start = Serialize(new Phd2SetPaSession {
                Parameters = new() { Active = true, TimeoutS = 300 },
            });
            Assert.That(start["method"]!.Value<string>(), Is.EqualTo("set_pa_session"));
            Assert.That(start["params"]!["active"]!.Value<bool>(), Is.True);
            Assert.That(start["params"]!["timeout_s"]!.Value<int>(), Is.EqualTo(300));

            var end = Serialize(new Phd2SetPaSession { Parameters = new() { Active = false } });
            Assert.That(end["params"]!["active"]!.Value<bool>(), Is.False);
            Assert.That(end["params"]!["timeout_s"], Is.Null,
                "ending the lease needs no timeout — it stays off the wire");
        }

        [Test]
        public void GetPaSession_is_a_parameterless_method() {
            var json = Serialize(new Phd2GetPaSession());
            Assert.That(json["method"]!.Value<string>(), Is.EqualTo("get_pa_session"));
        }
    }
}
