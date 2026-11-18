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

namespace OpenAstroAra.Test {

    [TestFixture]
    public class StartupNotificationFactoryTest {

        private static SequenceRunStateDto SampleState(Guid? id = null) => new(
            SequenceId: id ?? Guid.NewGuid(),
            RunId: Guid.NewGuid(),
            State: SequenceRunState.Running,
            CurrentInstructionIndex: 4,
            CurrentTargetName: null,
            StartedUtc: DateTimeOffset.UtcNow.AddMinutes(-10),
            CompletedUtc: null,
            InstructionsCompleted: 4,
            InstructionsTotal: 10,
            CurrentInstructionDescription: null);

        [Test]
        public void ForProfileSetAsides_yields_one_Critical_notice_per_file_naming_id_and_path() {
            var a = new ProfileSetAside(Guid.NewGuid(), "/tmp/p/a.json", "/tmp/p/a.json.corrupt.1");
            var b = new ProfileSetAside(Guid.NewGuid(), "/tmp/p/b.json", "/tmp/p/b.json.corrupt.2");

            var notes = StartupNotificationFactory.ForProfileSetAsides(new[] { a, b });

            Assert.That(notes, Has.Count.EqualTo(2));
            Assert.That(notes[0].Severity, Is.EqualTo(NotificationSeverity.Critical));
            Assert.That(notes[0].Category, Is.EqualTo(NotificationCategory.Software));
            Assert.That(notes[0].Message, Does.Contain(a.Id.ToString("N")).And.Contain(a.SetAsidePath));
            Assert.That(notes[1].Message, Does.Contain(b.Id.ToString("N")).And.Contain(b.SetAsidePath));
            Assert.That(notes[0].Id, Is.Not.EqualTo(notes[1].Id));
        }

        [Test]
        public void ForProfileSetAsides_yields_nothing_when_no_file_was_set_aside() {
            Assert.That(StartupNotificationFactory.ForProfileSetAsides(Array.Empty<ProfileSetAside>()), Is.Empty);
        }

        [Test]
        public void ForReconcilerResult_Interrupted_with_state_yields_Warning_and_RelatedEntity() {
            var seqId = Guid.NewGuid();
            var result = new SequenceReconcileResult(
                SequenceReconcileOutcome.Interrupted,
                PreviousState: SampleState(seqId),
                QuarantinedPath: null);

            var n = StartupNotificationFactory.ForReconcilerResult(result);

            Assert.That(n.Severity, Is.EqualTo(NotificationSeverity.Warning));
            Assert.That(n.Category, Is.EqualTo(NotificationCategory.Sequence));
            Assert.That(n.Title, Is.EqualTo("Previous sequence ended unexpectedly"));
            Assert.That(n.Read, Is.False);
            Assert.That(n.Dismissed, Is.False);
            Assert.That(n.RelatedEntityType, Is.EqualTo("sequence"));
            Assert.That(n.RelatedEntityId, Is.EqualTo(seqId.ToString()));
            // Message references frame counts from the previous state.
            Assert.That(n.Message, Does.Contain("4/10"));
        }

        [Test]
        public void ForReconcilerResult_Interrupted_without_state_omits_RelatedEntity() {
            // Defensive path — should not happen in practice, but the factory
            // shouldn't crash if PreviousState is null on an Interrupted outcome.
            var result = new SequenceReconcileResult(
                SequenceReconcileOutcome.Interrupted,
                PreviousState: null,
                QuarantinedPath: null);

            var n = StartupNotificationFactory.ForReconcilerResult(result);

            Assert.That(n.Severity, Is.EqualTo(NotificationSeverity.Warning));
            Assert.That(n.RelatedEntityType, Is.Null);
            Assert.That(n.RelatedEntityId, Is.Null);
            Assert.That(n.Message, Does.Contain("was running"));
        }

        [Test]
        public void ForReconcilerResult_Corrupt_with_quarantine_yields_Critical_and_path_in_message() {
            var path = "/profile/sequences/active/current.json.corrupt.1717000000";
            var result = new SequenceReconcileResult(
                SequenceReconcileOutcome.Corrupt,
                PreviousState: null,
                QuarantinedPath: path);

            var n = StartupNotificationFactory.ForReconcilerResult(result);

            Assert.That(n.Severity, Is.EqualTo(NotificationSeverity.Critical));
            Assert.That(n.Category, Is.EqualTo(NotificationCategory.Sequence));
            Assert.That(n.Title, Is.EqualTo("Sequence checkpoint was damaged"));
            Assert.That(n.Message, Does.Contain(path));
        }

