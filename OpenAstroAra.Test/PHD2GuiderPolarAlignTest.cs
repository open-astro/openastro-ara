#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using Newtonsoft.Json;
using NUnit.Framework;
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2.PhdEvents;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §45 (polar-align-a) — the send-site validation that guards the polar-alignment capture RPCs before
    /// they reach the socket (<c>capture_single_frame</c>), plus deserialization of the daemon's
    /// <c>get_star_centroids</c> array, <c>set/get_pa_session</c> status, and the <c>SingleFrameComplete</c>
    /// event. Mirrors the DarkLibrary precedent (<see cref="PHD2GuiderDarkLibraryTest"/>) — no socket.
    /// </summary>
    [TestFixture]
    public class PHD2GuiderPolarAlignTest {

        private static readonly int[] SampleSubframe = { 0, 0, 640, 480 };
        private static readonly int[] MalformedSubframe = { 0, 0, 640 };

        // ── BuildCaptureSolverFrameRequest: send-site validation ──

        [Test]
        public void Capture_request_carries_validated_params_and_saves_to_the_absolute_path() {
            var req = PHD2Guider.BuildCaptureSolverFrameRequest(
                exposureMs: 1500, binning: 2, gain: 60, subframe: null, path: "/tmp/ara-pa/a.fits", save: true);
            Assert.That(req.Method, Is.EqualTo("capture_single_frame"));
            Assert.That(req.Parameters!.ExposureMs, Is.EqualTo(1500));
            Assert.That(req.Parameters!.Binning, Is.EqualTo(2));
            Assert.That(req.Parameters!.Gain, Is.EqualTo(60));
            Assert.That(req.Parameters!.Path, Is.EqualTo("/tmp/ara-pa/a.fits"));
            Assert.That(req.Parameters!.Save, Is.True);
        }

        [Test]
        public void Capture_request_a_path_implies_save_even_when_save_is_false() {
            var req = PHD2Guider.BuildCaptureSolverFrameRequest(1000, null, null, null, "/tmp/ara-pa/b.fits", save: false);
            Assert.That(req.Parameters!.Save, Is.True, "a path implies a save");
            Assert.That(req.Parameters!.Path, Is.EqualTo("/tmp/ara-pa/b.fits"));
        }

        [Test]
        public void Capture_request_without_save_or_path_leaves_both_off_the_wire() {
            // A no-save refresh of the current frame (for a subsequent get_star_centroids) sends neither.
            var req = PHD2Guider.BuildCaptureSolverFrameRequest(1000, null, null, null, path: null, save: false);
            Assert.That(req.Parameters!.Save, Is.Null);
            Assert.That(req.Parameters!.Path, Is.Null);
        }

        [Test]
        public void Capture_request_rejects_a_save_without_a_path() {
            Assert.Throws<ArgumentException>(() =>
                PHD2Guider.BuildCaptureSolverFrameRequest(1000, null, null, null, path: null, save: true));
            Assert.Throws<ArgumentException>(() =>
                PHD2Guider.BuildCaptureSolverFrameRequest(1000, null, null, null, path: "   ", save: true));
        }

        [Test]
        public void Capture_request_rejects_a_relative_save_path() {
            Assert.Throws<ArgumentException>(() =>
                PHD2Guider.BuildCaptureSolverFrameRequest(1000, null, null, null, path: "relative/a.fits", save: true));
        }

        [Test]
        public void Capture_request_rejects_windows_drive_and_root_relative_paths() {
            // IsPathFullyQualified (not IsPathRooted) must reject these — IsPathRooted accepts them on
            // Windows but they are not truly absolute, so the daemon would still reject them.
            Assert.Throws<ArgumentException>(() =>
                PHD2Guider.BuildCaptureSolverFrameRequest(1000, null, null, null, path: @"C:a.fits", save: true),
                "drive-relative path must be rejected");
            Assert.Throws<ArgumentException>(() =>
                PHD2Guider.BuildCaptureSolverFrameRequest(1000, null, null, null, path: @"\a.fits", save: true),
                "root-relative path must be rejected");
        }

        [Test]
        public void Capture_request_rejects_out_of_range_exposure_binning_and_gain() {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                PHD2Guider.BuildCaptureSolverFrameRequest(0, null, null, null, null, false));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                PHD2Guider.BuildCaptureSolverFrameRequest(1000, 0, null, null, null, false));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                PHD2Guider.BuildCaptureSolverFrameRequest(1000, null, -1, null, null, false));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                PHD2Guider.BuildCaptureSolverFrameRequest(1000, null, 101, null, null, false));
        }

        [Test]
        public void Capture_request_accepts_gain_boundaries() {
            Assert.That(PHD2Guider.BuildCaptureSolverFrameRequest(1000, null, 0, null, null, false).Parameters!.Gain, Is.EqualTo(0));
            Assert.That(PHD2Guider.BuildCaptureSolverFrameRequest(1000, null, 100, null, null, false).Parameters!.Gain, Is.EqualTo(100));
        }

        [Test]
        public void Capture_request_rejects_a_malformed_subframe() {
            Assert.Throws<ArgumentException>(() =>
                PHD2Guider.BuildCaptureSolverFrameRequest(1000, null, null, MalformedSubframe, null, false));
        }

        [Test]
        public void Capture_request_accepts_a_four_element_subframe() {
            var req = PHD2Guider.BuildCaptureSolverFrameRequest(1000, null, null, SampleSubframe, null, false);
            Assert.That(req.Parameters!.Subframe, Is.EqualTo(SampleSubframe));
        }

        // ── Response deserialization ──

        [Test]
        public void Centroids_deserialize_from_the_bare_array_result() {
            const string json = """
                {"jsonrpc":"2.0","id":1,"result":[
                  {"x":100.125,"y":200.5,"snr":42.0,"mass":1234.5,"hfd":2.75},
                  {"x":300.0,"y":50.25,"snr":18.3,"mass":600.0,"hfd":3.1}
                ]}
                """;
            var response = JsonConvert.DeserializeObject<Phd2GetStarCentroidsResponse>(json)!;
            Assert.That(response.error, Is.Null);
            Assert.That(response.result, Has.Count.EqualTo(2));
            Assert.That(response.result![0].X, Is.EqualTo(100.125));
            Assert.That(response.result![0].Y, Is.EqualTo(200.5));
            Assert.That(response.result![0].Snr, Is.EqualTo(42.0));
            Assert.That(response.result![0].Mass, Is.EqualTo(1234.5));
            Assert.That(response.result![0].Hfd, Is.EqualTo(2.75));
            Assert.That(response.result![1].X, Is.EqualTo(300.0));
        }

        [Test]
        public void Centroids_deserialize_an_empty_array() {
            const string json = """{"jsonrpc":"2.0","id":1,"result":[]}""";
            var response = JsonConvert.DeserializeObject<Phd2GetStarCentroidsResponse>(json)!;
            Assert.That(response.result, Is.Not.Null.And.Empty);
        }

        [Test]
        public void PaSession_status_deserializes_active_with_remaining_seconds() {
            const string json = """{"jsonrpc":"2.0","id":2,"result":{"active":true,"expires_in_s":540}}""";
            var status = JsonConvert.DeserializeObject<Phd2PaSessionResponse>(json)!.result!;
            Assert.That(status.Active, Is.True);
            Assert.That(status.ExpiresInS, Is.EqualTo(540));
        }

        [Test]
        public void PaSession_status_inactive_omits_the_remaining_seconds() {
            const string json = """{"jsonrpc":"2.0","id":3,"result":{"active":false}}""";
            var status = JsonConvert.DeserializeObject<Phd2PaSessionResponse>(json)!.result!;
            Assert.That(status.Active, Is.False);
            Assert.That(status.ExpiresInS, Is.Null, "no remaining time is reported while the lease is inactive");
        }

        // ── SingleFrameComplete event DTO ──

        [Test]
        public void SingleFrameComplete_event_deserializes_a_saved_success() {
            const string json = """
                {"Event":"SingleFrameComplete","Timestamp":0.0,"Host":"g","Inst":1,"Success":true,"Path":"/tmp/ara-pa/a.fits"}
                """;
            var e = JsonConvert.DeserializeObject<PhdEventSingleFrameComplete>(json)!;
            Assert.That(e.Success, Is.True);
            Assert.That(e.Path, Is.EqualTo("/tmp/ara-pa/a.fits"));
            Assert.That(e.Error, Is.Null);
        }

        [Test]
        public void SingleFrameComplete_event_deserializes_a_failure_with_no_path() {
            const string json = """
                {"Event":"SingleFrameComplete","Timestamp":0.0,"Host":"g","Inst":1,"Success":false,"Error":"camera not connected"}
                """;
            var e = JsonConvert.DeserializeObject<PhdEventSingleFrameComplete>(json)!;
            Assert.That(e.Success, Is.False);
            Assert.That(e.Error, Is.EqualTo("camera not connected"));
            Assert.That(e.Path, Is.Null, "no path when the frame was not saved");
        }
    }
}
