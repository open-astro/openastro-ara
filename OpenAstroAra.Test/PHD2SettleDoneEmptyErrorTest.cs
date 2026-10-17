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
using OpenAstroAra.Core.Utility.Notification;
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2.PhdEvents;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Guider;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// The guider's successful <c>SettleDone</c> carries <c>"Error": ""</c>, not an absent key. The night of
    /// 2026-10-16 logged 46 <c>PHD2 error:</c> lines with nothing after them and raised a PHD2 warning for
    /// each (escalated by the unattended-hours rule). A settle with an empty error is a success: no warning.
    /// </summary>
    [TestFixture]
    [NonParallelizable] // Notifier.Use is process-global
    [Category("IO")] // loopback socket against the fake guider
    public class PHD2SettleDoneEmptyErrorTest {

        private sealed class RecordingSink : INotifierSink {
            public readonly ConcurrentQueue<(bool IsError, string? Header, string Message)> Seen = new();
            public void Notify(bool isError, string? header, string message) => Seen.Enqueue((isError, header, message));
        }

        [TearDown]
        public void ClearSink() => Notifier.Use(null);

        private static JsonObject SettleDone(int status, string? error) {
            var e = new JsonObject { ["Event"] = "SettleDone", ["Timestamp"] = 0.0, ["Host"] = "fake-guider", ["Inst"] = 1, ["Status"] = status };
            if (error is not null) {
                e["Error"] = error;
            }
            return e;
        }

        [Test]
        public async Task A_settle_done_with_an_empty_error_is_a_success_and_raises_no_warning() {
            await using var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Guiding"));
            var profiles = new HeadlessProfileService();
            profiles.ActiveProfile.GuiderSettings.PHD2ServerHost = "127.0.0.1";
            profiles.ActiveProfile.GuiderSettings.PHD2ServerPort = fake.Port;
            using var guider = new PHD2Guider(profiles);
            using var markers = new BlockingCollection<PhdGuiderMarkerEventArgs>();
            guider.MarkerEvent += (_, m) => markers.Add(m);
            using var cts = new CancellationTokenSource(System.TimeSpan.FromSeconds(60));
            Assert.That(await guider.Connect(cts.Token).ConfigureAwait(false), Is.True, "never connected to the fake");
            var sink = new RecordingSink();
            Notifier.Use(sink);

            await fake.BroadcastAsync(SettleDone(status: 0, error: "")).ConfigureAwait(false);
            Assert.That(markers.TryTake(out var ok, System.TimeSpan.FromSeconds(10)), Is.True, "no settle_done marker");
            Assert.That(ok!.Kind, Is.EqualTo("settle_done"));
            Assert.That(guider.LastSettleError, Is.Null);
            Assert.That(sink.Seen, Is.Empty, "an empty Error is a successful settle: no PHD2 warning");

            // The real failure still warns, so the sink is proven live.
            await fake.BroadcastAsync(SettleDone(status: 1, error: "timed-out waiting for guider to settle")).ConfigureAwait(false);
            Assert.That(markers.TryTake(out _, System.TimeSpan.FromSeconds(10)), Is.True);
            Assert.That(guider.LastSettleError, Is.EqualTo("timed-out waiting for guider to settle"));
            var seen = new List<(bool, string?, string)>(sink.Seen);
            Assert.That(seen, Has.Count.EqualTo(1));
            Assert.That(seen[0].Item3, Does.Contain("timed-out"));
        }
    }
}
