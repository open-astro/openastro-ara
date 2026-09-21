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
using OpenAstroAra.Core.Model.Equipment;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeviceType = OpenAstroAra.Server.Contracts.DeviceType;

namespace OpenAstroAra.Test {

    /// <summary>#1066 — the first-connect home to slot 0 is daemon policy. Drives a real
    /// <see cref="FilterWheelService"/> against a loopback Alpaca wheel stub that records every
    /// <c>PUT …/position</c>, and pins the rule: the claim is taken at the wheel's first connect of
    /// the daemon session (position known or not), the decision fires on the first refresh that
    /// reads a known position — off slot 0 homes, at 0 counts as homed — within a bounded window,
    /// an explicit change retires it, and a reconnect never homes.</summary>
    [TestFixture]
    public class FilterWheelFirstConnectHomeTest {

        /// <summary>A loopback Alpaca FilterWheel: answers <c>position</c>/<c>names</c>/
        /// <c>focusoffsets</c>/<c>connected</c> with real values and records position writes.</summary>
        private sealed class StubWheel : IAsyncDisposable {
            private readonly HttpListener _listener;
            private readonly CancellationTokenSource _cts = new();
            private readonly Task _loop;
            private int _position;
            private long _namesAvailableAtTicks;

            public readonly ConcurrentQueue<int> PositionWrites = new();

            /// <summary>Until this instant the stub answers <c>names</c>/<c>focusoffsets</c> with an
            /// Alpaca error, so the service's slot read fails and <c>_slots</c> stays null (#1079).</summary>
            public DateTime NamesAvailableAt { set => Volatile.Write(ref _namesAvailableAtTicks, value.Ticks); }

            private StubWheel(HttpListener listener, int port, int position) {
                BaseUri = new Uri($"http://127.0.0.1:{port}/");
                _listener = listener;
                _position = position;
                _loop = Task.Run(LoopAsync);
            }

            public Uri BaseUri { get; }

            /// <summary>The slot the stub reports; -1 = "moving" per ASCOM.</summary>
            public int Position { set => Volatile.Write(ref _position, value); }

            public static StubWheel Start(int position) {
                var (listener, port) = OpenAstroAra.TestHarness.Net.LoopbackListener.Bind();
                return new StubWheel(listener, port, position);
            }

            private async Task LoopAsync() {
                while (!_cts.IsCancellationRequested) {
                    HttpListenerContext ctx;
                    try {
                        ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                    } catch (HttpListenerException) {
                        return;
                    } catch (ObjectDisposedException) {
                        return;
                    }
                    var leaf = ctx.Request.Url!.AbsolutePath.TrimEnd('/');
                    leaf = leaf[(leaf.LastIndexOf('/') + 1)..].ToUpperInvariant();
                    string value = "true";
                    var errorNumber = 0;
                    if (ctx.Request.HttpMethod != "PUT" && (leaf == "NAMES" || leaf == "FOCUSOFFSETS")
                            && DateTime.UtcNow.Ticks < Volatile.Read(ref _namesAvailableAtTicks)) {
                        errorNumber = 1024; // not ready yet
                    }
                    if (ctx.Request.HttpMethod == "PUT") {
                        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                        var body = await reader.ReadToEndAsync().ConfigureAwait(false);
                        if (leaf == "POSITION") {
                            foreach (var pair in body.Split('&')) {
                                var kv = pair.Split('=');
                                if (kv.Length == 2 && kv[0].Equals("Position", StringComparison.OrdinalIgnoreCase)
                                        && int.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)) {
                                    PositionWrites.Enqueue(p);
                                    Volatile.Write(ref _position, p);
                                }
                            }
                        }
                        value = "null";
                    } else {
                        value = leaf switch {
                            "POSITION" => Volatile.Read(ref _position).ToString(CultureInfo.InvariantCulture),
                            "NAMES" => "[\"L\",\"R\",\"G\",\"B\"]",
                            "FOCUSOFFSETS" => "[0,0,0,0]",
                            _ => "true",
                        };
                    }
                    var bytes = Encoding.UTF8.GetBytes(
                        $"{{\"Value\":{value},\"ClientTransactionID\":0,\"ServerTransactionID\":0,\"ErrorNumber\":{errorNumber.ToString(CultureInfo.InvariantCulture)},\"ErrorMessage\":\"{(errorNumber == 0 ? "" : "not ready")}\"}}");
                    try {
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.ContentLength64 = bytes.Length;
                        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                        ctx.Response.Close();
                    } catch (HttpListenerException) {
                    } catch (ObjectDisposedException) {
                    }
                }
            }

