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
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §60.9 (WS slice 4) — SqliteDiagnosticsService publishes diagnostics.* events to the WS broadcaster when
    /// a diagnostic is raised (CreateEventAsync) or cleared (ClearOpenEventsByTypeAsync), so the §51 panel goes
    /// live. A no-op clear publishes nothing.
    /// </summary>
    [TestFixture]
    public class SqliteDiagnosticsServiceWsEventTest {

        private sealed class FakeWsBroadcaster : IWsBroadcaster {
            public readonly List<(string Type, JsonElement Payload)> Published = new();
            public bool Throw { get; set; }
            public long CurrentSequence { get; private set; }
            public Task PublishAsync(string eventType, JsonElement payload, CancellationToken ct) {
                CurrentSequence++;
                if (Throw) {
                    throw new InvalidOperationException("broadcaster is down");
                }
                Published.Add((eventType, payload.Clone()));
                return Task.CompletedTask;
            }
        }

        private string _profileDir = string.Empty;
        private SqliteAraDatabase _db = null!;
        private FakeWsBroadcaster _ws = null!;
        private SqliteDiagnosticsService _svc = null!;

        [SetUp]
        public async Task SetUp() {
            _profileDir = Path.Combine(Path.GetTempPath(), $"oara-diagws-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_profileDir);
            _db = new SqliteAraDatabase(_profileDir, logger: null);
            await _db.InitializeAsync(CancellationToken.None);
            _ws = new FakeWsBroadcaster();
            _svc = new SqliteDiagnosticsService(_db, logger: null, _ws);
        }

        [TearDown]
        public void TearDown() {
            try { Directory.Delete(_profileDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private static DiagnosticEventDto Event(string type, DiagnosticHealth sev, bool autoAction) =>
            new(Id: Guid.NewGuid(), EventType: type, Severity: sev, Description: $"Body of {type}",
                DetectedUtc: DateTimeOffset.UtcNow, ClearedUtc: null,
                AutoActionTaken: autoAction, AutoActionDescription: autoAction ? "auto-cleared" : null);

        [Test]
        public async Task CreateEvent_publishes_issue_detected_with_payload() {
            await _svc.CreateEventAsync(Event("disk.low", DiagnosticHealth.Yellow, autoAction: false),
                recommendedAction: "free up space", autoCorrectible: false, CancellationToken.None);

            Assert.That(_ws.Published, Has.Count.EqualTo(1));
            var (type, payload) = _ws.Published[0];
            Assert.That(type, Is.EqualTo("diagnostics.issue_detected"));
            Assert.That(payload.GetProperty("event_type").GetString(), Is.EqualTo("disk.low"));
            Assert.That(payload.GetProperty("severity").GetString(), Is.EqualTo("yellow"));
            Assert.That(payload.GetProperty("auto_action_taken").GetBoolean(), Is.False);
            Assert.That(payload.GetProperty("recommended_action").GetString(), Is.EqualTo("free up space"));
        }

        [Test]
        public async Task CreateEvent_with_auto_action_publishes_auto_action_taken() {
            await _svc.CreateEventAsync(Event("disk.critical", DiagnosticHealth.Red, autoAction: true),
                recommendedAction: null, autoCorrectible: null, CancellationToken.None);

            Assert.That(_ws.Published, Has.Count.EqualTo(1));
            Assert.That(_ws.Published[0].Type, Is.EqualTo("diagnostics.auto_action_taken"));
            Assert.That(_ws.Published[0].Payload.GetProperty("severity").GetString(), Is.EqualTo("red"));
        }

        [Test]
        public async Task Clear_that_closes_open_events_publishes_cleared() {
            await _svc.CreateEventAsync(Event("disk.low", DiagnosticHealth.Yellow, autoAction: false),
                recommendedAction: null, autoCorrectible: null, CancellationToken.None);
            _ws.Published.Clear();

            var affected = await _svc.ClearOpenEventsByTypeAsync("disk.low", DateTimeOffset.UtcNow, CancellationToken.None);

            Assert.That(affected, Is.EqualTo(1));
            Assert.That(_ws.Published, Has.Count.EqualTo(1));
            Assert.That(_ws.Published[0].Type, Is.EqualTo("diagnostics.cleared"));
            Assert.That(_ws.Published[0].Payload.GetProperty("event_type").GetString(), Is.EqualTo("disk.low"));
            Assert.That(_ws.Published[0].Payload.GetProperty("cleared_count").GetInt32(), Is.EqualTo(1));
        }

        [Test]
        public async Task Clear_with_no_open_events_publishes_nothing() {
            var affected = await _svc.ClearOpenEventsByTypeAsync("nothing.open", DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.That(affected, Is.EqualTo(0));
            Assert.That(_ws.Published, Is.Empty);
        }

        [Test]
        public async Task A_throwing_broadcaster_does_not_propagate_to_callers() {
            // WS emission is best-effort: a broadcaster fault must not surface as a failed diagnostic
            // raise/clear, since the SQLite write (the source of truth) already succeeded.
            _ws.Throw = true;

            Assert.DoesNotThrowAsync(async () => await _svc.CreateEventAsync(
                Event("disk.low", DiagnosticHealth.Yellow, autoAction: false),
                recommendedAction: null, autoCorrectible: null, CancellationToken.None));

            var affected = await _svc.ClearOpenEventsByTypeAsync("disk.low", DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.That(affected, Is.EqualTo(1), "the row was still persisted and cleared despite the WS fault");
        }
    }
}
