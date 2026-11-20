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

    /// <summary>
    /// §58.8 — an optics-train change resets <c>first_flip_confirmed</c> (the safety net assumes
    /// the rig hasn't changed since the user watched a flip succeed; optics is the ARA profile's
    /// rig identity). An identical re-save keeps the confirmation — panel Save-without-changes
    /// must not re-arm the announce. Covered on both store implementations.
    /// </summary>
    [TestFixture]
    [Category("IO")] // #1265 — real disk, loopback HTTP or a simulator: not part of the quick unit run
    public class FirstFlipConfirmationResetTest {

        /// <summary>#1589 — a confirmation always comes with the completed flip it confirms; a stored
        /// <c>true</c> without one is a legacy value the file store resets on its next write.</summary>
        private static SafetyPoliciesDto Confirmed(SafetyPoliciesDto current) =>
            current with { FirstFlipConfirmed = true, FirstFlipCompletedUtc = FlipInstant, FirstFlipPierSideChanged = true };

        private static readonly DateTimeOffset FlipInstant = new(2026, 11, 20, 3, 0, 0, TimeSpan.Zero);

        private static void AssertResetSemantics(IProfileStore store) {
            store.PutSafetyPolicies(Confirmed(store.GetSafetyPolicies()));
            var optics = store.GetOpticsSettings();

            // Identical re-save (the panel's Save with nothing edited) keeps the confirmation.
            store.PutOpticsSettings(optics);
            Assert.That(store.GetSafetyPolicies().FirstFlipConfirmed, Is.True,
                "an identical optics re-save is not an equipment change");

            // A genuine train change re-arms the announce.
            store.PutOpticsSettings(optics with { FocalLengthMm = optics.FocalLengthMm + 100 });
            Assert.That(store.GetSafetyPolicies().FirstFlipConfirmed, Is.False,
                "a changed optics train invalidates the first-flip confirmation");

            // The FUNCTIONAL Update path must reset too — the camera-connect auto-populate
            // (CameraService.MaybeAutoPopulateOptics) writes sensor dimensions through
            // UpdateOpticsSettings, and a swapped camera is exactly the rig change the
            // §58.8 net re-arms on.
            store.PutSafetyPolicies(Confirmed(store.GetSafetyPolicies()));
            var unchanged = store.UpdateOpticsSettings(current => current);
            Assert.That(store.GetSafetyPolicies().FirstFlipConfirmed, Is.True,
                "a no-op Update (same reference / null) is not an equipment change");
            Assert.That(unchanged, Is.EqualTo(store.GetOpticsSettings()));

            store.UpdateOpticsSettings(current => current with { SensorWidthPx = current.SensorWidthPx + 1 });
            Assert.That(store.GetSafetyPolicies().FirstFlipConfirmed, Is.False,
                "an optics change through the Update path must reset like the Put path");
        }

        [Test]
        public void The_safety_PUT_merge_preserves_the_stored_confirmation_both_ways() {
            // §58.8 — FirstFlipConfirmed is daemon-owned: a client PUT carrying a stale value
            // (either direction) must not move the stored one. Everything else is client-editable
            // and flows through.
            var stored = new SafetyPoliciesDto(
                OnUnsafe: "pause_and_park", AutoResumeWhenSafe: true, ResumeDelayMin: 10,
                MeridianFlipAuto: true, MeridianPauseMin: 5, MeridianRecenter: true,
                MeridianRecalGuider: true, OnAltitudeLimit: "skip_target",
                ParkIfNoMoreTargets: true, OnGuiderLost: "pause_and_retry",
                GuiderRetryTimeoutSec: 60, SkipTargetIfRecoveryFails: true,
                FirstFlipConfirmed: true);
            var staleIncoming = stored with { FirstFlipConfirmed = false, ResumeDelayMin = 20 };

            var merged = OpenAstroAra.Server.Endpoints.ProfileEndpoints
                .PreserveDaemonOwnedSafetyFields(staleIncoming, stored);
            Assert.That(merged.FirstFlipConfirmed, Is.True,
                "a stale hydrate PUT back after an overnight flip must not clear the confirmation");
            Assert.That(merged.ResumeDelayMin, Is.EqualTo(20), "client-editable fields flow through");

            var armedStored = stored with { FirstFlipConfirmed = false };
            var optimisticIncoming = stored with { FirstFlipConfirmed = true };
            Assert.That(OpenAstroAra.Server.Endpoints.ProfileEndpoints
                    .PreserveDaemonOwnedSafetyFields(optimisticIncoming, armedStored).FirstFlipConfirmed,
                Is.False,
                "nor can a client PUT ever CONFIRM a flip — only the user's POST /safety/first-flip/confirm does that");
        }

        [Test]
        public void The_safety_PUT_merge_keeps_the_stored_completed_flip_record() {
            // #1589 — the completed-flip record is daemon-owned too: a PUT can neither invent one
            // (and so open the way to a confirm) nor clear the one the executor wrote.
            var stored = new InMemoryProfileStore().GetSafetyPolicies();
            var invented = stored with { FirstFlipCompletedUtc = FlipInstant, FirstFlipPierSideChanged = true };
            var merged = OpenAstroAra.Server.Endpoints.ProfileEndpoints.PreserveDaemonOwnedSafetyFields(invented, stored);
            Assert.That(merged.FirstFlipCompletedUtc, Is.Null, "a client cannot record a flip");
            Assert.That(merged.FirstFlipPierSideChanged, Is.False);

            var recorded = stored with { FirstFlipCompletedUtc = FlipInstant, FirstFlipPierSideChanged = true };
            var stale = OpenAstroAra.Server.Endpoints.ProfileEndpoints.PreserveDaemonOwnedSafetyFields(stored, recorded);
            Assert.That(stale.FirstFlipCompletedUtc, Is.EqualTo(FlipInstant), "a stale PUT keeps the executor's record");
            Assert.That(stale.FirstFlipPierSideChanged, Is.True);
        }

        private static void AssertOpticsChangeClearsTheRecord(IProfileStore store) {
            store.UpdateSafetyPolicies(current => current with { FirstFlipCompletedUtc = FlipInstant, FirstFlipPierSideChanged = true });
            var optics = store.GetOpticsSettings();
            store.PutOpticsSettings(optics);
            Assert.That(store.GetSafetyPolicies().FirstFlipCompletedUtc, Is.EqualTo(FlipInstant), "an identical re-save keeps the record");

            store.PutOpticsSettings(optics with { FocalLengthMm = optics.FocalLengthMm + 100 });
            Assert.That(store.GetSafetyPolicies().FirstFlipCompletedUtc, Is.Null,
                "a flip on the old optics train is nothing to confirm for the new one");
            Assert.That(store.GetSafetyPolicies().FirstFlipPierSideChanged, Is.False);

            store.UpdateSafetyPolicies(current => current with { FirstFlipCompletedUtc = FlipInstant });
            store.UpdateOpticsSettings(current => current with { SensorWidthPx = current.SensorWidthPx + 1 });
            Assert.That(store.GetSafetyPolicies().FirstFlipCompletedUtc, Is.Null, "the Update path clears it too");
        }

        [Test]
        public void An_optics_change_clears_the_completed_flip_record_on_both_stores() {
            AssertOpticsChangeClearsTheRecord(new InMemoryProfileStore());
            var dir = Path.Combine(Path.GetTempPath(), "ara-firstflip-" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            try {
                AssertOpticsChangeClearsTheRecord(new FileProfileStore(dir));
            } finally {
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        [Test]
        public void A_legacy_confirmation_with_no_completed_flip_loads_unconfirmed() {
            // #1589 — before this change the executor wrote true as soon as the 60 s wait ended,
            // before any flip ran: such a true proves nothing and is reset on load. A confirmation
            // with its completed flip survives a reopen.
            var dir = Path.Combine(Path.GetTempPath(), "ara-firstflip-" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            try {
                new FileProfileStore(dir).UpdateSafetyPolicies(current => current with { FirstFlipConfirmed = true });
                Assert.That(new FileProfileStore(dir).GetSafetyPolicies().FirstFlipConfirmed, Is.False,
                    "a legacy true with no completed flip is not a confirmation");

                new FileProfileStore(dir).UpdateSafetyPolicies(Confirmed);
                var reopened = new FileProfileStore(dir).GetSafetyPolicies();
                Assert.That(reopened.FirstFlipConfirmed, Is.True);
                Assert.That(reopened.FirstFlipCompletedUtc, Is.EqualTo(FlipInstant));
            } finally {
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        [Test]
        public void A_created_profile_starts_unconfirmed() {
            // #1589 review — the wizard and duplicate-current create a profile with no settings, which
            // clones the live store. The new profile is usually for another mount, and no flip ran on it,
            // so it must not inherit the source's confirmation or completed flip. The source keeps both.
            var dir = Path.Combine(Path.GetTempPath(), "ara-firstflip-" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            try {
                var store = new FileProfileStore(dir);
                using var repo = new FileProfileRepository(dir, store);
                var rigA = repo.Create("Rig A", settings: null, makeActive: true);
                store.UpdateSafetyPolicies(Confirmed);

                var twin = repo.Create("Rig A twin", settings: null, makeActive: false);
                AssertUnconfirmed(repo.GetProfile(twin.Id)!.Settings.SafetyPolicies, "a duplicate");
                Assert.That(store.GetSafetyPolicies().FirstFlipConfirmed, Is.True,
                    "a duplicate that is not activated leaves the active profile confirmed");

                var rigB = repo.Create("Rig B (new mount)", settings: null, makeActive: true);
                AssertUnconfirmed(store.GetSafetyPolicies(), "the live store after a wizard create");
                AssertUnconfirmed(repo.GetProfile(rigB.Id)!.Settings.SafetyPolicies, "a wizard create");
                Assert.That(repo.GetProfile(rigA.Id)!.Settings.SafetyPolicies.FirstFlipConfirmed, Is.True,
                    "the source profile keeps its own confirmation");
            } finally {
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        [Test]
        public void A_create_body_cannot_carry_a_confirmation() {
            // #1589 review — only the flip executor and the confirm endpoint write the first-flip
            // fields. A create body with settings (a client that sends a snapshot) starts unconfirmed too.
            var dir = Path.Combine(Path.GetTempPath(), "ara-firstflip-" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            try {
                var store = new FileProfileStore(dir);
                using var repo = new FileProfileRepository(dir, store);
                repo.Create("Rig A", settings: null, makeActive: true);
                var body = ProfileStoreSnapshot.Capture(store);
                body = body with { SafetyPolicies = Confirmed(body.SafetyPolicies) };

                var created = repo.Create("Rig B", body, makeActive: true);
                AssertUnconfirmed(repo.GetProfile(created.Id)!.Settings.SafetyPolicies, "a create with a body");
                AssertUnconfirmed(store.GetSafetyPolicies(), "the live store after a create with a body");
            } finally {
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static void AssertUnconfirmed(SafetyPoliciesDto s, string what) {
            Assert.That(s.FirstFlipConfirmed, Is.False, $"{what} has had no flip, so it is not confirmed");
            Assert.That(s.FirstFlipCompletedUtc, Is.Null, $"{what} has no completed flip on record");
            Assert.That(s.FirstFlipPierSideChanged, Is.False, $"{what} has no pier-side change on record");
        }

        [Test]
        public void InMemory_store_resets_on_optics_change_only() =>
            AssertResetSemantics(new InMemoryProfileStore());

        [Test]
        public void File_store_resets_on_optics_change_only_and_persists_it() {
            var dir = Path.Combine(Path.GetTempPath(), "ara-firstflip-" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            try {
                var store = new FileProfileStore(dir);
                AssertResetSemantics(store);
                // The reset survives a reopen — it went through the same persist as the optics.
                var reopened = new FileProfileStore(dir);
                Assert.That(reopened.GetSafetyPolicies().FirstFlipConfirmed, Is.False);
            } finally {
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
