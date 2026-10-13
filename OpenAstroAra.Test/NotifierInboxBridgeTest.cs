#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using OpenAstroAra.Core.Utility.Notification;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1174 — the static Notifier's warnings and errors reach the §46 inbox.</summary>
    [TestFixture]
    [NonParallelizable] // Notifier.Use is process-global
    public class NotifierInboxBridgeTest {

        private sealed class RecordingSink : INotifierSink {
            public readonly List<(bool IsError, string? Header, string Message)> Seen = new();
            public void Notify(bool isError, string? header, string message) => Seen.Add((isError, header, message));
        }

        [TearDown]
        public void ClearSink() => Notifier.Use(null);

        [Test]
        public void Warnings_and_errors_reach_the_sink_informational_toasts_do_not() {
            var sink = new RecordingSink();
            Notifier.Use(sink);

            Notifier.ShowInformation("just so you know");
            Notifier.ShowSuccess("done");
            Notifier.ShowWarning("mount dither: mount disconnected");
            Notifier.ShowError("dome radius misconfigured");
            Notifier.ShowExternalWarning("settle timed out", "PHD2");

            Assert.That(sink.Seen, Is.EqualTo(new[] {
                (false, (string?)null, "mount dither: mount disconnected"),
                (true, (string?)null, "dome radius misconfigured"),
                (false, "PHD2", "settle timed out"),
            }));
        }

        [Test]
        public void A_throwing_sink_never_reaches_the_caller() {
            var sink = new Mock<INotifierSink>();
            sink.Setup(s => s.Notify(It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string>())).Throws(new InvalidOperationException("inbox down"));
            Notifier.Use(sink.Object);
            Assert.DoesNotThrow(() => Notifier.ShowWarning("still logged"));
        }

        [Test]
        public async Task The_bridge_posts_an_inbox_entry_with_the_right_severity_and_dedupes_repeats() {
            var posted = new List<NotificationDto>();
            var notifications = new Mock<INotificationService>();
            notifications.Setup(n => n.CreateAsync(It.IsAny<NotificationDto>(), It.IsAny<CancellationToken>()))
                .Callback<NotificationDto, CancellationToken>((d, _) => { lock (posted) { posted.Add(d); } })
                .Returns(Task.CompletedTask);
            var now = new DateTimeOffset(2026, 10, 13, 22, 0, 0, TimeSpan.Zero);
            var bridge = new NotifierInboxBridge(notifications.Object, NullLogger<NotifierInboxBridge>.Instance, () => now);

            bridge.Notify(isError: false, header: null, "guide star lost");
            bridge.Notify(isError: false, header: null, "guide star lost"); // same tick-loop toast again
            bridge.Notify(isError: true, header: "PHD2", "settle failed");
            now += NotifierInboxBridge.RepeatWindow + TimeSpan.FromSeconds(1);
            bridge.Notify(isError: false, header: null, "guide star lost"); // outside the window: a new episode

            Assert.That(await WaitForCountAsync(posted, 3), Is.True, "three distinct posts");
            var warning = posted.First(p => p.Message == "guide star lost");
            Assert.That(warning.Severity, Is.EqualTo(NotificationSeverity.Warning));
            Assert.That(warning.Category, Is.EqualTo(NotificationCategory.Equipment));
            Assert.That(warning.Title, Is.EqualTo("Equipment warning"));
            var error = posted.Single(p => p.Message == "settle failed");
            Assert.That(error.Severity, Is.EqualTo(NotificationSeverity.Error));
            Assert.That(error.Title, Is.EqualTo("PHD2"), "the external header becomes the title");
            Assert.That(posted.Count(p => p.Message == "guide star lost"), Is.EqualTo(2), "repeat inside the window dropped, outside it posted");
        }

        [Test]
        public async Task Notify_returns_at_once_while_the_inbox_write_blocks() {
            // r1 finding: the SQLite write (and its busy wait) must not run on the device thread
            // that raised the toast — PHD2's settle path and the sequencer toast mid-run.
            // A synchronously blocking write, the way Microsoft.Data.Sqlite's *Async calls behave.
            using var release = new ManualResetEventSlim(false);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var notifications = new Mock<INotificationService>();
            notifications.Setup(n => n.CreateAsync(It.IsAny<NotificationDto>(), It.IsAny<CancellationToken>()))
                .Returns(() => { entered.TrySetResult(); release.Wait(TimeSpan.FromSeconds(10)); return Task.CompletedTask; });
            var bridge = new NotifierInboxBridge(notifications.Object, NullLogger<NotifierInboxBridge>.Instance);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            bridge.Notify(isError: false, header: null, "star lost");
            sw.Stop();

            Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromMilliseconds(500)), "Notify must not wait for the inbox write");
            Assert.That(await Task.WhenAny(entered.Task, Task.Delay(TimeSpan.FromSeconds(5))), Is.SameAs(entered.Task), "the write did start, elsewhere");
            release.Set();
        }

        [Test]
        public void Old_slots_are_pruned_and_a_simultaneous_duplicate_posts_once() {
            var posted = 0;
            var notifications = new Mock<INotificationService>();
            notifications.Setup(n => n.CreateAsync(It.IsAny<NotificationDto>(), It.IsAny<CancellationToken>()))
                .Callback(() => Interlocked.Increment(ref posted)).Returns(Task.CompletedTask);
            var now = new DateTimeOffset(2026, 10, 13, 22, 0, 0, TimeSpan.Zero);
            var bridge = new NotifierInboxBridge(notifications.Object, NullLogger<NotifierInboxBridge>.Instance, () => now);

            Parallel.For(0, 32, _ => bridge.Notify(isError: false, header: null, "same toast"));
            Assert.That(bridge.SlotCount, Is.EqualTo(1));
            for (var i = 0; i < 20; i++) {
                bridge.Notify(isError: true, header: null, $"error code {i}");
            }
            Assert.That(bridge.SlotCount, Is.EqualTo(21), "every distinct text holds a slot inside the window");

            now += NotifierInboxBridge.RepeatWindow + TimeSpan.FromSeconds(1);
            bridge.Notify(isError: false, header: null, "a new night");
            Assert.That(bridge.SlotCount, Is.EqualTo(1), "slots older than the window are pruned on the next post");
            Assert.That(SpinWait.SpinUntil(() => Volatile.Read(ref posted) >= 22, TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(posted, Is.EqualTo(22), "32 simultaneous identical toasts posted exactly once");
        }

        [Test]
        public void A_failing_inbox_write_is_swallowed() {
            var notifications = new Mock<INotificationService>();
            notifications.Setup(n => n.CreateAsync(It.IsAny<NotificationDto>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("db locked"));
            var bridge = new NotifierInboxBridge(notifications.Object, NullLogger<NotifierInboxBridge>.Instance);
            Assert.DoesNotThrow(() => bridge.Notify(isError: true, header: null, "boom"));
        }

        private static async Task<bool> WaitForCountAsync(List<NotificationDto> list, int count) {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline) {
                lock (list) {
                    if (list.Count >= count) {
                        return true;
                    }
                }
                await Task.Delay(20);
            }
            return false;
        }
    }
}
