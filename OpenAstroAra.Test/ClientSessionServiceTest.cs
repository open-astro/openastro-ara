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
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>§27 single-client policy: the connect handshake (free slot / takeover
    /// dance / dead-holder sweep), liveness bookkeeping, and the 4004 kick — all
    /// against a fake WS connection, no sockets. Timeout knobs are shrunk to
    /// milliseconds so the unresponsive paths run fast.</summary>
    [TestFixture]
    public class ClientSessionServiceTest {

        private sealed class FakeConnection : IWsClientConnection {
            public List<string> SentFrames { get; } = new();
            public int? CloseCode { get; private set; }
            public string? CloseReason { get; private set; }
            public bool FailSends { get; set; }

            public Task SendTextAsync(byte[] utf8Json, CancellationToken ct) {
                if (FailSends) {
                    throw new InvalidOperationException("socket gone");
                }
                lock (SentFrames) {
                    SentFrames.Add(Encoding.UTF8.GetString(utf8Json));
                }
                return Task.CompletedTask;
            }

            public Task CloseAsync(int closeCode, string reason, CancellationToken ct) {
                CloseCode = closeCode;
                CloseReason = reason;
                return Task.CompletedTask;
            }

            public string LastFrame() {
                lock (SentFrames) {
                    return SentFrames[^1];
                }
            }
        }

        private static ClientSessionService NewService(DateTimeOffset? now = null) {
            var svc = new ClientSessionService {
                RequestTimeout = TimeSpan.FromMilliseconds(200),
                DeadAfter = TimeSpan.FromSeconds(60),
            };
            if (now is not null) {
                svc.UtcNow = () => now.Value;
            }
            return svc;
        }

        private static string RequestIdOf(FakeConnection holder) {
            using var doc = JsonDocument.Parse(holder.LastFrame());
            return doc.RootElement.GetProperty("request_id").GetString()!;
        }

        // ── free slot ───────────────────────────────────────────────────────

        [Test]
        public async Task Connect_on_free_slot_is_granted_immediately() {
            var svc = NewService();
            var outcome = await svc.ConnectAsync("mac.local", null, CancellationToken.None);

            Assert.That(outcome.Kind, Is.EqualTo(ConnectOutcomeKind.Granted));
            Assert.That(outcome.SessionId, Is.Not.EqualTo(Guid.Empty));
            var session = svc.GetSession();
            Assert.That(session.Connected, Is.True);
            Assert.That(session.Hostname, Is.EqualTo("mac.local"));
        }

        [Test]
        public void GetSession_with_no_client_reports_not_connected() {
            var session = NewService().GetSession();
            Assert.That(session.Connected, Is.False);
            Assert.That(session.Hostname, Is.Null);
            Assert.That(session.ConnectedAt, Is.Null);
            Assert.That(session.IdleSeconds, Is.Null);
        }

        // ── takeover dance ──────────────────────────────────────────────────

        [Test]
        public async Task Takeover_allow_grants_new_client_and_closes_old_socket_with_4004() {
            var svc = NewService();
            var holderConn = new FakeConnection();
            var holder = await svc.ConnectAsync("mac.local", null, CancellationToken.None);
            Assert.That(svc.BindSocket(holder.SessionId, holderConn), Is.True);

            var connectTask = svc.ConnectAsync("ipad.local", null, CancellationToken.None);
            // The holder receives the connection.request frame with the new hostname.
            await WaitForFrameAsync(holderConn);
            using (var doc = JsonDocument.Parse(holderConn.LastFrame())) {
                Assert.That(doc.RootElement.GetProperty("type").GetString(), Is.EqualTo("connection.request"));
                Assert.That(doc.RootElement.GetProperty("from").GetString(), Is.EqualTo("ipad.local"));
            }

            Assert.That(svc.TryCompleteTakeover(RequestIdOf(holderConn), "allow"), Is.True);
            var outcome = await connectTask;

            Assert.That(outcome.Kind, Is.EqualTo(ConnectOutcomeKind.Granted));
            Assert.That(outcome.SessionId, Is.Not.EqualTo(holder.SessionId));
            Assert.That(svc.GetSession().Hostname, Is.EqualTo("ipad.local"));
            // The displaced holder's socket got the §60.9 close code 4004.
            await WaitUntilAsync(() => holderConn.CloseCode is not null);
            Assert.That(holderConn.CloseCode, Is.EqualTo(ClientSessionService.TakeoverCloseCode));
            Assert.That(holderConn.CloseReason, Does.Contain("ipad.local"));
        }

        [Test]
        public async Task Takeover_reject_returns_rejected_with_holder_hostname_and_keeps_holder() {
            var svc = NewService();
            var holderConn = new FakeConnection();
            var holder = await svc.ConnectAsync("mac.local", null, CancellationToken.None);
            svc.BindSocket(holder.SessionId, holderConn);

            var connectTask = svc.ConnectAsync("ipad.local", null, CancellationToken.None);
            await WaitForFrameAsync(holderConn);
            svc.TryCompleteTakeover(RequestIdOf(holderConn), "reject");
            var outcome = await connectTask;

            Assert.That(outcome.Kind, Is.EqualTo(ConnectOutcomeKind.Rejected));
            Assert.That(outcome.CurrentHostname, Is.EqualTo("mac.local"));
            Assert.That(svc.GetSession().Hostname, Is.EqualTo("mac.local"), "holder keeps the slot");
            Assert.That(holderConn.CloseCode, Is.Null, "holder's socket must not be closed");
        }

        [Test]
        public async Task Takeover_timeout_returns_unresponsive_and_keeps_holder() {
            var svc = NewService();
            var holderConn = new FakeConnection();
            var holder = await svc.ConnectAsync("mac.local", null, CancellationToken.None);
            svc.BindSocket(holder.SessionId, holderConn);

            var outcome = await svc.ConnectAsync("ipad.local", null, CancellationToken.None);

            Assert.That(outcome.Kind, Is.EqualTo(ConnectOutcomeKind.Unresponsive));
            Assert.That(outcome.CurrentHostname, Is.EqualTo("mac.local"));
            Assert.That(svc.GetSession().Hostname, Is.EqualTo("mac.local"));
        }

        [Test]
        public async Task Takeover_send_failure_returns_unresponsive_without_granting() {
            var svc = NewService();
            var holderConn = new FakeConnection { FailSends = true };
            var holder = await svc.ConnectAsync("mac.local", null, CancellationToken.None);
            svc.BindSocket(holder.SessionId, holderConn);

            var outcome = await svc.ConnectAsync("ipad.local", null, CancellationToken.None);

            Assert.That(outcome.Kind, Is.EqualTo(ConnectOutcomeKind.Unresponsive));
            Assert.That(svc.GetSession().Hostname, Is.EqualTo("mac.local"));
        }

        [Test]
        public async Task Second_concurrent_connect_during_pending_takeover_is_busy() {
            var svc = NewService();
            var holderConn = new FakeConnection();
            var holder = await svc.ConnectAsync("mac.local", null, CancellationToken.None);
            svc.BindSocket(holder.SessionId, holderConn);

            var first = svc.ConnectAsync("ipad.local", null, CancellationToken.None);
            await WaitForFrameAsync(holderConn);
            var second = await svc.ConnectAsync("phone.local", null, CancellationToken.None);
            Assert.That(second.Kind, Is.EqualTo(ConnectOutcomeKind.Busy));

            svc.TryCompleteTakeover(RequestIdOf(holderConn), "reject");
            await first;

            // Pending cleared → a later attempt gets the full dance again (times out here).
            var third = await svc.ConnectAsync("phone.local", null, CancellationToken.None);
            Assert.That(third.Kind, Is.EqualTo(ConnectOutcomeKind.Unresponsive),
                "after the pending request resolves, the next attempt is no longer Busy");
        }

        [Test]
        public async Task Stale_or_wrong_request_id_does_not_complete_takeover() {
            var svc = NewService();
            var holderConn = new FakeConnection();
            var holder = await svc.ConnectAsync("mac.local", null, CancellationToken.None);
            svc.BindSocket(holder.SessionId, holderConn);

            Assert.That(svc.TryCompleteTakeover("nope", "allow"), Is.False, "no pending request at all");

            var connectTask = svc.ConnectAsync("ipad.local", null, CancellationToken.None);
            await WaitForFrameAsync(holderConn);
            Assert.That(svc.TryCompleteTakeover("wrong-id", "allow"), Is.False);
            var outcome = await connectTask;
            Assert.That(outcome.Kind, Is.EqualTo(ConnectOutcomeKind.Unresponsive),
                "a wrong request id must not grant the slot");
        }

        // ── idempotent re-claim ─────────────────────────────────────────────

        [Test]
        public async Task Reclaim_with_own_session_id_regrants_without_a_dance() {
            var svc = NewService();
            var holderConn = new FakeConnection();
            var holder = await svc.ConnectAsync("mac.local", null, CancellationToken.None);
            svc.BindSocket(holder.SessionId, holderConn);

            // Same client reconnecting after a Wi-Fi blip: presents its session id.
            var reclaim = await svc.ConnectAsync("mac.local", holder.SessionId, CancellationToken.None);

            Assert.That(reclaim.Kind, Is.EqualTo(ConnectOutcomeKind.Granted));
            Assert.That(reclaim.SessionId, Is.EqualTo(holder.SessionId), "same session, not a new one");
            Assert.That(holderConn.SentFrames, Is.Empty, "no connection.request modal for a re-claim");
            Assert.That(holderConn.CloseCode, Is.Null);
        }

        [Test]
        public async Task Reclaim_revives_a_dead_but_unclaimed_session() {
            var now = new DateTimeOffset(2026, 7, 6, 2, 0, 0, TimeSpan.Zero);
            var svc = NewService(now);
            var holder = await svc.ConnectAsync("mac.local", null, CancellationToken.None);

            // 90 s of silence — past DeadAfter, but nobody else claimed the slot.
            svc.UtcNow = () => now.AddSeconds(90);
            var reclaim = await svc.ConnectAsync("mac.local", holder.SessionId, CancellationToken.None);

            Assert.That(reclaim.Kind, Is.EqualTo(ConnectOutcomeKind.Granted));
            Assert.That(reclaim.SessionId, Is.EqualTo(holder.SessionId));
            Assert.That(svc.GetSession().Connected, Is.True, "re-claim refreshed liveness");
        }

        [Test]
        public async Task Stale_session_id_after_takeover_goes_through_the_normal_dance() {
            var now = new DateTimeOffset(2026, 7, 6, 2, 0, 0, TimeSpan.Zero);
            var svc = NewService(now);
            var oldHolder = await svc.ConnectAsync("mac.local", null, CancellationToken.None);
            // mac.local goes silent; ipad.local sweeps the dead slot.
            svc.UtcNow = () => now.AddSeconds(61);
            var newHolder = await svc.ConnectAsync("ipad.local", null, CancellationToken.None);
            Assert.That(newHolder.Kind, Is.EqualTo(ConnectOutcomeKind.Granted));

            // mac.local comes back presenting its OLD id — that's a foreign connect
            // now (holder has no bound socket and is fresh, so: unresponsive).
            var comeback = await svc.ConnectAsync("mac.local", oldHolder.SessionId, CancellationToken.None);
            Assert.That(comeback.Kind, Is.EqualTo(ConnectOutcomeKind.Unresponsive));
            Assert.That(svc.GetSession().Hostname, Is.EqualTo("ipad.local"), "the thief keeps the slot");
        }

        // ── liveness / dead holder ──────────────────────────────────────────

        [Test]
        public async Task Holder_with_no_bound_socket_within_grace_is_unresponsive_then_dead_after_60s() {
            var now = new DateTimeOffset(2026, 7, 6, 2, 0, 0, TimeSpan.Zero);
            var svc = NewService(now);
            await svc.ConnectAsync("mac.local", null, CancellationToken.None);
            // No WS ever bound. 30 s in: still within the DeadAfter grace.
            svc.UtcNow = () => now.AddSeconds(30);
            var early = await svc.ConnectAsync("ipad.local", null, CancellationToken.None);
            Assert.That(early.Kind, Is.EqualTo(ConnectOutcomeKind.Unresponsive));

            // 61 s in: dead — the slot sweeps to the new client with no dance.
            svc.UtcNow = () => now.AddSeconds(61);
            var late = await svc.ConnectAsync("ipad.local", null, CancellationToken.None);
            Assert.That(late.Kind, Is.EqualTo(ConnectOutcomeKind.Granted));
            Assert.That(svc.GetSession().Hostname, Is.EqualTo("ipad.local"));
        }

        [Test]
        public async Task Silent_holder_with_bound_socket_goes_dead_and_gets_4004_kicked() {
            var now = new DateTimeOffset(2026, 7, 6, 2, 0, 0, TimeSpan.Zero);
            var svc = NewService(now);
            var holderConn = new FakeConnection();
            var holder = await svc.ConnectAsync("mac.local", null, CancellationToken.None);
            svc.BindSocket(holder.SessionId, holderConn);

            // A frozen app whose OS-level socket stays open: no frames for > 60 s.
            svc.UtcNow = () => now.AddSeconds(61);
            var outcome = await svc.ConnectAsync("ipad.local", null, CancellationToken.None);

            Assert.That(outcome.Kind, Is.EqualTo(ConnectOutcomeKind.Granted));
            await WaitUntilAsync(() => holderConn.CloseCode is not null);
            Assert.That(holderConn.CloseCode, Is.EqualTo(4004), "zombie socket must be kicked");
        }

        [Test]
        public async Task RecordActivity_keeps_the_holder_alive() {
            var now = new DateTimeOffset(2026, 7, 6, 2, 0, 0, TimeSpan.Zero);
            var svc = NewService(now);
            var holderConn = new FakeConnection();
            var holder = await svc.ConnectAsync("mac.local", null, CancellationToken.None);
            svc.BindSocket(holder.SessionId, holderConn);

            // Pong at t+50s resets the silence window.
            svc.UtcNow = () => now.AddSeconds(50);
            svc.RecordActivity(holder.SessionId);
            svc.UtcNow = () => now.AddSeconds(100); // only 50 s since the pong
            var outcome = await svc.ConnectAsync("ipad.local", null, CancellationToken.None);

            Assert.That(outcome.Kind, Is.Not.EqualTo(ConnectOutcomeKind.Granted),
                "a ponging holder must not be swept");
            Assert.That(svc.GetSession().Hostname, Is.EqualTo("mac.local"));
        }

        [Test]
        public async Task Unbind_freezes_last_seen_then_rebind_revives() {
            var now = new DateTimeOffset(2026, 7, 6, 2, 0, 0, TimeSpan.Zero);
            var svc = NewService(now);
            var conn1 = new FakeConnection();
            var holder = await svc.ConnectAsync("mac.local", null, CancellationToken.None);
            svc.BindSocket(holder.SessionId, conn1);

            // Wi-Fi blip at t+10s: socket unbinds; the 60 s countdown starts there.
            svc.UtcNow = () => now.AddSeconds(10);
            svc.UnbindSocket(holder.SessionId, conn1);

            // Client reconnects (resume protocol) at t+40s with a new socket.
            svc.UtcNow = () => now.AddSeconds(40);
            var conn2 = new FakeConnection();
            Assert.That(svc.BindSocket(holder.SessionId, conn2), Is.True, "same session re-binds after a drop");

            // t+90s: only 50 s since the re-bind — still alive.
            svc.UtcNow = () => now.AddSeconds(90);
            Assert.That(svc.GetSession().Connected, Is.True);
        }

        [Test]
        public async Task Stale_socket_unbind_does_not_detach_the_replacement() {
            var svc = NewService();
            var holder = await svc.ConnectAsync("mac.local", null, CancellationToken.None);
            var oldConn = new FakeConnection();
            var newConn = new FakeConnection();
            svc.BindSocket(holder.SessionId, oldConn);
            svc.BindSocket(holder.SessionId, newConn);

            // The old socket's teardown finally-block runs late.
            svc.UnbindSocket(holder.SessionId, oldConn);

            // The takeover dance still reaches the (still-bound) new socket.
            var connectTask = svc.ConnectAsync("ipad.local", null, CancellationToken.None);
            await WaitForFrameAsync(newConn);
            svc.TryCompleteTakeover(RequestIdOf(newConn), "reject");
            var outcome = await connectTask;
            Assert.That(outcome.Kind, Is.EqualTo(ConnectOutcomeKind.Rejected));
        }

        [Test]
        public async Task BindSocket_with_unknown_session_id_is_refused() {
            var svc = NewService();
            await svc.ConnectAsync("mac.local", null, CancellationToken.None);
            Assert.That(svc.BindSocket(Guid.NewGuid(), new FakeConnection()), Is.False);
        }

        // ── disconnect ──────────────────────────────────────────────────────

        [Test]
        public async Task Disconnect_releases_the_slot_only_for_the_owning_session() {
            var svc = NewService();
            var holder = await svc.ConnectAsync("mac.local", null, CancellationToken.None);

            Assert.That(svc.Disconnect(Guid.NewGuid()), Is.False, "wrong id must not release");
            Assert.That(svc.GetSession().Connected, Is.True);

            Assert.That(svc.Disconnect(holder.SessionId), Is.True);
            Assert.That(svc.GetSession().Connected, Is.False);

            var next = await svc.ConnectAsync("ipad.local", null, CancellationToken.None);
            Assert.That(next.Kind, Is.EqualTo(ConnectOutcomeKind.Granted));
        }

        [Test]
        public async Task Disconnect_during_pending_takeover_grants_the_waiting_connector() {
            var svc = NewService();
            var holderConn = new FakeConnection();
            var holder = await svc.ConnectAsync("mac.local", null, CancellationToken.None);
            svc.BindSocket(holder.SessionId, holderConn);

            var connectTask = svc.ConnectAsync("ipad.local", null, CancellationToken.None);
            await WaitForFrameAsync(holderConn);
            // The holder quits gracefully instead of answering the modal.
            Assert.That(svc.Disconnect(holder.SessionId), Is.True);

            var outcome = await connectTask;
            Assert.That(outcome.Kind, Is.EqualTo(ConnectOutcomeKind.Granted));
            Assert.That(svc.GetSession().Hostname, Is.EqualTo("ipad.local"));
        }

        // ── client frame parsing (WS receive-loop helper) ───────────────────

        [Test]
        public async Task HandleClientFrame_routes_connection_response_to_the_pending_takeover() {
            var svc = NewService();
            var holderConn = new FakeConnection();
            var holder = await svc.ConnectAsync("mac.local", null, CancellationToken.None);
            svc.BindSocket(holder.SessionId, holderConn);

            var connectTask = svc.ConnectAsync("ipad.local", null, CancellationToken.None);
            await WaitForFrameAsync(holderConn);
            var frame = $"{{\"type\":\"connection.response\",\"request_id\":\"{RequestIdOf(holderConn)}\",\"action\":\"allow\"}}";
            WebSocketEndpoints.HandleClientFrame(Encoding.UTF8.GetBytes(frame), svc);

            var outcome = await connectTask;
            Assert.That(outcome.Kind, Is.EqualTo(ConnectOutcomeKind.Granted));
        }

        [TestCase("{\"type\":\"pong\"}", TestName = "HandleClientFrame_ignores_pong")]
        [TestCase("{\"type\":\"connection.response\",\"request_id\":\"x\",\"action\":\"detonate\"}", TestName = "HandleClientFrame_ignores_unknown_action")]
        [TestCase("{\"type\":\"connection.response\",\"action\":\"allow\"}", TestName = "HandleClientFrame_ignores_missing_request_id")]
        [TestCase("{\"type\":\"mystery.frame\",\"data\":42}", TestName = "HandleClientFrame_ignores_unknown_type")]
        [TestCase("not json at all", TestName = "HandleClientFrame_ignores_non_json")]
        [TestCase("", TestName = "HandleClientFrame_ignores_empty")]
        [TestCase("null", TestName = "HandleClientFrame_ignores_json_null")]
        public async Task HandleClientFrame_is_permissive(string payload) {
            var svc = NewService();
            var holderConn = new FakeConnection();
            var holder = await svc.ConnectAsync("mac.local", null, CancellationToken.None);
            svc.BindSocket(holder.SessionId, holderConn);
            var connectTask = svc.ConnectAsync("ipad.local", null, CancellationToken.None);
            await WaitForFrameAsync(holderConn);

            Assert.DoesNotThrow(() =>
                WebSocketEndpoints.HandleClientFrame(Encoding.UTF8.GetBytes(payload), svc));

            // None of these frames may resolve the dance — it times out instead.
            var outcome = await connectTask;
            Assert.That(outcome.Kind, Is.EqualTo(ConnectOutcomeKind.Unresponsive));
        }

        // ── close-reason truncation ─────────────────────────────────────────

        [Test]
        public void Close_reason_truncation_respects_the_rfc6455_byte_cap() {
            Assert.That(WsClientConnection.Truncate("short reason"), Is.EqualTo("short reason"));

            var longAscii = new string('a', 200);
            var truncated = WsClientConnection.Truncate(longAscii);
            Assert.That(Encoding.UTF8.GetByteCount(truncated), Is.LessThanOrEqualTo(123));

            // Multi-byte hostnames must not be split mid-sequence (would mojibake).
            var emoji = string.Concat(System.Linq.Enumerable.Repeat("🔭", 40)); // 4 bytes each
            var emojiTruncated = WsClientConnection.Truncate(emoji);
            Assert.That(Encoding.UTF8.GetByteCount(emojiTruncated), Is.LessThanOrEqualTo(123));
            Assert.That(emojiTruncated.Length % 2, Is.EqualTo(0), "no dangling half surrogate pair");
        }

        // ── helpers ─────────────────────────────────────────────────────────

        private static async Task WaitForFrameAsync(FakeConnection conn) {
            await WaitUntilAsync(() => {
                lock (conn.SentFrames) {
                    return conn.SentFrames.Count > 0;
                }
            });
        }

        private static async Task WaitUntilAsync(Func<bool> condition) {
            for (var i = 0; i < 200; i++) {
                if (condition()) return;
                await Task.Delay(10);
            }
            Assert.Fail("condition not met within 2s");
        }
    }
}
