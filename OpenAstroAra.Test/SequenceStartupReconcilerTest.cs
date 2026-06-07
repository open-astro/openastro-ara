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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.IO;
using System.Linq;

namespace OpenAstroAra.Test {

    [TestFixture]
    public class SequenceStartupReconcilerTest {

        private string _profileDir = string.Empty;

        [SetUp]
        public void SetUp() {
            _profileDir = Path.Combine(Path.GetTempPath(), $"oara-reconcile-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_profileDir);
        }

        [TearDown]
        public void TearDown() {
            try { Directory.Delete(_profileDir, recursive: true); } catch { }
        }

        private static SequenceRunStateDto SampleState() => new(
            SequenceId: Guid.NewGuid(),
            RunId: Guid.NewGuid(),
            State: SequenceRunState.Running,
            CurrentInstructionIndex: 3,
            CurrentTargetName: null,
            StartedUtc: DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedUtc: null,
            FramesCompleted: 3,
            FramesTotal: 10,
            CurrentInstructionDescription: "capture #4");

        [Test]
        public void Reconcile_returns_Clean_when_no_checkpoint() {
            var chk = new ActiveSequenceCheckpoint(_profileDir);
            var sut = new SequenceStartupReconciler(chk);
            var result = sut.Reconcile();
            Assert.That(result.Outcome, Is.EqualTo(SequenceStartupReconciler.Outcome.Clean));
            Assert.That(result.PreviousState, Is.Null);
            Assert.That(result.QuarantinedPath, Is.Null);
        }

        [Test]
        public void Reconcile_returns_Interrupted_and_clears_checkpoint() {
            var chk = new ActiveSequenceCheckpoint(_profileDir);
            var prev = SampleState();
            chk.Write(prev);
            Assert.That(chk.Exists(), Is.True);

            var sut = new SequenceStartupReconciler(chk);
            var result = sut.Reconcile();

            Assert.That(result.Outcome, Is.EqualTo(SequenceStartupReconciler.Outcome.Interrupted));
            Assert.That(result.PreviousState, Is.Not.Null);
            Assert.That(result.PreviousState!.SequenceId, Is.EqualTo(prev.SequenceId));
            // File cleared so next startup is Clean.
            Assert.That(chk.Exists(), Is.False);
        }

        [Test]
        public void Reconcile_quarantines_corrupt_checkpoint() {
            var chk = new ActiveSequenceCheckpoint(_profileDir);
            File.WriteAllText(chk.FilePath, "this is not valid JSON {");

            var sut = new SequenceStartupReconciler(chk);
            var result = sut.Reconcile();

            Assert.That(result.Outcome, Is.EqualTo(SequenceStartupReconciler.Outcome.Corrupt));
            Assert.That(result.QuarantinedPath, Is.Not.Null);
            Assert.That(File.Exists(result.QuarantinedPath!), Is.True);
            // Quarantined name matches the §28.1 pattern.
            Assert.That(Path.GetFileName(result.QuarantinedPath!), Does.StartWith("current.json.corrupt."));
            // Canonical path cleared so next startup is Clean.
            Assert.That(chk.Exists(), Is.False);
        }

        [Test]
        public void Reconcile_is_idempotent_on_clean_state() {
            var chk = new ActiveSequenceCheckpoint(_profileDir);
            var sut = new SequenceStartupReconciler(chk);
            Assert.That(sut.Reconcile().Outcome, Is.EqualTo(SequenceStartupReconciler.Outcome.Clean));
            Assert.That(sut.Reconcile().Outcome, Is.EqualTo(SequenceStartupReconciler.Outcome.Clean));
        }

        [Test]
        public void Reconcile_quarantine_name_has_unix_timestamp_suffix() {
            var chk = new ActiveSequenceCheckpoint(_profileDir);
            File.WriteAllText(chk.FilePath, "garbage");
            var sut = new SequenceStartupReconciler(chk);
            var result = sut.Reconcile();
            // Suffix is a unix ts integer; verify it parses.
            var suffix = Path.GetFileName(result.QuarantinedPath!).Split('.').Last();
            Assert.That(long.TryParse(suffix, out var ts), Is.True);
            Assert.That(ts, Is.GreaterThan(0));
        }
    }
}