        [Test]
        public void ForReconcilerResult_Corrupt_without_quarantine_path_still_critical() {
            // Hit when File.Move + fallback File.Delete both run (reconciler
            // last-resort path). No path to surface, but still a critical alert.
            var result = new SequenceReconcileResult(
                SequenceReconcileOutcome.Corrupt,
                PreviousState: null,
                QuarantinedPath: null);

            var n = StartupNotificationFactory.ForReconcilerResult(result);

            Assert.That(n.Severity, Is.EqualTo(NotificationSeverity.Critical));
            Assert.That(n.Message, Does.Contain("could not be quarantined"));
        }

        [Test]
        public void ForReconcilerResult_Clean_throws() {
            var result = new SequenceReconcileResult(
                SequenceReconcileOutcome.Clean, null, null);
            Assert.Throws<ArgumentException>(
                () => StartupNotificationFactory.ForReconcilerResult(result));
        }

        [Test]
        public void ForReconcilerResult_assigns_new_id_each_call() {
            var result = new SequenceReconcileResult(
                SequenceReconcileOutcome.Interrupted, SampleState(), null);
            var a = StartupNotificationFactory.ForReconcilerResult(result);
            var b = StartupNotificationFactory.ForReconcilerResult(result);
            Assert.That(a.Id, Is.Not.EqualTo(b.Id));
            Assert.That(a.Id, Is.Not.EqualTo(Guid.Empty));
        }

        // §38j-9 — diagnostic-event factory for Corrupt outcomes.

        [Test]
        public void DiagnosticForCorruptResult_with_quarantine_yields_Red_auto_cleared_event() {
            var path = "/profile/sequences/active/current.json.corrupt.1717999000";
            var result = new SequenceReconcileResult(
                SequenceReconcileOutcome.Corrupt,
                PreviousState: null,
                QuarantinedPath: path);

            var (evt, rec, autoCorr) = StartupNotificationFactory.DiagnosticForCorruptResult(result);

            Assert.That(evt.Severity, Is.EqualTo(DiagnosticHealth.Red));
            Assert.That(evt.EventType, Is.EqualTo("sequence.checkpoint.corrupt"));
            // Pre-cleared — the quarantine already took care of the file,
            // so nothing for the user to do; §51 panel sees this in history,
            // not the open-issues count.
            Assert.That(evt.ClearedUtc, Is.Not.Null);
            Assert.That(evt.AutoActionTaken, Is.True);
            Assert.That(evt.AutoActionDescription, Does.Contain(path));
            Assert.That(evt.Description, Does.Contain(path));
            Assert.That(autoCorr, Is.True);
            Assert.That(rec, Is.Null);  // nothing for the user to do
        }

        [Test]
        public void DiagnosticForCorruptResult_without_quarantine_uses_delete_fallback_copy() {
            var result = new SequenceReconcileResult(
                SequenceReconcileOutcome.Corrupt,
                PreviousState: null,
                QuarantinedPath: null);

            var (evt, _, _) = StartupNotificationFactory.DiagnosticForCorruptResult(result);

            Assert.That(evt.Severity, Is.EqualTo(DiagnosticHealth.Red));
            Assert.That(evt.AutoActionDescription, Does.Contain("Deleted"));
            Assert.That(evt.Description, Does.Contain("could not be quarantined")
                .Or.Contain("deleted").IgnoreCase);
        }

        [Test]
        public void DiagnosticForCorruptResult_rejects_non_Corrupt_outcomes() {
            var clean = new SequenceReconcileResult(
                SequenceReconcileOutcome.Clean, null, null);
            var interrupted = new SequenceReconcileResult(
                SequenceReconcileOutcome.Interrupted, SampleState(), null);

            Assert.Throws<ArgumentException>(
                () => StartupNotificationFactory.DiagnosticForCorruptResult(clean));
            Assert.Throws<ArgumentException>(
                () => StartupNotificationFactory.DiagnosticForCorruptResult(interrupted));
        }
    }
}