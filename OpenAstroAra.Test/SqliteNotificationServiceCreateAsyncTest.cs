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
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §38j-8 — verifies the new <see cref="INotificationService.CreateAsync"/>
    /// surface lands a row in the SQLite catalog and surfaces in subsequent
    /// list queries. Used by the §28.2 startup reconciler to emit the
    /// "previous sequence ended unexpectedly" inbox entry.
    /// </summary>
    [TestFixture]
    public class SqliteNotificationServiceCreateAsyncTest {

        private string _profileDir = string.Empty;
        private SqliteAraDatabase _db = null!;
        private SqliteNotificationService _svc = null!;

        [SetUp]
        public async Task SetUp() {
            _profileDir = Path.Combine(Path.GetTempPath(), $"oara-notif-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_profileDir);
            _db = new SqliteAraDatabase(_profileDir, logger: null);
            await _db.InitializeAsync(CancellationToken.None);
            _svc = new SqliteNotificationService(_db, logger: null);
        }

        [TearDown]
        public void TearDown() {
            try { Directory.Delete(_profileDir, recursive: true); } catch (System.IO.IOException) { } catch (System.UnauthorizedAccessException) { }
        }

        private static NotificationDto Sample(Guid id, NotificationSeverity sev, string title) => new(
            Id: id,
            PostedUtc: DateTimeOffset.UtcNow,
            Severity: sev,
            Category: NotificationCategory.Sequence,
            Title: title,
            Message: $"Body of {title}",
            Read: false,
            Dismissed: false,
            DismissedUtc: null,
            Payload: null,
            RelatedEntityType: "sequence",
            RelatedEntityId: Guid.NewGuid().ToString());

        [Test]
        public async Task CreateAsync_inserts_a_row_that_ListAsync_returns() {
            var id = Guid.NewGuid();
            await _svc.CreateAsync(Sample(id, NotificationSeverity.Warning, "Test warning"), CancellationToken.None);

            var page = await _svc.ListAsync(50, cursor: null, unreadOnly: null, CancellationToken.None);
            var hit = page.Items.FirstOrDefault(x => x.Id == id);
            Assert.That(hit, Is.Not.Null);
            Assert.That(hit!.Title, Is.EqualTo("Test warning"));
            Assert.That(hit.Severity, Is.EqualTo(NotificationSeverity.Warning));
            Assert.That(hit.Read, Is.False);
            Assert.That(hit.Dismissed, Is.False);
        }

        [Test]
        public async Task CreateAsync_round_trips_RelatedEntity_columns() {
            var id = Guid.NewGuid();
            var seqId = Guid.NewGuid().ToString();
            var n = Sample(id, NotificationSeverity.Critical, "Checkpoint corrupt") with {
                RelatedEntityType = "sequence",
                RelatedEntityId = seqId,
            };
            await _svc.CreateAsync(n, CancellationToken.None);

            var page = await _svc.ListAsync(50, null, null, CancellationToken.None);
            var hit = page.Items.First(x => x.Id == id);
            Assert.That(hit.RelatedEntityType, Is.EqualTo("sequence"));
            Assert.That(hit.RelatedEntityId, Is.EqualTo(seqId));
        }

        [Test]
        public async Task CreateAsync_then_unreadOnly_filter_returns_new_row() {
            // Seed the table with the 3 sample notifications (one is read).
            await _svc.EnsureSeededAsync(CancellationToken.None);
            var id = Guid.NewGuid();
            await _svc.CreateAsync(Sample(id, NotificationSeverity.Warning, "Just emitted"), CancellationToken.None);

            var unread = await _svc.ListAsync(50, null, unreadOnly: true, CancellationToken.None);
            Assert.That(unread.Items.Any(x => x.Id == id), Is.True);
        }

        // ─── §58.10 — unattended-hours severity escalation at the CreateAsync chokepoint ───

        // Winter solstice at lat 40 N, lon 0: local midnight has the sun ~73° below the horizon
        // (deep astronomical darkness); local noon has it ~26° up. Both far from the −18° edge,
        // so the tests never depend on almanac precision.
        private static readonly DateTimeOffset WinterMidnightUtc = new(2026, 12, 21, 0, 0, 0, TimeSpan.Zero);
        private static readonly DateTimeOffset WinterNoonUtc = new(2026, 12, 21, 12, 0, 0, TimeSpan.Zero);

        private SqliteNotificationService UnattendedAwareService(DateTimeOffset now, bool escalationEnabled = true) {
            var profiles = new InMemoryProfileStore();
            profiles.PutSiteSettings(profiles.GetSiteSettings() with { LatitudeDeg = 40, LongitudeDeg = 0 });
            profiles.PutSafetyPolicies(profiles.GetSafetyPolicies() with { UnattendedEscalation = escalationEnabled });
            return new SqliteNotificationService(_db, logger: null, profiles) { UtcNow = () => now };
        }

        [Test]
        public async Task At_night_an_equipment_impacting_warning_lands_as_error_and_says_why() {
            var svc = UnattendedAwareService(WinterMidnightUtc);
            var id = Guid.NewGuid();
            await svc.CreateAsync(Sample(id, NotificationSeverity.Warning, "Dew heater dropout"), CancellationToken.None);

            var hit = (await svc.ListAsync(50, null, null, CancellationToken.None)).Items.First(x => x.Id == id);
            Assert.That(hit.Severity, Is.EqualTo(NotificationSeverity.Error), "Warning bumps one level unattended");
            Assert.That(hit.Message, Does.Contain("unattended hours"),
                "morning triage must see WHY the severity is higher than the event usually carries");
        }

        [Test]
        public async Task At_noon_the_same_warning_lands_untouched() {
            var svc = UnattendedAwareService(WinterNoonUtc);
            var id = Guid.NewGuid();
            await svc.CreateAsync(Sample(id, NotificationSeverity.Warning, "Dew heater dropout"), CancellationToken.None);

            var hit = (await svc.ListAsync(50, null, null, CancellationToken.None)).Items.First(x => x.Id == id);
            Assert.That(hit.Severity, Is.EqualTo(NotificationSeverity.Warning));
            Assert.That(hit.Message, Does.Not.Contain("unattended hours"));
        }

        [Test]
        public async Task Critical_is_already_the_ceiling_and_gains_no_suffix() {
            var svc = UnattendedAwareService(WinterMidnightUtc);
            var id = Guid.NewGuid();
            await svc.CreateAsync(Sample(id, NotificationSeverity.Critical, "Flip failed"), CancellationToken.None);

            var hit = (await svc.ListAsync(50, null, null, CancellationToken.None)).Items.First(x => x.Id == id);
            Assert.That(hit.Severity, Is.EqualTo(NotificationSeverity.Critical));
            Assert.That(hit.Message, Does.Not.Contain("unattended hours"),
                "an unchanged severity must not claim it was raised");
        }

        [Test]
        public async Task Software_chatter_is_not_equipment_impacting_and_stays_put_at_night() {
            var svc = UnattendedAwareService(WinterMidnightUtc);
            var id = Guid.NewGuid();
            var n = Sample(id, NotificationSeverity.Warning, "Update available") with {
                Category = NotificationCategory.Software,
            };
            await svc.CreateAsync(n, CancellationToken.None);

            var hit = (await svc.ListAsync(50, null, null, CancellationToken.None)).Items.First(x => x.Id == id);
            Assert.That(hit.Severity, Is.EqualTo(NotificationSeverity.Warning));
        }

        [Test]
        public async Task The_profile_toggle_disables_the_escalation() {
            var svc = UnattendedAwareService(WinterMidnightUtc, escalationEnabled: false);
            var id = Guid.NewGuid();
            await svc.CreateAsync(Sample(id, NotificationSeverity.Warning, "Dew heater dropout"), CancellationToken.None);

            var hit = (await svc.ListAsync(50, null, null, CancellationToken.None)).Items.First(x => x.Id == id);
            Assert.That(hit.Severity, Is.EqualTo(NotificationSeverity.Warning));
        }

        [Test]
        public async Task Without_a_profile_store_the_shim_is_inert() {
            // The default fixture service has no profile store — a Warning at ANY hour lands as-is.
            var id = Guid.NewGuid();
            await _svc.CreateAsync(Sample(id, NotificationSeverity.Warning, "No profiles wired"), CancellationToken.None);
            var hit = (await _svc.ListAsync(50, null, null, CancellationToken.None)).Items.First(x => x.Id == id);
            Assert.That(hit.Severity, Is.EqualTo(NotificationSeverity.Warning));
        }

        [Test]
        public async Task CreateAsync_after_seed_does_not_re_trigger_seed() {
            // EnsureSeededAsync should be no-op once any row exists, even one
            // inserted via CreateAsync. Otherwise the reconciler's emit would
            // accidentally re-seed the inbox on every startup.
            var id = Guid.NewGuid();
            await _svc.CreateAsync(Sample(id, NotificationSeverity.Info, "First"), CancellationToken.None);
            await _svc.EnsureSeededAsync(CancellationToken.None);

            var page = await _svc.ListAsync(50, null, null, CancellationToken.None);
            // Exactly one row — seed bailed out because we had one already.
            Assert.That(page.Items, Has.Count.EqualTo(1));
            Assert.That(page.Items[0].Id, Is.EqualTo(id));
        }
    }
}