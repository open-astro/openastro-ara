#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Hosting;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Test {

    [TestFixture]
    public class ServerRestartTest {
        private sealed class FakeRestarter : IDaemonRestarter {
            public readonly TaskCompletionSource Called = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public int Calls;
            public void Restart() { Interlocked.Increment(ref Calls); Called.TrySetResult(); }
        }

        [Test]
        public async Task RestartAsync_returns_accepted_then_calls_the_seam_once_after_the_notice() {
            var fake = new FakeRestarter();
            var svc = new PlaceholderServerStateService(restarter: fake);

            var accepted = await svc.RestartAsync("test", null, CancellationToken.None);

            Assert.That(accepted, Is.Not.Null);
            Assert.That(fake.Calls, Is.EqualTo(0), "the seam must wait for the 2 s notice");
            Assert.That(await Task.WhenAny(fake.Called.Task, Task.Delay(TimeSpan.FromSeconds(10))), Is.SameAs(fake.Called.Task));
            await Task.Delay(300);
            Assert.That(fake.Calls, Is.EqualTo(1));
        }

        private sealed class RecordingBroadcaster : IWsBroadcaster {
            public readonly List<(string Type, string Payload)> Events = [];
            public long CurrentSequence => Events.Count;
            public Task PublishAsync(OpenAstroAra.Server.Contracts.WsEvents.WsEvent eventType, System.Text.Json.JsonElement payload, CancellationToken ct) {
                lock (Events) { Events.Add((eventType.Token, payload.GetRawText())); }
                return Task.CompletedTask;
            }
            public bool Has(string type) { lock (Events) { return Events.Any(e => e.Type == type); } }
        }

        [Test]
        public async Task RestartOnIdleAsync_with_no_run_restarts_at_once_through_the_seam() {
            var fake = new FakeRestarter();
            var svc = new PlaceholderServerStateService(restarter: fake, occupancy: new RigOccupancy().RegisterSession(new ActiveRunSessionRegistry(), () => null));

            await svc.RestartOnIdleAsync("test", null, CancellationToken.None);

            Assert.That(await Task.WhenAny(fake.Called.Task, Task.Delay(TimeSpan.FromSeconds(10))), Is.SameAs(fake.Called.Task));
        }

        [Test]
        public async Task RestartOnIdleAsync_with_a_run_waits_says_pending_and_restarts_once_when_the_run_ends() {
            var fake = new FakeRestarter();
            var ws = new RecordingBroadcaster();
            var runs = new ActiveRunSessionRegistry();
            var svc = new PlaceholderServerStateService(broadcaster: ws, restarter: fake, occupancy: new RigOccupancy().RegisterSession(runs, () => null));
            var run = Guid.NewGuid();
            runs.Enter(run);

            await svc.RestartOnIdleAsync("test", null, CancellationToken.None);
            await svc.RestartOnIdleAsync("again", null, CancellationToken.None);

            await Task.Delay(2500);
            Assert.That(fake.Calls, Is.EqualTo(0), "must not restart while a run is active");
            Assert.That(ws.Has("server.restart_pending"), Is.True);
            Assert.That(ws.Events.Count(e => e.Type == "server.restart_pending"), Is.EqualTo(1), "a second request adds no second pending notice");

            runs.Exit(run);

            Assert.That(await Task.WhenAny(fake.Called.Task, Task.Delay(TimeSpan.FromSeconds(10))), Is.SameAs(fake.Called.Task));
            await Task.Delay(2500);
            Assert.That(fake.Calls, Is.EqualTo(1));
            Assert.That(ws.Has("server.restart_imminent"), Is.True);
        }

        private static Moq.Mock<ISequencerService> SequencerWith(Func<int> activeRuns) {
            var seq = new Moq.Mock<ISequencerService>();
            seq.SetupGet(x => x.HasUnfinishedRun).Returns(() => activeRuns() > 0);
            return seq;
        }

        [Test]
        public async Task RestartOnIdleAsync_waits_for_a_sequencer_run_that_has_no_catalog_session() {
            var fake = new FakeRestarter();
            var active = 1;
            var seq = SequencerWith(() => active);
            var svc = new PlaceholderServerStateService(restarter: fake, occupancy: new RigOccupancy().RegisterSession(new ActiveRunSessionRegistry(), () => seq.Object));

            await svc.RestartOnIdleAsync("test", null, CancellationToken.None);

            await Task.Delay(2500);
            Assert.That(fake.Calls, Is.EqualTo(0), "a sequencer run is on, so no restart");
            active = 0;
            Assert.That(await Task.WhenAny(fake.Called.Task, Task.Delay(TimeSpan.FromSeconds(10))), Is.SameAs(fake.Called.Task));
            await Task.Delay(2500);
            Assert.That(fake.Calls, Is.EqualTo(1));
        }

        // #2141 — restart when idle waits for any routine on the register, not only a Session.
        [Test]
        public async Task RestartOnIdleAsync_waits_for_a_polar_alignment() {
            var fake = new FakeRestarter();
            var aligning = 1;
            var occupancy = new RigOccupancy().Register("polar alignment", RigHolding.Mount, () => Volatile.Read(ref aligning) == 1);
            var svc = new PlaceholderServerStateService(restarter: fake, occupancy: occupancy);

            var accepted = await svc.RestartOnIdleAsync("test", null, CancellationToken.None);

            Assert.That(accepted.Status, Is.EqualTo("queued"), "a routine is on, so the restart is queued");
            await Task.Delay(2500);
            Assert.That(fake.Calls, Is.EqualTo(0), "polar alignment is running, so no restart");
            Volatile.Write(ref aligning, 0);
            Assert.That(await Task.WhenAny(fake.Called.Task, Task.Delay(TimeSpan.FromSeconds(10))), Is.SameAs(fake.Called.Task));
        }

        [Test]
        public async Task RestartOnIdleAsync_does_not_restart_when_a_run_starts_inside_the_notice_window() {
            var fake = new FakeRestarter();
            var ws = new RecordingBroadcaster();
            var active = 0;
            var seq = SequencerWith(() => Volatile.Read(ref active));
            var runs = new ActiveRunSessionRegistry();
            var svc = new PlaceholderServerStateService(broadcaster: ws, restarter: fake, occupancy: new RigOccupancy().RegisterSession(runs, () => seq.Object));
            var session = Guid.NewGuid();
            runs.Enter(session);
            await svc.RestartOnIdleAsync("test", null, CancellationToken.None);
            runs.Exit(session); // the run ends; its auto-flats run is not on yet

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!ws.Has("server.restart_imminent") && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.That(ws.Has("server.restart_imminent"), Is.True, "the 2 s notice must start once no run is on");

            Volatile.Write(ref active, 1); // the flats run starts inside the 2 s notice window
            await Task.Delay(3000);
            Assert.That(fake.Calls, Is.EqualTo(0), "the flats run must not be killed");

            Volatile.Write(ref active, 0);
            Assert.That(await Task.WhenAny(fake.Called.Task, Task.Delay(TimeSpan.FromSeconds(10))), Is.SameAs(fake.Called.Task));
            await Task.Delay(500);
            Assert.That(fake.Calls, Is.EqualTo(1));
        }

        [Test]
        public async Task RestartOnIdleAsync_says_queued_with_a_run_and_restarting_without_one() {
            var runs = new ActiveRunSessionRegistry();
            var idle = new PlaceholderServerStateService(restarter: new FakeRestarter(), occupancy: new RigOccupancy().RegisterSession(runs, () => null));
            Assert.That((await idle.RestartOnIdleAsync("t", null, CancellationToken.None)).Status, Is.EqualTo("restarting"));

            runs.Enter(Guid.NewGuid());
            var busy = new PlaceholderServerStateService(restarter: new FakeRestarter(), occupancy: new RigOccupancy().RegisterSession(runs, () => null));
            Assert.That((await busy.RestartOnIdleAsync("t", null, CancellationToken.None)).Status, Is.EqualTo("queued"));
            await busy.CancelRestartOnIdleAsync(CancellationToken.None); // stop its waiter: the run never ends
        }

        [Test]
        public async Task CancelRestartOnIdleAsync_stops_the_restart_publishes_cancelled_once_and_clears_PendingRestart() {
            var fake = new FakeRestarter();
            var ws = new RecordingBroadcaster();
            var runs = new ActiveRunSessionRegistry();
            var svc = new PlaceholderServerStateService(broadcaster: ws, restarter: fake, occupancy: new RigOccupancy().RegisterSession(runs, () => null));
            var run = Guid.NewGuid();
            runs.Enter(run);
            await svc.RestartOnIdleAsync("because", null, CancellationToken.None);

            var queued = (await svc.GetSnapshotAsync(CancellationToken.None)).PendingRestart;
            Assert.That(queued, Is.Not.Null, "a queued request is visible in the server state");
            Assert.That(queued!.Reason, Is.EqualTo("because"));
            Assert.That(queued.RestartOnIdle, Is.True);

            await svc.CancelRestartOnIdleAsync(CancellationToken.None);
            await svc.CancelRestartOnIdleAsync(CancellationToken.None); // nothing queued now: no-op

            Assert.That((await svc.GetSnapshotAsync(CancellationToken.None)).PendingRestart, Is.Null);
            Assert.That(ws.Events.Count(e => e.Type == "server.restart_cancelled"), Is.EqualTo(1));
            runs.Exit(run);
            await Task.Delay(4500);
            Assert.That(fake.Calls, Is.EqualTo(0), "a cancelled request must not restart when the run ends");
            Assert.That(ws.Has("server.restart_imminent"), Is.False);
        }

        [Test]
        public async Task CancelRestartOnIdleAsync_with_nothing_queued_does_nothing() {
            var ws = new RecordingBroadcaster();
            var svc = new PlaceholderServerStateService(broadcaster: ws, restarter: new FakeRestarter(), occupancy: new RigOccupancy().RegisterSession(new ActiveRunSessionRegistry(), () => null));
            await svc.CancelRestartOnIdleAsync(CancellationToken.None);
            Assert.That(ws.Events, Is.Empty);
        }

        [Test]
        public async Task RestartOnIdleAsync_is_held_by_a_paused_run_and_an_aborting_run_until_terminal() {
            var fake = new FakeRestarter();
            var states = new Moq.Mock<ISequencerService>();
            var held = new[] { Guid.NewGuid() }; // what a paused or an aborting run reports as unfinished
            var unfinished = (IReadOnlyList<Guid>)held;
            states.SetupGet(x => x.HasUnfinishedRun).Returns(() => Volatile.Read(ref unfinished).Count > 0);
            var svc = new PlaceholderServerStateService(restarter: fake, occupancy: new RigOccupancy().RegisterSession(new ActiveRunSessionRegistry(), () => states.Object));

            var accepted = await svc.RestartOnIdleAsync("t", null, CancellationToken.None);

            Assert.That(accepted.Status, Is.EqualTo("queued"));
            await Task.Delay(3000);
            Assert.That(fake.Calls, Is.EqualTo(0));
            Volatile.Write(ref unfinished, Array.Empty<Guid>());
            Assert.That(await Task.WhenAny(fake.Called.Task, Task.Delay(TimeSpan.FromSeconds(10))), Is.SameAs(fake.Called.Task));
        }

        private sealed class FakeHostApplicationLifetime : IHostApplicationLifetime {
            public int StopCalls;
            public CancellationToken ApplicationStarted => CancellationToken.None;
            public CancellationToken ApplicationStopping => CancellationToken.None;
            public CancellationToken ApplicationStopped => CancellationToken.None;
            public void StopApplication() => StopCalls++;
        }

        [Test]
        public void ExitDaemonRestarter_Restart_sets_exit_code_75_and_stops_the_host() {
            var lifetime = new FakeHostApplicationLifetime();
            var saved = Environment.ExitCode;
            try {
                new ExitDaemonRestarter(lifetime).Restart();

                Assert.That(Environment.ExitCode, Is.EqualTo(ExitDaemonRestarter.RestartExitCode));
                Assert.That(ExitDaemonRestarter.RestartExitCode, Is.EqualTo(75));
                Assert.That(lifetime.StopCalls, Is.EqualTo(1));
            } finally {
                Environment.ExitCode = saved;
            }
        }
    }
}