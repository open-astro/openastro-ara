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

namespace OpenAstroAra.Test {

    [TestFixture]
    public class ActiveSequenceCheckpointTest {

        private string _profileDir = string.Empty;

        [SetUp]
        public void SetUp() {
            _profileDir = Path.Combine(Path.GetTempPath(), $"oara-active-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_profileDir);
        }

        [TearDown]
        public void TearDown() {
            try { Directory.Delete(_profileDir, recursive: true); } catch { }
        }

        private static SequenceRunStateDto SampleState(Guid id) => new(
            SequenceId: id,
            RunId: Guid.NewGuid(),
            State: SequenceRunState.Running,
            CurrentInstructionIndex: 2,
            CurrentTargetName: null,
            StartedUtc: DateTimeOffset.UtcNow,
            CompletedUtc: null,
            FramesCompleted: 2,
            FramesTotal: 5,
            CurrentInstructionDescription: "capture #3");

        [Test]
        public void Write_creates_active_current_json() {
            var chk = new ActiveSequenceCheckpoint(_profileDir);
            chk.Write(SampleState(Guid.NewGuid()));
            Assert.That(File.Exists(chk.FilePath), Is.True);
        }

        [Test]
        public void TryRead_roundtrips_written_state() {
            var chk = new ActiveSequenceCheckpoint(_profileDir);
            var id = Guid.NewGuid();
            var original = SampleState(id);
            chk.Write(original);

            var roundTripped = chk.TryRead();
            Assert.That(roundTripped, Is.Not.Null);
            Assert.That(roundTripped!.SequenceId, Is.EqualTo(id));
            Assert.That(roundTripped.State, Is.EqualTo(SequenceRunState.Running));
            Assert.That(roundTripped.FramesCompleted, Is.EqualTo(2));
            Assert.That(roundTripped.FramesTotal, Is.EqualTo(5));
        }

        [Test]
        public void TryRead_returns_null_for_missing_file() {
            var chk = new ActiveSequenceCheckpoint(_profileDir);
            Assert.That(chk.TryRead(), Is.Null);
            Assert.That(chk.Exists(), Is.False);
        }

        [Test]
        public void TryRead_returns_null_for_corrupt_file() {
            // §28.1 corruption quarantine renames; that lives in the §28.2
            // reconciler, not this writer. Here we just verify TryRead
            // returns null without throwing on bad JSON.
            var chk = new ActiveSequenceCheckpoint(_profileDir);
            File.WriteAllText(chk.FilePath, "{ this is not valid json");
            Assert.That(chk.TryRead(), Is.Null);
            Assert.That(chk.Exists(), Is.True);  // file still there
        }

        [Test]
        public void Clear_removes_existing_file() {
            var chk = new ActiveSequenceCheckpoint(_profileDir);
            chk.Write(SampleState(Guid.NewGuid()));
            Assert.That(chk.Exists(), Is.True);
            chk.Clear();
            Assert.That(chk.Exists(), Is.False);
        }

        [Test]
        public void Clear_is_idempotent_for_missing_file() {
            var chk = new ActiveSequenceCheckpoint(_profileDir);
            Assert.DoesNotThrow(() => chk.Clear());
        }

        [Test]
        public void Write_uses_atomic_temp_then_rename() {
            // Verify there's no .tmp file lingering after a successful write.
            var chk = new ActiveSequenceCheckpoint(_profileDir);
            chk.Write(SampleState(Guid.NewGuid()));
            Assert.That(File.Exists(chk.FilePath + ".tmp"), Is.False);
        }
    }
}
