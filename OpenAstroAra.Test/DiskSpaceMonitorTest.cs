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

    /// <summary>§29 — pure logic of the disk-space monitor (level thresholds + path→volume resolution).</summary>
    [TestFixture]
    public class DiskSpaceMonitorTest {

        private const long Gib = 1024L * 1024 * 1024;
        private static readonly string[] UnixRoots = { "/", "/media/openastroara" };
        private static readonly string[] DedicatedMountOnly = { "/media/openastroara" };
        private static readonly string[] WindowsRoots = { @"C:\", @"D:\" };
        private static readonly string[] PrefixCollisionRoots = { "/mnt/data", "/mnt/data2" };

        [Test]
        public void Evaluate_maps_free_space_to_levels_with_inclusive_low_boundaries() {
            const long low = 10 * Gib;
            const long crit = 2 * Gib;
            Assert.That(DiskSpaceMonitor.Evaluate(50 * Gib, low, crit), Is.EqualTo(DiskSpaceLevel.Ok));
            Assert.That(DiskSpaceMonitor.Evaluate(low + 1, low, crit), Is.EqualTo(DiskSpaceLevel.Ok));
            Assert.That(DiskSpaceMonitor.Evaluate(low, low, crit), Is.EqualTo(DiskSpaceLevel.Low), "≤ low trips Low");
            Assert.That(DiskSpaceMonitor.Evaluate(5 * Gib, low, crit), Is.EqualTo(DiskSpaceLevel.Low));
            Assert.That(DiskSpaceMonitor.Evaluate(crit + 1, low, crit), Is.EqualTo(DiskSpaceLevel.Low));
            Assert.That(DiskSpaceMonitor.Evaluate(crit, low, crit), Is.EqualTo(DiskSpaceLevel.Critical), "≤ critical trips Critical");
            Assert.That(DiskSpaceMonitor.Evaluate(0, low, crit), Is.EqualTo(DiskSpaceLevel.Critical));
        }

        [Test]
        public void ResolveThresholdBytes_converts_valid_gib_pairs() {
            var (low, crit) = DiskSpaceMonitor.ResolveThresholdBytes(warnGb: 20, criticalGb: 5);
            Assert.That(low, Is.EqualTo(20 * Gib));
            Assert.That(crit, Is.EqualTo(5 * Gib));
        }

        [Test]
        public void ResolveThresholdBytes_falls_back_to_defaults_on_a_bad_pair() {
            var defaults = (DiskSpaceMonitor.DefaultLowBytes, DiskSpaceMonitor.DefaultCriticalBytes);
            // Inverted (critical >= warn) — Evaluate would otherwise read Critical for almost anything.
            Assert.That(DiskSpaceMonitor.ResolveThresholdBytes(2, 10), Is.EqualTo(defaults));
            // Equal is also rejected (warn must be strictly above critical).
            Assert.That(DiskSpaceMonitor.ResolveThresholdBytes(5, 5), Is.EqualTo(defaults));
            // Non-positive critical.
            Assert.That(DiskSpaceMonitor.ResolveThresholdBytes(10, 0), Is.EqualTo(defaults));
            Assert.That(DiskSpaceMonitor.ResolveThresholdBytes(10, -1), Is.EqualTo(defaults));
            // Non-positive warn (even with a "valid"-looking critical).
            Assert.That(DiskSpaceMonitor.ResolveThresholdBytes(0, 1), Is.EqualTo(defaults));
        }

        [Test]
        public void ShouldNotify_only_on_a_strictly_worse_transition() {
            // New alert when it degrades...
            Assert.That(DiskSpaceMonitor.ShouldNotify(DiskSpaceLevel.Ok, DiskSpaceLevel.Low), Is.True);
            Assert.That(DiskSpaceMonitor.ShouldNotify(DiskSpaceLevel.Low, DiskSpaceLevel.Critical), Is.True);
            Assert.That(DiskSpaceMonitor.ShouldNotify(DiskSpaceLevel.Ok, DiskSpaceLevel.Critical), Is.True);
            // ...but not on an improvement or recovery (the diagnostic still updates; the inbox doesn't pile up).
            Assert.That(DiskSpaceMonitor.ShouldNotify(DiskSpaceLevel.Critical, DiskSpaceLevel.Low), Is.False);
            Assert.That(DiskSpaceMonitor.ShouldNotify(DiskSpaceLevel.Low, DiskSpaceLevel.Ok), Is.False);
            Assert.That(DiskSpaceMonitor.ShouldNotify(DiskSpaceLevel.Critical, DiskSpaceLevel.Ok), Is.False);
        }

        [Test]
        public void ShouldAbortSequence_only_for_the_abort_policy() {
            Assert.That(DiskSpaceMonitor.ShouldAbortSequence("abort"), Is.True);
            Assert.That(DiskSpaceMonitor.ShouldAbortSequence("  Abort "), Is.True, "trimmed + case-insensitive");
            // The default and anything unrecognised mean warn-only (safe).
            Assert.That(DiskSpaceMonitor.ShouldAbortSequence("warn"), Is.False);
            Assert.That(DiskSpaceMonitor.ShouldAbortSequence("pause"), Is.False);
            Assert.That(DiskSpaceMonitor.ShouldAbortSequence(""), Is.False);
            Assert.That(DiskSpaceMonitor.ShouldAbortSequence(null), Is.False);
        }

        [Test]
        public void LongestPrefixRoot_picks_the_most_specific_mount() {
            // A dedicated /media mount wins over the / root for a path under it.
            Assert.That(
                DiskSpaceMonitor.LongestPrefixRoot("/media/openastroara/M42/x.fits", UnixRoots),
                Is.EqualTo("/media/openastroara"));
            // A path not under the dedicated mount falls back to /.
            Assert.That(
                DiskSpaceMonitor.LongestPrefixRoot("/home/astro/x.fits", UnixRoots),
                Is.EqualTo("/"));
        }

        [Test]
        public void LongestPrefixRoot_returns_null_when_no_root_matches() {
            Assert.That(
                DiskSpaceMonitor.LongestPrefixRoot("/mnt/other/x.fits", DedicatedMountOnly),
                Is.Null);
        }

        [Test]
        public void LongestPrefixRoot_respects_path_component_boundaries() {
            // "/mnt/data2/..." must NOT match the shorter "/mnt/data" mount (string-prefix but not a path prefix).
            Assert.That(
                DiskSpaceMonitor.LongestPrefixRoot("/mnt/data2/images/x.fits", PrefixCollisionRoots),
                Is.EqualTo("/mnt/data2"));
            Assert.That(
                DiskSpaceMonitor.LongestPrefixRoot("/mnt/data/images/x.fits", PrefixCollisionRoots),
                Is.EqualTo("/mnt/data"));
        }

        [Test]
        public void LongestPrefixRoot_handles_windows_volume_roots() {
            Assert.That(
                DiskSpaceMonitor.LongestPrefixRoot(@"D:\Astro\M42\x.fits", WindowsRoots),
                Is.EqualTo(@"D:\"));
        }

        [Test]
        public void LongestPrefixRoot_can_match_case_insensitively_for_windows_drive_letters() {
            // A lowercase-drive save dir ("d:\...") must still match DriveInfo's uppercase "D:\" root when the
            // caller opts into OrdinalIgnoreCase (Windows); the default Ordinal would miss it.
            Assert.That(
                DiskSpaceMonitor.LongestPrefixRoot(@"d:\Astro\x.fits", WindowsRoots),
                Is.Null, "case-sensitive default does not match a lowercase drive letter");
            Assert.That(
                DiskSpaceMonitor.LongestPrefixRoot(@"d:\Astro\x.fits", WindowsRoots, System.StringComparison.OrdinalIgnoreCase),
                Is.EqualTo(@"D:\"));
        }
    }
}
