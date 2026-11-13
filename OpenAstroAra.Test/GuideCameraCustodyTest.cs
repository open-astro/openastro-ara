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
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// ADR 0016 — who holds the Guide Camera, tested once against a fake guider state and lease: no
    /// socket, no delay, no <c>GuiderService</c>.
    /// </summary>
    [TestFixture]
    public class GuideCameraCustodyTest {

        private sealed class FakePort : IGuideCameraPort {
            public string? State { get; set; } = "Stopped";
            public bool? LeaseReported { get; set; }
            public bool FailRelease { get; set; }
            public List<string> Calls { get; } = new();
            public string? AppState => State;
            public Task<bool?> LeaseActiveAsync(CancellationToken ct) {
                Calls.Add("get-lease");
                return Task.FromResult(LeaseReported);
            }
            public Task SetLeaseAsync(bool active, int? timeoutS, CancellationToken ct) {
                Calls.Add(active ? $"lease-on:{timeoutS}" : "lease-off");
                if (!active && FailRelease) {
                    throw new InvalidOperationException("guider dropped");
                }
                return Task.CompletedTask;
            }
        }

        private static readonly CameraUse[] Setup = { CameraUse.PolarAlign, CameraUse.GuideFocus, CameraUse.LiveView };

        private static Func<CameraUse, Task> Stop(List<string> log, string name) => by => { log.Add($"stop:{name}:{by}"); return Task.CompletedTask; };

        [TestCaseSource(nameof(Setup))]
        public async Task Guiding_takes_the_camera_from_each_setup_holder_stops_it_then_releases_the_lease(CameraUse holder) {
            var custody = new GuideCameraCustody();
            var port = new FakePort();
            var log = new List<string>();
            var stop = Stop(log, holder.ToString());
            await custody.TakeAsync(holder, port, by => { port.Calls.Add("stop"); return stop(by); }, CancellationToken.None);
            port.Calls.Clear();

            await custody.TakeForGuidingAsync(port, CancellationToken.None);

            Assert.That(log, Is.EqualTo(new[] { $"stop:{holder}:Guiding" }), "the holder is stopped, not completed");
            Assert.That(port.Calls.IndexOf("stop"), Is.EqualTo(0));
            Assert.That(port.Calls.IndexOf("lease-off"), Is.GreaterThan(port.Calls.IndexOf("stop")), "the lease goes after the holder stops");
            Assert.That(custody.Holder, Is.Null);
        }

        [TestCase("Guiding")]
        [TestCase("Calibrating")]
        [TestCase("Paused")]
        [TestCase("LostLock")]
        public async Task A_setup_activity_is_refused_with_a_reason_while_the_guider_is_busy(string state) {
            foreach (var use in Setup) {
                var custody = new GuideCameraCustody();
                var port = new FakePort { State = state };
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => custody.TakeAsync(use, port, _ => Task.CompletedTask, CancellationToken.None));
                Assert.That(ex!.Message, Does.Contain(state).IgnoreCase.And.Contain("stop guiding"), use.ToString());
                Assert.That(custody.Holder, Is.Null, "a refusal holds nothing");
                Assert.That(port.Calls, Is.Empty, "a refusal never touches the lease");
            }
        }

        [TestCase("Looping")]
        [TestCase("Selected")]
        [TestCase("Stopped")]
        [TestCase(null)]
        public async Task A_setup_activity_is_allowed_while_the_guider_is_not_busy(string? state) {
            foreach (var use in Setup) {
                var custody = new GuideCameraCustody();
                var claim = await custody.TakeAsync(use, new FakePort { State = state }, _ => Task.CompletedTask, CancellationToken.None);
                Assert.That(custody.Holder, Is.EqualTo(use));
                await claim.ReleaseAsync();
                Assert.That(custody.Holder, Is.Null);
            }
        }

        [Test]
        public async Task A_newer_setup_activity_takes_the_camera_from_the_older_one_in_each_order() {
            foreach (var older in Setup) {
                foreach (var newer in Setup.Where(u => u != older)) {
                    var custody = new GuideCameraCustody();
                    var port = new FakePort();
                    var log = new List<string>();
                    await custody.TakeAsync(older, port, Stop(log, "older"), CancellationToken.None);
                    await custody.TakeAsync(newer, port, Stop(log, "newer"), CancellationToken.None);
                    Assert.That(log, Is.EqualTo(new[] { $"stop:older:{newer}" }), $"{older} -> {newer}");
                    Assert.That(custody.Holder, Is.EqualTo(newer));
                }
            }
        }

        [Test]
        public async Task A_stale_lease_with_no_holder_is_released_before_guiding() {
            var custody = new GuideCameraCustody();
            var port = new FakePort { LeaseReported = true };

            await custody.TakeForGuidingAsync(port, CancellationToken.None);

            Assert.That(port.Calls, Is.EqualTo(new List<string> { "get-lease", "lease-off" }));

            port.Calls.Clear();
            port.LeaseReported = false;
            await custody.TakeForGuidingAsync(port, CancellationToken.None);
            Assert.That(port.Calls, Is.EqualTo(new List<string> { "get-lease" }), "no lease, nothing to release");
        }

        [Test]
        public async Task A_fault_in_a_holders_stop_or_the_lease_release_never_stops_guiding_from_starting() {
            var custody = new GuideCameraCustody();
            var port = new FakePort { FailRelease = true };
            await custody.TakeAsync(CameraUse.GuideFocus, port, _ => throw new InvalidOperationException("loop stuck"), CancellationToken.None);

            await Assert.DoesNotThrowAsync(() => custody.TakeForGuidingAsync(port, CancellationToken.None));
            Assert.That(custody.Holder, Is.Null, "the holder is gone even though its stop and the release faulted");

            // A guider that cannot report its lease is no reason not to guide either.
            await Assert.DoesNotThrowAsync(() => custody.TakeForGuidingAsync(new ThrowingPort(), CancellationToken.None));
        }

        private sealed class ThrowingPort : IGuideCameraPort {
            public string? AppState => throw new InvalidOperationException("guider is not connected");
            public Task<bool?> LeaseActiveAsync(CancellationToken ct) => throw new InvalidOperationException("guider is not connected");
            public Task SetLeaseAsync(bool active, int? timeoutS, CancellationToken ct) => throw new InvalidOperationException("guider is not connected");
        }

        [Test]
        public async Task The_lease_is_taken_and_renewed_only_by_the_module_on_its_declared_interval() {
            var now = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
            var custody = new GuideCameraCustody { Clock = () => now };
            Assert.That(custody.LeaseRenewInterval, Is.EqualTo(TimeSpan.FromSeconds(240)));
            Assert.That(GuideCameraCustody.LeaseTimeoutSeconds, Is.EqualTo(600));
            var port = new FakePort();
            var claim = await custody.TakeAsync(CameraUse.PolarAlign, port, _ => Task.CompletedTask, CancellationToken.None);
            Assert.That(port.Calls, Is.EqualTo(new List<string> { "lease-on:600" }));

            now += TimeSpan.FromSeconds(240);
            await claim.RenewIfDueAsync(CancellationToken.None);
            Assert.That(port.Calls, Has.Count.EqualTo(1), "not due yet");

            now += TimeSpan.FromSeconds(1);
            await claim.RenewIfDueAsync(CancellationToken.None);
            Assert.That(port.Calls, Is.EqualTo(new List<string> { "lease-on:600", "lease-on:600" }));

            await claim.RenewIfDueAsync(CancellationToken.None);
            Assert.That(port.Calls, Has.Count.EqualTo(2), "a renew restarts the interval");

            // The live view holds custody only: it never takes or renews the guider program's lease.
            var live = await custody.TakeAsync(CameraUse.LiveView, port, _ => Task.CompletedTask, CancellationToken.None);
            port.Calls.Clear();
            now += TimeSpan.FromHours(1);
            await live.RenewIfDueAsync(CancellationToken.None);
            await live.ReleaseAsync();
            Assert.That(port.Calls, Is.Empty);
        }

        [Test]
        public async Task A_released_or_replaced_claim_leaves_the_lease_to_its_successor() {
            var custody = new GuideCameraCustody();
            var port = new FakePort();
            var first = await custody.TakeAsync(CameraUse.PolarAlign, port, _ => Task.CompletedTask, CancellationToken.None);
            var second = await custody.TakeAsync(CameraUse.GuideFocus, port, _ => Task.CompletedTask, CancellationToken.None);
            port.Calls.Clear();

            await first.ReleaseAsync();
            await first.RenewIfDueAsync(CancellationToken.None);

            Assert.That(port.Calls, Is.Empty, "the older claim no longer owns the lease");
            Assert.That(custody.Holder, Is.EqualTo(CameraUse.GuideFocus));
            await second.ReleaseAsync();
            Assert.That(port.Calls, Is.EqualTo(new List<string> { "lease-off" }));
        }

        [Test]
        public async Task The_held_reason_counts_every_setup_holder_not_only_polar_align() {
            var custody = new GuideCameraCustody();
            var port = new FakePort();
            Assert.That(custody.HeldReason("Stopped"), Is.Null);

            await custody.TakeAsync(CameraUse.PolarAlign, port, _ => Task.CompletedTask, CancellationToken.None);
            Assert.That(custody.HeldReason("Stopped"), Does.Contain("polar alignment"));

            await custody.TakeAsync(CameraUse.GuideFocus, port, _ => Task.CompletedTask, CancellationToken.None);
            Assert.That(custody.HeldReason("Stopped"), Does.Contain("focus"));

            await custody.TakeAsync(CameraUse.LiveView, port, _ => Task.CompletedTask, CancellationToken.None);
            Assert.That(custody.HeldReason("Looping"), Does.Contain("live view"));
            Assert.That(custody.HeldReason("Stopped"), Is.Null, "a live view whose loop has ended holds nothing worth waiting for");
        }

        [TestCase("Guiding", true)]
        [TestCase("Calibrating", true)]
        [TestCase("Paused", true)]
        [TestCase("LostLock", true)]
        [TestCase("Looping", false)]
        [TestCase("Selected", false)]
        [TestCase("Stopped", false)]
        [TestCase(null, false)]
        public void Busy_is_one_definition(string? state, bool busy) =>
            Assert.That(GuideCameraCustody.IsBusy(state), Is.EqualTo(busy));
    }
}