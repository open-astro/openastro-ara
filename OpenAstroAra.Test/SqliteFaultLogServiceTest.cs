#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Moq;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>§42.5 — the persisted fault log: detection insert, reaction-outcome
    /// stamping (with the action-lands-first upsert race), session attribution,
    /// and the newest-first filtered read API.</summary>
    [TestFixture]
    public class SqliteFaultLogServiceTest {

        private string profileDir = null!;
        private SqliteAraDatabase db = null!;
        private ActiveRunSessionRegistry registry = null!;
        private SqliteFaultLogService service = null!;

        [SetUp]
        public async Task SetUp() {
            profileDir = Path.Combine(Path.GetTempPath(), $"oara-faults-{Guid.NewGuid():N}");
            Directory.CreateDirectory(profileDir);
            db = new SqliteAraDatabase(profileDir, logger: null);
            await db.InitializeAsync(CancellationToken.None);
            registry = new ActiveRunSessionRegistry();
            service = new SqliteFaultLogService(db, logger: null, sessions: registry);
        }

        [TearDown]
        public void TearDown() {
            service.Dispose();
            try {
                Directory.Delete(profileDir, recursive: true);
            } catch (IOException) {
            } catch (UnauthorizedAccessException) {
            }
        }

        private static readonly string[] CameraOnly = ["camera"];
        private static readonly string[] ValueMismatchOnly = ["value_mismatch"];
        private static readonly string[] TelescopeOnly = ["telescope"];
        private static readonly string[] SwitchAndTelescope = ["switch", "telescope"];

        private static EquipmentFaultEvent Fault(
                DeviceType type = DeviceType.Camera,
                EquipmentFaultKind kind = EquipmentFaultKind.Disconnected,
                DateTimeOffset? detectedUtc = null,
                string? deviceId = "dev-1") =>
            new(type, deviceId, "Test Device", kind, "3 probes failed",
                detectedUtc ?? new DateTimeOffset(2026, 7, 10, 4, 0, 0, TimeSpan.Zero));

        private async Task<Guid> CreateSessionRowAsync() {
            var id = Guid.NewGuid();
            await using var conn = db.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO sessions (id, started_at) VALUES ($id, $now);";
            cmd.Parameters.AddWithValue("$id", id.ToString());
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
            return id;
        }

        [Test]
        public async Task Detection_round_trips_through_the_read_api() {
            var fault = Fault();
            await service.RecordFaultAsync(fault, CancellationToken.None);

            var page = await service.ListAsync(50, null, null, null, null, null, CancellationToken.None);
            Assert.That(page.Items, Has.Count.EqualTo(1));
            var row = page.Items[0];
            Assert.That(row.EquipmentType, Is.EqualTo("camera"));
            Assert.That(row.FaultType, Is.EqualTo("disconnected"));
            Assert.That(row.EquipmentId, Is.EqualTo("dev-1"));
            Assert.That(row.EquipmentName, Is.EqualTo("Test Device"));
            Assert.That(row.Details, Is.EqualTo("3 probes failed"));
            Assert.That(row.DetectedUtc, Is.EqualTo(fault.DetectedUtc));
            Assert.That(row.SessionId, Is.Null, "no run was active");
            Assert.That(row.ActionTaken, Is.Null, "no reaction has landed yet");
            Assert.That(row.ResolvedUtc, Is.Null);
            Assert.That(row.AffectedFrames, Is.Empty, "frame correlation is the §42.6 slice");

            // Field-wise compare — FaultDto record equality is reference-based on the
            // AffectedFrames list member, so two reads are never Equals-equal.
            var fetched = await service.GetAsync(row.Id, CancellationToken.None);
            Assert.That(fetched, Is.Not.Null);
            Assert.That(fetched!.Id, Is.EqualTo(row.Id));
            Assert.That(fetched.DetectedUtc, Is.EqualTo(row.DetectedUtc));
            Assert.That(fetched.EquipmentType, Is.EqualTo(row.EquipmentType));
            Assert.That(fetched.FaultType, Is.EqualTo(row.FaultType));
        }

        [Test]
        public async Task Detection_stamps_the_active_run_session() {
            var sessionId = await CreateSessionRowAsync();
            registry.Enter(sessionId);
            try {
                await service.RecordFaultAsync(Fault(), CancellationToken.None);
            } finally {
                registry.Exit(sessionId);
            }

            var page = await service.ListAsync(50, null, null, null, null, null, CancellationToken.None);
            Assert.That(page.Items[0].SessionId, Is.EqualTo(sessionId));
            Assert.That(registry.Current, Is.Null, "exit cleared the registry");
        }

        [Test]
        public async Task Detection_skips_attribution_when_several_runs_are_active() {
            var sessionA = await CreateSessionRowAsync();
            var sessionB = await CreateSessionRowAsync();
            registry.Enter(sessionA);
            registry.Enter(sessionB);
            try {
                Assert.That(registry.Current, Is.Null,
                    "a watch/timer fault can't tell which concurrent run it belongs to");
                await service.RecordFaultAsync(Fault(), CancellationToken.None);
            } finally {
                registry.Exit(sessionA);
                registry.Exit(sessionB);
            }

            var page = await service.ListAsync(50, null, null, null, null, null, CancellationToken.None);
            Assert.That(page.Items[0].SessionId, Is.Null,
                "ambiguous attribution records null, never a plausible-but-wrong session");
        }

        [Test]
        public async Task List_is_newest_first_and_paginates() {
            var t0 = new DateTimeOffset(2026, 7, 10, 3, 0, 0, TimeSpan.Zero);
            for (var i = 0; i < 3; i++) {
                await service.RecordFaultAsync(Fault(detectedUtc: t0.AddMinutes(i)), CancellationToken.None);
            }

            var first = await service.ListAsync(2, null, null, null, null, null, CancellationToken.None);
            Assert.That(first.Items.Select(f => f.DetectedUtc),
                Is.EqualTo(new[] { t0.AddMinutes(2), t0.AddMinutes(1) }), "newest first");
            Assert.That(first.HasMore, Is.True);

            var second = await service.ListAsync(2, first.NextCursor, null, null, null, null, CancellationToken.None);
            Assert.That(second.Items.Select(f => f.DetectedUtc), Is.EqualTo(new[] { t0 }));
            Assert.That(second.HasMore, Is.False);
            Assert.That(second.NextCursor, Is.Null);
        }

        [Test]
        public async Task List_filters_combine() {
            var sessionId = await CreateSessionRowAsync();
            var t0 = new DateTimeOffset(2026, 7, 10, 3, 0, 0, TimeSpan.Zero);
            await service.RecordFaultAsync(Fault(DeviceType.Camera, EquipmentFaultKind.Disconnected, t0), CancellationToken.None);
            await service.RecordFaultAsync(Fault(DeviceType.Switch, EquipmentFaultKind.ValueMismatch, t0.AddMinutes(1)), CancellationToken.None);
            registry.Enter(sessionId);
            try {
                await service.RecordFaultAsync(Fault(DeviceType.Telescope, EquipmentFaultKind.TrackingLost, t0.AddMinutes(2)), CancellationToken.None);
            } finally {
                registry.Exit(sessionId);
            }
            // Resolve the camera fault so unresolvedOnly can distinguish.
            await service.RecordActionAsync(Fault(DeviceType.Camera, EquipmentFaultKind.Disconnected, t0),
                "recovered", t0.AddMinutes(5), CancellationToken.None);

            var cameras = await service.ListAsync(50, null, "camera", null, null, null, CancellationToken.None);
            Assert.That(cameras.Items.Select(f => f.EquipmentType), Is.EqualTo(CameraOnly));

            var mismatches = await service.ListAsync(50, null, null, null, null, "value_mismatch", CancellationToken.None);
            Assert.That(mismatches.Items.Select(f => f.FaultType), Is.EqualTo(ValueMismatchOnly));

            var inSession = await service.ListAsync(50, null, null, sessionId, null, null, CancellationToken.None);
            Assert.That(inSession.Items.Select(f => f.EquipmentType), Is.EqualTo(TelescopeOnly));

            var unresolved = await service.ListAsync(50, null, null, null, true, null, CancellationToken.None);
            Assert.That(unresolved.Items.Select(f => f.EquipmentType),
                Is.EquivalentTo(SwitchAndTelescope), "the recovered camera fault drops out");
        }

        [Test]
        public async Task Actions_stamp_the_row_last_write_wins_and_recovered_resolves() {
            var fault = Fault();
            await service.RecordFaultAsync(fault, CancellationToken.None);

            await service.RecordActionAsync(fault, "reconnecting", null, CancellationToken.None);
            var page = await service.ListAsync(50, null, null, null, null, null, CancellationToken.None);
            Assert.That(page.Items[0].ActionTaken, Is.EqualTo("reconnecting"));
            Assert.That(page.Items[0].ResolvedUtc, Is.Null);

            var recoveredAt = new DateTimeOffset(2026, 7, 10, 4, 5, 0, TimeSpan.Zero);
            await service.RecordActionAsync(fault, "recovered", recoveredAt, CancellationToken.None);
            page = await service.ListAsync(50, null, null, null, null, null, CancellationToken.None);
            Assert.That(page.Items, Has.Count.EqualTo(1), "actions update, never duplicate");
            Assert.That(page.Items[0].ActionTaken, Is.EqualTo("recovered"));
            Assert.That(page.Items[0].ResolvedUtc, Is.EqualTo(recoveredAt));
        }

        [Test]
        public async Task An_action_landing_before_detection_creates_the_row_and_detection_noops() {
            var fault = Fault();
            // Detection persists fire-and-forget off the hub, so the reaction's first
            // action can win the race — the row is created with the action stamped...
            await service.RecordActionAsync(fault, "sequence_paused", null, CancellationToken.None);
            // ...and the late detection insert must not create a second row.
            await service.RecordFaultAsync(fault, CancellationToken.None);

            var page = await service.ListAsync(50, null, null, null, null, null, CancellationToken.None);
            Assert.That(page.Items, Has.Count.EqualTo(1));
            Assert.That(page.Items[0].ActionTaken, Is.EqualTo("sequence_paused"));
            Assert.That(page.Items[0].Details, Is.EqualTo("3 probes failed"));
        }

        [Test]
        public async Task Get_returns_null_for_an_unknown_id() {
            Assert.That(await service.GetAsync(Guid.NewGuid(), CancellationToken.None), Is.Null);
        }

        [Test]
        public void The_hub_persists_every_published_fault() {
            var ws = new Mock<IWsBroadcaster>();
            var log = new Mock<IFaultLogService>();
            var hub = new EquipmentFaultHub(ws.Object, logger: null, faultLog: log.Object);
            var fault = Fault();

            hub.Publish(fault);

            log.Verify(l => l.RecordFaultAsync(fault, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public void A_failing_fault_log_never_breaks_the_publish_path() {
            var ws = new Mock<IWsBroadcaster>();
            var log = new Mock<IFaultLogService>();
            log.Setup(l => l.RecordFaultAsync(It.IsAny<EquipmentFaultEvent>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("store down"));
            var hub = new EquipmentFaultHub(ws.Object, logger: null, faultLog: log.Object);

            Assert.DoesNotThrow(() => hub.Publish(Fault()));
            ws.Verify(w => w.PublishAsync("equipment.fault", It.IsAny<System.Text.Json.JsonElement>(), It.IsAny<CancellationToken>()),
                Times.Once, "the WS broadcast still goes out");
        }

        [Test]
        public async Task A_recovered_reaction_episode_resolves_the_fault_row() {
            var log = new Mock<IFaultLogService>();
            var recorded = new List<(string Action, DateTimeOffset? ResolvedUtc)>();
            log.Setup(l => l.RecordActionAsync(It.IsAny<EquipmentFaultEvent>(), It.IsAny<string>(),
                    It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
                .Callback<EquipmentFaultEvent, string, DateTimeOffset?, CancellationToken>(
                    (_, a, r, _) => { lock (recorded) { recorded.Add((a, r)); } })
                .Returns(Task.CompletedTask);
            var reconnector = new Mock<IEquipmentReconnector>();
            reconnector.Setup(r => r.ReconnectAsync(DeviceType.Camera, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ReconnectOutcome(1, 1));
            reconnector.Setup(r => r.GetConnectionStateAsync(DeviceType.Camera, It.IsAny<CancellationToken>()))
                .ReturnsAsync(EquipmentConnectionState.Connected);
            using var reaction = new FaultReactionService(
                hub: null, reconnector.Object, faultLog: log.Object) {
                DelayShaper = _ => TimeSpan.Zero,
                ConnectConfirmTimeout = TimeSpan.FromMilliseconds(200),
                ConnectPollInterval = TimeSpan.FromMilliseconds(5),
            };

            reaction.OnFault(Fault());
            await reaction.WhenIdleAsync();

            Assert.That(recorded.Select(r => r.Action), Does.Contain("recovered"));
            Assert.That(recorded.Single(r => r.Action == "recovered").ResolvedUtc, Is.Not.Null,
                "recovery resolves the row");
            Assert.That(recorded.Where(r => r.Action != "recovered").Select(r => r.ResolvedUtc),
                Has.All.Null, "only recovery resolves");
        }

        [Test]
        public async Task A_gave_up_episode_stamps_the_terminal_outcome_inline() {
            var log = new Mock<IFaultLogService>();
            var recorded = new List<string>();
            log.Setup(l => l.RecordActionAsync(It.IsAny<EquipmentFaultEvent>(), It.IsAny<string>(),
                    It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
                .Callback<EquipmentFaultEvent, string, DateTimeOffset?, CancellationToken>(
                    (_, a, _, _) => { lock (recorded) { recorded.Add(a); } })
                .Returns(Task.CompletedTask);
            var reconnector = new Mock<IEquipmentReconnector>();
            reconnector.Setup(r => r.ReconnectAsync(DeviceType.Camera, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ReconnectOutcome(1, 1));
            reconnector.Setup(r => r.GetConnectionStateAsync(DeviceType.Camera, It.IsAny<CancellationToken>()))
                .ReturnsAsync(EquipmentConnectionState.Error);
            using var reaction = new FaultReactionService(
                hub: null, reconnector.Object, faultLog: log.Object) {
                DelayShaper = _ => TimeSpan.Zero,
                ConnectConfirmTimeout = TimeSpan.FromMilliseconds(200),
                ConnectPollInterval = TimeSpan.FromMilliseconds(5),
            };

            reaction.OnFault(Fault());
            await reaction.WhenIdleAsync();

            Assert.That(recorded.Last(), Is.EqualTo("gave_up:pause_sequence"),
                "the stored action carries the terminal outcome inline");
        }
    }
}