            public async ValueTask DisposeAsync() {
                await _cts.CancelAsync().ConfigureAwait(false);
                try { _listener.Stop(); } catch (ObjectDisposedException) { }
                _listener.Close();
                try {
                    await _loop.ConfigureAwait(false);
                } catch (HttpListenerException) {
                } catch (ObjectDisposedException) {
                }
                _cts.Dispose();
            }
        }

        private static DiscoveredDeviceDto Device(StubWheel stub, string uniqueId = "wheel-under-test") => new(
            UniqueId: uniqueId, Name: "Bench Wheel", Type: DeviceType.FilterWheel,
            HostName: stub.BaseUri.Host, IpAddress: stub.BaseUri.Host, IpPort: stub.BaseUri.Port,
            AlpacaDeviceNumber: 0, UseHttps: false);

        private static async Task WaitForStateAsync(FilterWheelService svc, EquipmentConnectionState state, TimeSpan timeout) {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline) {
                var dto = await svc.GetAsync(CancellationToken.None);
                if (dto?.State == state) {
                    return;
                }
                await Task.Delay(50);
            }
            Assert.Fail($"filter wheel never reached {state}");
        }

        private static async Task<bool> WaitForWriteAsync(StubWheel stub, TimeSpan timeout) {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline) {
                if (!stub.PositionWrites.IsEmpty) {
                    return true;
                }
                await Task.Delay(50);
            }
            return false;
        }

        private static async Task ConnectAsync(FilterWheelService svc, StubWheel stub, string uniqueId = "wheel-under-test") {
            await svc.ConnectAsync(new ConnectRequestDto(Device(stub, uniqueId)), idempotencyKey: null, CancellationToken.None);
            await WaitForStateAsync(svc, EquipmentConnectionState.Connected, TimeSpan.FromSeconds(15));
        }

        private static async Task DisconnectAsync(FilterWheelService svc) {
            await svc.DisconnectAsync(idempotencyKey: null, CancellationToken.None);
            await WaitForStateAsync(svc, EquipmentConnectionState.Disconnected, TimeSpan.FromSeconds(15));
        }

        [Test]
        [Category("bench")] // loopback-only, runs in the default job too
        public async Task First_connect_off_slot_0_homes_and_a_reconnect_never_does() {
            await using var stub = StubWheel.Start(position: 3);
            using var svc = new FilterWheelService();

            await ConnectAsync(svc, stub);
            Assert.That(await WaitForWriteAsync(stub, TimeSpan.FromSeconds(10)), Is.True, "first connect must home the wheel");
            Assert.That(stub.PositionWrites.TryDequeue(out var target) && target == FilterWheelService.DefaultSlot, Is.True,
                "the home goes to the default slot");

            // Park it elsewhere while offline, reconnect: NO home — a reconnect must never yank a
            // running sequence's filter back to L.
            await DisconnectAsync(svc);
            stub.Position = 2;
            await ConnectAsync(svc, stub);
            Assert.That(await WaitForWriteAsync(stub, TimeSpan.FromSeconds(3)), Is.False, "a reconnect never re-homes");
            await DisconnectAsync(svc);
        }

        [Test]
        [Category("bench")]
        public async Task Already_at_slot_0_counts_as_homed_and_a_moving_wheel_is_homed_once_its_position_is_known() {
            await using var stub = StubWheel.Start(position: FilterWheelService.DefaultSlot);
            using var svc = new FilterWheelService();

            // Already at 0: no move, but the session's home decision is settled…
            await ConnectAsync(svc, stub);
            Assert.That(await WaitForWriteAsync(stub, TimeSpan.FromSeconds(3)), Is.False, "already at 0 — nothing to move");
            await DisconnectAsync(svc);
            stub.Position = 3;
            await ConnectAsync(svc, stub);
            Assert.That(await WaitForWriteAsync(stub, TimeSpan.FromSeconds(3)), Is.False, "…so a later reconnect never re-homes");
            await DisconnectAsync(svc);

            // A DIFFERENT wheel that reports "moving" (-1) at connect (a driver repositioning to
            // its last slot): the claim is taken at connect, the decision waits for the first
            // known position on a later refresh tick of the SAME connection…
            stub.Position = -1;
            await ConnectAsync(svc, stub, uniqueId: "second-wheel");
            Assert.That(await WaitForWriteAsync(stub, TimeSpan.FromSeconds(3)), Is.False, "position unknown — nothing to decide yet");
            stub.Position = 3;
            Assert.That(await WaitForWriteAsync(stub, TimeSpan.FromSeconds(10)), Is.True, "the home fires once the position becomes known");
            Assert.That(stub.PositionWrites.TryDequeue(out var target) && target == FilterWheelService.DefaultSlot, Is.True);
            await DisconnectAsync(svc);

            // …and because it was claimed at connect, a reconnect mid-sequence (parked on Ha) is left alone.
            stub.Position = 2;
            await ConnectAsync(svc, stub, uniqueId: "second-wheel");
            Assert.That(await WaitForWriteAsync(stub, TimeSpan.FromSeconds(3)), Is.False, "claimed at first connect — a reconnect never homes");
            await DisconnectAsync(svc);
        }

        [Test]
        [Category("bench")]
        public async Task An_explicit_change_before_the_first_known_position_retires_the_home() {
            // Review of #1073: a wheel mid-rotation at connect, then a change to slot 2 before the
            // position was ever read — the change's own follow-up refresh must NOT fire the home.
            await using var stub = StubWheel.Start(position: -1);
            using var svc = new FilterWheelService();
            await ConnectAsync(svc, stub);
            // Slots are seeded even while the position is unknown; wait for them so the change validates.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline && (await svc.GetAsync(CancellationToken.None))?.Slots.Count == 0) {
                await Task.Delay(50);
            }
            await svc.ChangeFilterAsync(new FilterChangeRequestDto(2), idempotencyKey: null, CancellationToken.None);
            Assert.That(await WaitForWriteAsync(stub, TimeSpan.FromSeconds(10)), Is.True, "the requested change is written");
            Assert.That(stub.PositionWrites.TryDequeue(out var first) && first == 2, Is.True);
            // The stub now reports 2 (a known position) — the retired home must not follow.
            await Task.Delay(TimeSpan.FromSeconds(5));
            Assert.That(stub.PositionWrites, Is.Empty, "the explicit slot is never overridden by the first-connect home");
            await DisconnectAsync(svc);
        }

        private static async Task WaitForSlotsAsync(FilterWheelService svc) {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline && (await svc.GetAsync(CancellationToken.None))?.Slots.Count == 0) {
                await Task.Delay(50);
            }
        }

        [Test]
        [Category("bench")]
        public async Task A_sequence_SwitchFilter_before_the_first_known_position_retires_the_home_too() {
            // Review of #1073: the mediator path (a sequence's SwitchFilter) must retire the pending
            // home exactly like the REST path — delete RetirePendingHome() in the mediator and the
            // wheel is pulled to 0 behind the sequence.
            await using var stub = StubWheel.Start(position: -1);
            using var svc = new FilterWheelService();
            await ConnectAsync(svc, stub);
            await WaitForSlotsAsync(svc);
            var result = await ((IFilterWheelMediator)svc).ChangeFilter(new FilterInfo("G", 0, 2), progress: null, CancellationToken.None);
            Assert.That(result.Position, Is.EqualTo(2), "the sequence's change is confirmed on the requested slot");
            Assert.That(stub.PositionWrites.TryDequeue(out var first) && first == 2, Is.True);
            await Task.Delay(TimeSpan.FromSeconds(5));
            Assert.That(stub.PositionWrites, Is.Empty, "the first-connect home never lands on top of a sequence's filter");
            await DisconnectAsync(svc);
        }

        [Test]
        [Category("bench")]
        public async Task A_wheel_that_never_reports_a_position_within_the_window_is_left_alone() {
            // Review of #1073: the pending window is bounded (seed + MaxPendingHomeTicks ticks). A wheel
            // that reports -1 past it is not "just connected" any more — once it finally reports a
            // known position off 0, no home fires.
            await using var stub = StubWheel.Start(position: -1);
            using var svc = new FilterWheelService();
            await ConnectAsync(svc, stub);
            // Refresh cadence is 2 s; sit well past seed + 4 ticks.
            await Task.Delay(TimeSpan.FromSeconds(13));
            stub.Position = 3;
            Assert.That(await WaitForWriteAsync(stub, TimeSpan.FromSeconds(6)), Is.False, "the home window expired — the wheel is left where it is");
            await DisconnectAsync(svc);
        }

        [Test]
        [Category("bench")]
        public async Task A_sequence_SwitchFilter_issued_before_the_slots_land_is_honoured_and_retires_the_home() {
            // #1079: boot auto-connect immediately followed by a sequence's first SwitchFilter. The
            // stub withholds the slot list for a few seconds, so ChangeFilter finds _slots null —
            // it must wait for them (not skip) and move to the requested slot; the first-connect
            // home (which needs no slot list and may already have been written) must never land
            // AFTER it, so the wheel ends where the sequence asked.
            await using var stub = StubWheel.Start(position: 3);
            stub.NamesAvailableAt = DateTime.UtcNow.AddSeconds(3);
            using var svc = new FilterWheelService();
            await ConnectAsync(svc, stub);
            var result = await ((IFilterWheelMediator)svc).ChangeFilter(new FilterInfo("G", 0, 2), progress: null, CancellationToken.None);
            Assert.That(result.Position, Is.EqualTo(2), "the change waited for the slot list and was honoured");
            await Task.Delay(TimeSpan.FromSeconds(5));
            var writes = stub.PositionWrites.ToArray();
            Assert.That(writes, Does.Contain(2));
            Assert.That(writes[^1], Is.EqualTo(2), "nothing — not the first-connect home — lands behind the sequence's change");
            Assert.That(writes.Count(w => w == FilterWheelService.DefaultSlot), Is.LessThanOrEqualTo(1), "the home is written at most once");
            await DisconnectAsync(svc);
        }

        [Test]
        [Category("bench")]
        public async Task A_sequence_SwitchFilter_retires_the_home_even_when_the_change_itself_is_skipped() {
            // #1079 — the retire happens before the slot-list wait and whether or not the change
            // goes ahead: the wheel is mid-rotation at connect (home pending, undecided) and the
            // slot list never arrives, so ChangeFilter waits out its budget and skips the change;
            // once the wheel then reports a position, the pending home must NOT fire. Restore the
            // old post-validation retire and the wheel is pulled to 0 behind the sequence.
            await using var stub = StubWheel.Start(position: -1);
            stub.NamesAvailableAt = DateTime.UtcNow.AddMinutes(5);
            using var svc = new FilterWheelService();
            await ConnectAsync(svc, stub);
            var started = DateTime.UtcNow;
            var result = await ((IFilterWheelMediator)svc).ChangeFilter(new FilterInfo("G", 0, 2), progress: null, CancellationToken.None);
            Assert.That(result.Position, Is.EqualTo(2), "a skipped change hands back the requested filter");
            Assert.That(DateTime.UtcNow - started, Is.GreaterThan(TimeSpan.FromSeconds(4)).And.LessThan(TimeSpan.FromSeconds(20)), "the slot wait is bounded");
            Assert.That(stub.PositionWrites, Is.Empty, "no slot list → nothing is written to the wheel");
            // The wheel now reports a known, off-0 position: a still-pending home would fire here.
            stub.Position = 3;
            Assert.That(await WaitForWriteAsync(stub, TimeSpan.FromSeconds(6)), Is.False, "the sequence's SwitchFilter retired the home even though its own change was skipped");
            await DisconnectAsync(svc);
        }

        [Test]
        [Category("bench")]
        public async Task A_dispatched_home_steps_aside_when_a_change_bumped_its_token_before_the_write() {
            // #1079 — deterministic version of the sub-millisecond race: the wheel is parked off 0
            // and its home has (notionally) been dispatched with the generation as it was; a
            // change then bumps the generation; invoking the home task with the STALE token must
            // issue no Position write.
            await using var stub = StubWheel.Start(position: 3);
            using var svc = new FilterWheelService();
            await ConnectAsync(svc, stub);
            // The real first-connect home lands first (position known at seed); let it settle.
            Assert.That(await WaitForWriteAsync(stub, TimeSpan.FromSeconds(10)), Is.True);
            while (stub.PositionWrites.TryDequeue(out _)) { }
            await WaitForSlotsAsync(svc);
            var (stale, client) = svc.HomeTokenForTest();
            Assert.That(client, Is.Not.Null);
            // An explicit change bumps the token (and moves the wheel to 2).
            await svc.ChangeFilterAsync(new FilterChangeRequestDto(2), idempotencyKey: null, CancellationToken.None);
            Assert.That(await WaitForWriteAsync(stub, TimeSpan.FromSeconds(10)), Is.True);
            while (stub.PositionWrites.TryDequeue(out _)) { }
            // The stale home task runs now: it must step aside.
            svc.HomeInBackground(client!, stale);
            await Task.Delay(500);
            Assert.That(stub.PositionWrites, Is.Empty, "a home whose token moved never writes Position = 0");
            // And the CURRENT token still homes (proves the guard is the token, not the connection checks).
            var (current, _) = svc.HomeTokenForTest();
            svc.HomeInBackground(client!, current);
            Assert.That(await WaitForWriteAsync(stub, TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(stub.PositionWrites.TryDequeue(out var w) && w == FilterWheelService.DefaultSlot, Is.True);
            await DisconnectAsync(svc);
        }

        [Test]
        [Category("bench")]
        public async Task The_profile_policy_can_turn_the_first_connect_home_off() {
            // #1075 — home_on_first_connect = false: the wheel is left where the driver reports it.
            await using var stub = StubWheel.Start(position: 3);
            var store = new InMemoryProfileStore();
            store.PutFilterWheelPolicy(new FilterWheelPolicyDto(HomeOnFirstConnect: false));
            using var svc = new FilterWheelService(profileStore: store);
            await ConnectAsync(svc, stub);
            Assert.That(await WaitForWriteAsync(stub, TimeSpan.FromSeconds(5)), Is.False, "policy off → no home write");
            // Turning it on afterwards never homes this session's already-claimed wheel.
            store.PutFilterWheelPolicy(FilterWheelPolicyDto.Default);
            await DisconnectAsync(svc);
            await ConnectAsync(svc, stub);
            Assert.That(await WaitForWriteAsync(stub, TimeSpan.FromSeconds(3)), Is.False, "the claim was consumed on the first connect");
            await DisconnectAsync(svc);
        }
    }
}
