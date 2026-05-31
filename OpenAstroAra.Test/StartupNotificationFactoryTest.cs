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
            FramesCompleted: 4,
            FramesTotal: 10,
            CurrentInstructionDescription: null);

        [Test]
        public void ForReconcilerResult_Interrupted_with_state_yields_Warning_and_RelatedEntity() {
            var seqId = Guid.NewGuid();
            var result = new SequenceStartupReconciler.Result(
                SequenceStartupReconciler.Outcome.Interrupted,
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
            var result = new SequenceStartupReconciler.Result(
                SequenceStartupReconciler.Outcome.Interrupted,
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
            var result = new SequenceStartupReconciler.Result(
                SequenceStartupReconciler.Outcome.Corrupt,
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
            var result = new SequenceStartupReconciler.Result(
                SequenceStartupReconciler.Outcome.Corrupt,
                PreviousState: null,
                QuarantinedPath: null);

            var n = StartupNotificationFactory.ForReconcilerResult(result);

            Assert.That(n.Severity, Is.EqualTo(NotificationSeverity.Critical));
            Assert.That(n.Message, Does.Contain("could not be quarantined"));
        }

        [Test]
        public void ForReconcilerResult_Clean_throws() {
            var result = new SequenceStartupReconciler.Result(
                SequenceStartupReconciler.Outcome.Clean, null, null);
            Assert.Throws<ArgumentException>(
                () => StartupNotificationFactory.ForReconcilerResult(result));
        }

        [Test]
        public void ForReconcilerResult_assigns_new_id_each_call() {
            var result = new SequenceStartupReconciler.Result(
                SequenceStartupReconciler.Outcome.Interrupted, SampleState(), null);
            var a = StartupNotificationFactory.ForReconcilerResult(result);
            var b = StartupNotificationFactory.ForReconcilerResult(result);
            Assert.That(a.Id, Is.Not.EqualTo(b.Id));
            Assert.That(a.Id, Is.Not.EqualTo(Guid.Empty));
        }
    }
}
