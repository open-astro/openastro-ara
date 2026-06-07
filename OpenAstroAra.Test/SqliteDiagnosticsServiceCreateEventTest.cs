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
    /// §38j-9 — verifies the new <see cref="IDiagnosticsService.CreateEventAsync"/>
    /// surface lands a row in the §51 catalog and surfaces in subsequent
    /// history queries. Used by the §28.2 startup reconciler to emit a Red
    /// auto-cleared event for the Corrupt outcome.
    /// </summary>
    [TestFixture]
    public class SqliteDiagnosticsServiceCreateEventTest {

        private string _profileDir = string.Empty;
        private SqliteAraDatabase _db = null!;
        private SqliteDiagnosticsService _svc = null!;

        [SetUp]
        public async Task SetUp() {
            _profileDir = Path.Combine(Path.GetTempPath(), $"oara-diag-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_profileDir);
            _db = new SqliteAraDatabase(_profileDir, logger: null);
            await _db.InitializeAsync(CancellationToken.None);
            _svc = new SqliteDiagnosticsService(_db, logger: null);
        }

        [TearDown]
        public void TearDown() {
            try { Directory.Delete(_profileDir, recursive: true); } catch { }
        }

        private static DiagnosticEventDto Sample(Guid id, DiagnosticHealth sev, string eventType) {
            var now = DateTimeOffset.UtcNow;
            return new DiagnosticEventDto(
                Id: id,
                EventType: eventType,
                Severity: sev,
                Description: $"Body of {eventType}",
                DetectedUtc: now,
                ClearedUtc: now,
                AutoActionTaken: true,
                AutoActionDescription: "auto-cleared");
        }

        [Test]
        public async Task CreateEventAsync_inserts_a_row_that_GetHistoryAsync_returns() {
            var id = Guid.NewGuid();
            await _svc.CreateEventAsync(
                Sample(id, DiagnosticHealth.Red, "sequence.checkpoint.corrupt"),
                recommendedAction: null,
                autoCorrectible: true,
                CancellationToken.None);

            var page = await _svc.GetHistoryAsync(50, cursor: null, CancellationToken.None);
            var hit = page.Items.FirstOrDefault(x => x.Id == id);
            Assert.That(hit, Is.Not.Null);
            Assert.That(hit!.EventType, Is.EqualTo("sequence.checkpoint.corrupt"));
            Assert.That(hit.Severity, Is.EqualTo(DiagnosticHealth.Red));
            Assert.That(hit.AutoActionTaken, Is.True);
            // Pre-cleared events live in history, not the open set.
            Assert.That(hit.ClearedUtc, Is.Not.Null);
        }

        [Test]
        public async Task CreateEventAsync_pre_cleared_event_does_not_open_an_issue() {
            // The Corrupt diagnostic is pre-cleared: ClearedUtc != null at
            // insert time. GetStateAsync's open-issues should not include
            // this event because cleared_utc IS NOT NULL.
            var id = Guid.NewGuid();
            await _svc.CreateEventAsync(
                Sample(id, DiagnosticHealth.Red, "sequence.checkpoint.corrupt"),
                recommendedAction: null,
                autoCorrectible: true,
                CancellationToken.None);

            var state = await _svc.GetStateAsync(CancellationToken.None);
            // open_issues is the cleared_utc IS NULL slice; a pre-cleared
            // event must not show up there.
            Assert.That(state.OpenIssues.Any(x => x.Id == id), Is.False,
                "Pre-cleared diagnostic event should not appear in OpenIssues.");
        }

        [Test]
        public async Task CreateEventAsync_after_seed_does_not_re_trigger_seed() {
            // EnsureSeededAsync should be no-op once any row exists, even
            // one inserted via CreateEventAsync. Mirrors the §38j-8 invariant
            // for notifications.
            var id = Guid.NewGuid();
            await _svc.CreateEventAsync(
                Sample(id, DiagnosticHealth.Red, "sequence.checkpoint.corrupt"),
                recommendedAction: null,
                autoCorrectible: true,
                CancellationToken.None);
            await _svc.EnsureSeededAsync(CancellationToken.None);

            var page = await _svc.GetHistoryAsync(50, null, CancellationToken.None);
            Assert.That(page.Items, Has.Count.EqualTo(1));
            Assert.That(page.Items[0].Id, Is.EqualTo(id));
        }
    }
}