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

        private async Task<Guid> InsertFrameAsync(Guid sessionId, DateTimeOffset capturedUtc, double exposureSec) {
            var id = Guid.NewGuid();
            await using var conn = db.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO frames
                    (id, session_id, target_name, frame_type, exposure_seconds, captured_utc,
                     file_path, file_size_bytes, width, height, bit_depth)
                VALUES
                    ($id, $session, 'M42', 'light', $exp, $captured, $path, 1, 100, 100, 16);
                """;
            cmd.Parameters.AddWithValue("$id", id.ToString());
            cmd.Parameters.AddWithValue("$session", sessionId.ToString());
            cmd.Parameters.AddWithValue("$exp", exposureSec);
            cmd.Parameters.AddWithValue("$captured", capturedUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$path", $"/tmp/{id}.fits");
            await cmd.ExecuteNonQueryAsync();
            return id;
        }

        // §42.6 — the default Fault() detects at 04:00Z; a 10-minute outage window against a
        // spread of exposures probing every overlap edge.
        private static readonly DateTimeOffset Detected = new(2026, 7, 10, 4, 0, 0, TimeSpan.Zero);
        private static readonly DateTimeOffset Resolved = Detected + TimeSpan.FromMinutes(10);

        private async Task<(Guid Spanning, Guid Inside, Guid Straddling, Guid Before, Guid After)> InsertOverlapSpreadAsync(Guid sessionId) => (
            Spanning: await InsertFrameAsync(sessionId, Detected - TimeSpan.FromSeconds(60), 120),   // in flight when the fault hit
            Inside: await InsertFrameAsync(sessionId, Detected + TimeSpan.FromMinutes(5), 60),       // wholly inside the window
            Straddling: await InsertFrameAsync(sessionId, Resolved - TimeSpan.FromSeconds(30), 300), // started before the resolve
            Before: await InsertFrameAsync(sessionId, Detected - TimeSpan.FromMinutes(3), 60),       // ended before the fault
            After: await InsertFrameAsync(sessionId, Resolved + TimeSpan.FromMinutes(1), 60));       // started after the resolve

        [Test]
        public async Task A_recovered_resolution_correlates_the_frames_whose_exposure_overlapped_the_window() {
            var sessionId = await CreateSessionRowAsync();
            var frames = await InsertOverlapSpreadAsync(sessionId);
            var fault = Fault(DeviceType.Telescope, detectedUtc: Detected);
            registry.Enter(sessionId);
            try {
                await service.RecordFaultAsync(fault, CancellationToken.None);
            } finally {
                registry.Exit(sessionId);
            }

            await service.RecordActionAsync(fault, "recovered", Resolved, CancellationToken.None);

            var row = (await service.ListAsync(50, null, null, null, null, null, CancellationToken.None)).Items[0];
            Assert.That(row.AffectedFrames,
                Is.EquivalentTo(new[] { frames.Spanning, frames.Inside, frames.Straddling }),
                "exactly the frames whose exposure window overlapped [detected, resolved]");
        }

        [Test]
        public async Task An_action_created_row_with_a_resolution_correlates_too() {
            // The reaction's action can land before detection (insert path) — a resolution
            // stamped there must correlate exactly like the update path.
            var sessionId = await CreateSessionRowAsync();
            var frames = await InsertOverlapSpreadAsync(sessionId);
            registry.Enter(sessionId);
            try {
                await service.RecordActionAsync(Fault(detectedUtc: Detected), "recovered", Resolved, CancellationToken.None);
            } finally {
                registry.Exit(sessionId);
            }

            var row = (await service.ListAsync(50, null, null, null, null, null, CancellationToken.None)).Items[0];
            Assert.That(row.AffectedFrames,
                Is.EquivalentTo(new[] { frames.Spanning, frames.Inside, frames.Straddling }));
        }

        [Test]
        public async Task A_reconnect_resolution_correlates_frames_too() {
            var sessionId = await CreateSessionRowAsync();
            var frames = await InsertOverlapSpreadAsync(sessionId);
            registry.Enter(sessionId);
            try {
                await service.RecordFaultAsync(Fault(DeviceType.Focuser, detectedUtc: Detected), CancellationToken.None);
            } finally {
                registry.Exit(sessionId);
            }

            await service.ResolveOnReconnectAsync(DeviceType.Focuser, Resolved, CancellationToken.None);

            var row = (await service.ListAsync(50, null, null, null, null, null, CancellationToken.None)).Items[0];
            Assert.That(row.AffectedFrames,
                Is.EquivalentTo(new[] { frames.Spanning, frames.Inside, frames.Straddling }),
                "the §42.5 reconnect hook is a resolution like any other");
        }

        [Test]
        public async Task Correlation_is_scoped_to_the_faults_session() {
            var faultSession = await CreateSessionRowAsync();
            var otherSession = await CreateSessionRowAsync();
            await InsertFrameAsync(otherSession, Detected + TimeSpan.FromMinutes(1), 60); // would overlap, wrong session
            var inSession = await InsertFrameAsync(faultSession, Detected + TimeSpan.FromMinutes(1), 60);
            var fault = Fault(detectedUtc: Detected);
            registry.Enter(faultSession);
            try {
                await service.RecordFaultAsync(fault, CancellationToken.None);
            } finally {
                registry.Exit(faultSession);
            }

            await service.RecordActionAsync(fault, "recovered", Resolved, CancellationToken.None);

            var row = (await service.ListAsync(50, null, null, null, null, null, CancellationToken.None)).Items[0];
            Assert.That(row.AffectedFrames, Is.EquivalentTo(new[] { inSession }),
                "another run's frames are not this fault's business");
        }

        [Test]
        public async Task A_fault_outside_a_run_and_a_non_resolving_action_never_correlate() {
            var sessionId = await CreateSessionRowAsync();
            await InsertOverlapSpreadAsync(sessionId);

            // No session on the fault row: nothing to correlate against.
            var outsideRun = Fault(detectedUtc: Detected);
            await service.RecordFaultAsync(outsideRun, CancellationToken.None);
            await service.RecordActionAsync(outsideRun, "recovered", Resolved, CancellationToken.None);

            // Session attributed, but the action carries no resolution.
            var mid = Fault(DeviceType.Switch, kind: EquipmentFaultKind.ValueMismatch, detectedUtc: Detected);
            registry.Enter(sessionId);
            try {
                await service.RecordFaultAsync(mid, CancellationToken.None);
            } finally {
                registry.Exit(sessionId);
            }
            await service.RecordActionAsync(mid, "notify_only", resolvedUtc: null, CancellationToken.None);

            var page = await service.ListAsync(50, null, null, null, null, null, CancellationToken.None);
            Assert.That(page.Items.Select(i => i.AffectedFrames.Count), Is.All.Zero,
                "no session → no frame set; no resolution → the window never closed");
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
            Assert.That(row.AffectedFrames, Is.Empty, "affected_frames fills at resolve time (§42.6), not detection");

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
        public async Task Reconnect_resolves_only_that_types_unresolved_disconnect_rows() {
            var t0 = new DateTimeOffset(2026, 7, 10, 3, 0, 0, TimeSpan.Zero);
            await service.RecordFaultAsync(Fault(DeviceType.Camera, EquipmentFaultKind.Disconnected, t0), CancellationToken.None);
            await service.RecordActionAsync(Fault(DeviceType.Camera, EquipmentFaultKind.Disconnected, t0),
                "gave_up:pause_sequence", null, CancellationToken.None);
            await service.RecordFaultAsync(Fault(DeviceType.Camera, EquipmentFaultKind.OpError, t0.AddMinutes(1)), CancellationToken.None);
            await service.RecordFaultAsync(Fault(DeviceType.Telescope, EquipmentFaultKind.Disconnected, t0.AddMinutes(2)), CancellationToken.None);
            var alreadyResolved = t0.AddMinutes(3);
            await service.RecordFaultAsync(Fault(DeviceType.Camera, EquipmentFaultKind.Disconnected, alreadyResolved), CancellationToken.None);
            await service.RecordActionAsync(Fault(DeviceType.Camera, EquipmentFaultKind.Disconnected, alreadyResolved),
                "recovered", alreadyResolved.AddMinutes(1), CancellationToken.None);

            var reconnectAt = t0.AddHours(1);
            var resolved = await service.ResolveOnReconnectAsync(DeviceType.Camera, reconnectAt, CancellationToken.None);
            Assert.That(resolved, Is.EqualTo(1), "only the camera's unresolved disconnect row resolves");

            var all = await service.ListAsync(50, null, null, null, null, null, CancellationToken.None);
            var gaveUp = all.Items.Single(f => f.ActionTaken == "gave_up:pause_sequence");
            Assert.That(gaveUp.ResolvedUtc, Is.EqualTo(reconnectAt), "the manual-fix row is resolved by the reconnect");
            Assert.That(gaveUp.ActionTaken, Is.EqualTo("gave_up:pause_sequence"),
                "the reaction outcome is history, not rewritten by the resolution");
            Assert.That(all.Items.Single(f => f.FaultType == "op_error").ResolvedUtc, Is.Null,
                "advisories are one-shots — a connect doesn't retract them");
            Assert.That(all.Items.Single(f => f.EquipmentType == "telescope").ResolvedUtc, Is.Null,
                "another device's rows are untouched");
            Assert.That(all.Items.Single(f => f.ActionTaken == "recovered").ResolvedUtc,
                Is.EqualTo(alreadyResolved.AddMinutes(1)), "an already-resolved row keeps its original resolution time");
        }

        [Test]
        public void A_connected_transition_at_the_publisher_resolves_the_fault_log() {
            var ws = new Mock<IWsBroadcaster>();
            ws.Setup(w => w.PublishAsync(It.IsAny<string>(), It.IsAny<System.Text.Json.JsonElement>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            var log = new Mock<IFaultLogService>();
            var publisher = new EquipmentEventPublisher(ws.Object, logger: null, faultLog: log.Object);

            publisher.StateChanged(DeviceType.Camera, "dev-1", "Cam", EquipmentConnectionState.Connected);
            log.Verify(l => l.ResolveOnReconnectAsync(DeviceType.Camera, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);

            publisher.StateChanged(DeviceType.Camera, "dev-1", "Cam", EquipmentConnectionState.Disconnected);
            publisher.StateChanged(DeviceType.Camera, "dev-1", "Cam", EquipmentConnectionState.Error);
            log.Verify(l => l.ResolveOnReconnectAsync(It.IsAny<DeviceType>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once,
                "only the connected transition resolves");

            // One physical device under two wire tokens — either token's
            // reconnect resolves both.
            publisher.StateChanged(DeviceType.FlatDevice, "dev-2", "Panel", EquipmentConnectionState.Connected);
            log.Verify(l => l.ResolveOnReconnectAsync(DeviceType.FlatDevice, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
            log.Verify(l => l.ResolveOnReconnectAsync(DeviceType.CoverCalibrator, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
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
