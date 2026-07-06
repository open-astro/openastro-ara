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
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §29 — the pre-capture gate's WIRING in <see cref="CameraService"/> (the pure decision is
    /// covered in <c>DiskSpaceMonitorTest</c>): <c>CaptureCoreAsync</c> consults exactly this
    /// method before the shutter opens. The "critically low" condition is manufactured with an
    /// absurdly high critical threshold (an exabyte-class GiB figure) so the REAL volume under
    /// the save dir reads as critical on any machine — no fake drives needed.
    /// </summary>
    [TestFixture]
    public class CameraServicePreCaptureDiskTest {

        private static InMemoryProfileStore StoreWith(int warnGb, int criticalGb, string policy,
                string saveDir = "/media/openastroara") {
            var store = new InMemoryProfileStore();
            store.PutStorageSettings(store.GetStorageSettings() with {
                SaveDirectory = saveDir,
                MinFreeDiskWarnGb = warnGb,
                MinFreeDiskCriticalGb = criticalGb,
            });
            store.PutSafetyPolicies(store.GetSafetyPolicies() with { OnDiskSpaceCritical = policy });
            return store;
        }

        [Test]
        public void Without_a_profile_store_the_gate_never_blocks() {
            using var svc = new CameraService();
            Assert.That(svc.PreCaptureDiskBlocked(out _), Is.False);
        }

        [Test]
        public void An_empty_save_directory_never_blocks() {
            using var svc = new CameraService(
                profileStore: StoreWith(2_000_000, 1_000_000, "abort", saveDir: ""));
            Assert.That(svc.PreCaptureDiskBlocked(out _), Is.False,
                "fallback-dir captures are dev-box territory — the monitor doesn't watch it either");
        }

        [Test]
        public void A_critical_volume_blocks_under_abort_and_reports_the_free_bytes() {
            // ~1 EiB critical threshold: every real volume reads critical.
            using var svc = new CameraService(
                profileStore: StoreWith(2_000_000, 1_000_000, "abort"));
            Assert.That(svc.PreCaptureDiskBlocked(out var freeBytes), Is.True);
            Assert.That(freeBytes, Is.GreaterThan(0), "the log figure carries the actual free space");
        }

        [Test]
        public void The_warn_default_never_blocks_even_when_critical() {
            using var svc = new CameraService(
                profileStore: StoreWith(2_000_000, 1_000_000, "warn"));
            Assert.That(svc.PreCaptureDiskBlocked(out _), Is.False,
                "warn-only posture: the monitor's notification path owns reporting");
        }

        [Test]
        public void A_healthy_volume_never_blocks_under_abort() {
            // Sane 10/2 GiB thresholds: any machine able to run this suite has more than
            // 2 GiB free on the volume the temp save dir resolves to.
            using var svc = new CameraService(
                profileStore: StoreWith(10, 2, "abort", saveDir: System.IO.Path.GetTempPath()));
            Assert.That(svc.PreCaptureDiskBlocked(out _), Is.False);
        }
    }
}
