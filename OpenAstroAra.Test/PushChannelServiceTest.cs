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
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §54 push channels — sending rules (both-values gate, severity gate, trigger toggles) and
    /// the outbound wire shapes, captured by a canned <see cref="HttpMessageHandler"/>; a
    /// provider failure must log-and-drop, never escape toward the notification chokepoint.
    /// </summary>
    [TestFixture]
    public class PushChannelServiceTest {

        private sealed class CapturingHandler : HttpMessageHandler {
            public readonly ConcurrentQueue<(Uri Uri, string Body)> Requests = new();
            public HttpStatusCode Status = HttpStatusCode.OK;

            protected override async Task<HttpResponseMessage> SendAsync(
                    HttpRequestMessage request, CancellationToken ct) {
                var body = request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(ct);
                Requests.Enqueue((request.RequestUri!, body));
                return new HttpResponseMessage(Status);
            }
        }

        private static InMemoryProfileStore StoreWith(
                string pushoverToken = "", string pushoverUser = "",
                string telegramToken = "", string telegramChat = "",
                bool onSafety = true) {
            var store = new InMemoryProfileStore();
            store.PutNotificationsSettings(store.GetNotificationsSettings() with {
                PushoverToken = pushoverToken,
                PushoverUserKey = pushoverUser,
                TelegramBotToken = telegramToken,
                TelegramChatId = telegramChat,
                OnSafetyEvent = onSafety,
            });
            return store;
        }

        private static NotificationDto Note(NotificationSeverity sev,
                NotificationCategory cat = NotificationCategory.Safety) => new(
            Id: Guid.NewGuid(), PostedUtc: DateTimeOffset.UtcNow, Severity: sev, Category: cat,
            Title: "Flip failed", Message: "The mount is parked.", Read: false, Dismissed: false,
            DismissedUtc: null, Payload: null, RelatedEntityType: null, RelatedEntityId: null);

        private static void AwaitSends(CapturingHandler handler, int expected) {
            // ForwardInBackground is deliberately fire-and-forget; poll briefly for the sends.
            for (var i = 0; i < 100 && handler.Requests.Count < expected; i++) {
                Thread.Sleep(20);
            }
        }

        [Test]
        public void A_critical_safety_note_reaches_both_configured_channels_with_the_right_shapes() {
            using var handler = new CapturingHandler();
            using var svc = new PushChannelService(
                StoreWith("app-tok", "user-key", "bot-tok", "chat-42"), handler: handler);

            svc.ForwardInBackground(Note(NotificationSeverity.Critical));
            AwaitSends(handler, 2);

            Assert.That(handler.Requests, Has.Count.EqualTo(2));
            var pushover = handler.Requests.Single(r => r.Uri.Host == "api.pushover.net");
            Assert.That(pushover.Body, Does.Contain("token=app-tok"));
            Assert.That(pushover.Body, Does.Contain("user=user-key"));
            Assert.That(pushover.Body, Does.Contain("priority=1"),
                "Critical rides Pushover high priority so it bypasses device quiet hours");
            var telegram = handler.Requests.Single(r => r.Uri.Host == "api.telegram.org");
            Assert.That(telegram.Uri.AbsolutePath, Does.Contain("botbot-tok/sendMessage"));
            Assert.That(telegram.Body, Does.Contain("chat_id=chat-42"));
        }

        [Test]
        public void A_warning_sends_at_normal_priority() {
            using var handler = new CapturingHandler();
            using var svc = new PushChannelService(StoreWith("t", "u"), handler: handler);
            svc.ForwardInBackground(Note(NotificationSeverity.Warning));
            AwaitSends(handler, 1);
            Assert.That(handler.Requests.Single().Body, Does.Contain("priority=0"));
        }

        [Test]
        public void Info_never_pushes_and_a_half_configured_channel_never_sends() {
            using var handler = new CapturingHandler();
            using var svc = new PushChannelService(
                StoreWith(pushoverToken: "app-tok", telegramChat: "chat-42"), handler: handler);

            svc.ForwardInBackground(Note(NotificationSeverity.Info));       // severity gate
            svc.ForwardInBackground(Note(NotificationSeverity.Critical));   // both channels half-configured
            Thread.Sleep(150);

            Assert.That(handler.Requests, Is.Empty,
                "each channel needs BOTH of its values; Info is housekeeping, not a page");
        }

        [Test]
        public void The_per_trigger_toggle_gates_the_push() {
            using var handler = new CapturingHandler();
            using var svc = new PushChannelService(
                StoreWith("t", "u", onSafety: false), handler: handler);
            svc.ForwardInBackground(Note(NotificationSeverity.Critical));
            Thread.Sleep(150);
            Assert.That(handler.Requests, Is.Empty);
        }

        [Test]
        public void A_provider_failure_is_swallowed_and_the_other_channel_still_sends() {
            using var handler = new CapturingHandler { Status = HttpStatusCode.InternalServerError };
            using var svc = new PushChannelService(
                StoreWith("t", "u", "bt", "ch"), handler: handler);
            Assert.DoesNotThrow(() => svc.ForwardInBackground(Note(NotificationSeverity.Critical)));
            AwaitSends(handler, 2);
            Assert.That(handler.Requests, Has.Count.EqualTo(2),
                "a failing Pushover must not stop the Telegram attempt (and vice versa)");
        }

        [Test]
        public void Trigger_mapping_matches_the_documented_category_routing() {
            var s = new InMemoryProfileStore().GetNotificationsSettings() with {
                OnSafetyEvent = true, OnDiskSpaceLow = false,
                OnSequencePaused = true, OnCriticalDiagnostic = false,
            };
            Assert.That(PushChannelService.TriggerAllows(s, NotificationCategory.Safety), Is.True);
            Assert.That(PushChannelService.TriggerAllows(s, NotificationCategory.Storage), Is.False);
            Assert.That(PushChannelService.TriggerAllows(s, NotificationCategory.Sequence), Is.True);
            Assert.That(PushChannelService.TriggerAllows(s, NotificationCategory.Equipment), Is.False);
            Assert.That(PushChannelService.SeverityAllows(NotificationSeverity.Info), Is.False);
            Assert.That(PushChannelService.SeverityAllows(NotificationSeverity.Warning), Is.True);
        }
    }
}
