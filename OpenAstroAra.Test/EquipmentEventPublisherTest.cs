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
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>§60.9 — the equipment.* connection events. The catalog carried these
    /// from day one but nothing emitted them; the publisher is the one emitter, and
    /// device services call it from their SetState choke points.</summary>
    [TestFixture]
    public class EquipmentEventPublisherTest {

        private sealed class RecordingBroadcaster : IWsBroadcaster {
            public List<(string Type, JsonElement Payload)> Events { get; } = new();
            public bool ThrowSync { get; set; }
            public bool FaultAsync { get; set; }
            public long CurrentSequence => Events.Count;

            public Task PublishAsync(string eventType, JsonElement payload, CancellationToken ct) {
                if (ThrowSync) {
                    throw new InvalidOperationException("channel gone");
                }
                if (FaultAsync) {
                    return Task.FromException(new InvalidOperationException("late fault"));
                }
                lock (Events) {
                    Events.Add((eventType, payload.Clone()));
                }
                return Task.CompletedTask;
            }
        }

        private static (EquipmentEventPublisher Publisher, RecordingBroadcaster Broadcaster) NewPublisher() {
            var broadcaster = new RecordingBroadcaster();
            return (new EquipmentEventPublisher(broadcaster), broadcaster);
        }

        [Test]
        public void Removed_publishes_state_changed_flagged_removed_and_no_alias() {
            // #1127 — the card's Remove: the same event the clients refresh on, so a second open
            // client re-reads the type and drops the card; no connection alias, it is not a transition.
            var (publisher, broadcaster) = NewPublisher();
            publisher.Removed(DeviceType.Focuser, "foc-1", "Bench focuser");
            Assert.That(broadcaster.Events, Has.Count.EqualTo(1));
            var (type, payload) = broadcaster.Events[0];
            Assert.That(type, Is.EqualTo(WsEventCatalog.EquipmentStateChanged));
            Assert.That(payload.GetProperty("device_type").GetString(), Is.EqualTo("focuser"));
            Assert.That(payload.GetProperty("device_id").GetString(), Is.EqualTo("foc-1"));
            Assert.That(payload.GetProperty("state").GetString(), Is.EqualTo("disconnected"));
            Assert.That(payload.GetProperty("removed").GetBoolean(), Is.True);
        }

        [Test]
        public void Connected_publishes_state_changed_plus_the_connected_alias() {
            var (publisher, broadcaster) = NewPublisher();

            publisher.StateChanged(DeviceType.Telescope, "dev-1", "EQ6-R", EquipmentConnectionState.Connected);

            Assert.That(broadcaster.Events, Has.Count.EqualTo(2));
            Assert.That(broadcaster.Events[0].Type, Is.EqualTo(WsEventCatalog.EquipmentStateChanged));
            Assert.That(broadcaster.Events[1].Type, Is.EqualTo(WsEventCatalog.EquipmentConnected));
            var payload = broadcaster.Events[0].Payload;
            Assert.That(payload.GetProperty("device_type").GetString(), Is.EqualTo("telescope"),
                "§60.6 all-lowercase enum token");
            Assert.That(payload.GetProperty("device_id").GetString(), Is.EqualTo("dev-1"));
            Assert.That(payload.GetProperty("device_name").GetString(), Is.EqualTo("EQ6-R"));
            Assert.That(payload.GetProperty("state").GetString(), Is.EqualTo("connected"));
        }

        [TestCase(EquipmentConnectionState.Disconnected, WsEventCatalog.EquipmentDisconnected)]
        [TestCase(EquipmentConnectionState.Error, WsEventCatalog.EquipmentConnectionFailed)]
        public void Notable_states_publish_their_alias(EquipmentConnectionState state, string alias) {
            var (publisher, broadcaster) = NewPublisher();

            publisher.StateChanged(DeviceType.Camera, "dev-2", "ASI2600", state);

            Assert.That(broadcaster.Events, Has.Count.EqualTo(2));
            Assert.That(broadcaster.Events[1].Type, Is.EqualTo(alias));
        }

        [Test]
        public void Connecting_publishes_only_state_changed() {
            var (publisher, broadcaster) = NewPublisher();

            publisher.StateChanged(DeviceType.Focuser, "dev-3", "EAF", EquipmentConnectionState.Connecting);

            Assert.That(broadcaster.Events, Has.Count.EqualTo(1));
            Assert.That(broadcaster.Events[0].Type, Is.EqualTo(WsEventCatalog.EquipmentStateChanged));
        }

        [Test]
        public void Null_device_identity_survives_a_teardown_race() {
            var (publisher, broadcaster) = NewPublisher();

            publisher.StateChanged(DeviceType.Dome, null, null, EquipmentConnectionState.Disconnected);

            var payload = broadcaster.Events[0].Payload;
            Assert.That(payload.GetProperty("device_id").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(payload.GetProperty("device_name").ValueKind, Is.EqualTo(JsonValueKind.Null));
        }

        [Test]
        public void A_throwing_broadcaster_never_propagates_into_the_connect_path() {
            var (publisher, broadcaster) = NewPublisher();
            broadcaster.ThrowSync = true;

            Assert.DoesNotThrow(() => publisher.StateChanged(
                DeviceType.Switch, "dev-4", "Pegasus", EquipmentConnectionState.Connected));
        }

        [Test]
        public void A_faulted_broadcast_task_is_observed_not_propagated() {
            var (publisher, broadcaster) = NewPublisher();
            broadcaster.FaultAsync = true;

            Assert.DoesNotThrow(() => publisher.StateChanged(
                DeviceType.Rotator, "dev-5", "Falcon", EquipmentConnectionState.Connected));
        }

        [Test]
        public void Catalog_carries_every_event_the_publisher_can_emit() {
            Assert.That(WsEventCatalog.All, Does.Contain(WsEventCatalog.EquipmentStateChanged));
            Assert.That(WsEventCatalog.All, Does.Contain(WsEventCatalog.EquipmentConnected));
            Assert.That(WsEventCatalog.All, Does.Contain(WsEventCatalog.EquipmentDisconnected));
            Assert.That(WsEventCatalog.All, Does.Contain(WsEventCatalog.EquipmentConnectionFailed));
        }

        [Test]
        public void ExposureStarted_publishes_frame_exposure_start_kind_and_filter() {
            var (publisher, broadcaster) = NewPublisher();
            var id = Guid.NewGuid();
            var started = new DateTimeOffset(2026, 10, 4, 21, 30, 15, 250, TimeSpan.Zero);
            publisher.ExposureStarted(id, 120.5, started, "light", "Ha");
            Assert.That(broadcaster.Events, Has.Count.EqualTo(1));
            var (type, payload) = broadcaster.Events[0];
            Assert.That(type, Is.EqualTo(WsEventCatalog.CameraExposureStarted));
            Assert.That(payload.GetProperty("frame_id").GetString(), Is.EqualTo(id.ToString()));
            Assert.That(payload.GetProperty("exposure_sec").GetDouble(), Is.EqualTo(120.5));
            Assert.That(payload.GetProperty("started_utc").GetString(), Does.StartWith("2026-10-04T21:30:15.25"));
            Assert.That(payload.GetProperty("kind").GetString(), Is.EqualTo("light"));
            Assert.That(payload.GetProperty("filter_name").GetString(), Is.EqualTo("Ha"));
        }

        [Test]
        public void ExposureStarted_without_a_filter_carries_a_null_filter_name() {
            var (publisher, broadcaster) = NewPublisher();
            publisher.ExposureStarted(Guid.NewGuid(), 2, DateTimeOffset.UtcNow, "analysis", null);
            var (_, payload) = broadcaster.Events[0];
            Assert.That(payload.GetProperty("filter_name").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(payload.GetProperty("kind").GetString(), Is.EqualTo("analysis"));
        }

        [Test]
        public void ExposureComplete_publishes_the_elapsed_window() {
            var (publisher, broadcaster) = NewPublisher();
            var id = Guid.NewGuid();
            publisher.ExposureComplete(id, 30, DateTimeOffset.UtcNow, "flat", 31250);
            var (type, payload) = broadcaster.Events[0];
            Assert.That(type, Is.EqualTo(WsEventCatalog.CameraExposureComplete));
            Assert.That(payload.GetProperty("frame_id").GetString(), Is.EqualTo(id.ToString()));
            Assert.That(payload.GetProperty("exposure_sec").GetDouble(), Is.EqualTo(30));
            Assert.That(payload.GetProperty("kind").GetString(), Is.EqualTo("flat"));
            Assert.That(payload.GetProperty("elapsed_ms").GetInt64(), Is.EqualTo(31250));
        }

        [Test]
        public void ExposureFailed_publishes_the_reason() {
            var (publisher, broadcaster) = NewPublisher();
            var id = Guid.NewGuid();
            publisher.ExposureFailed(id, "light", "cancelled");
            var (type, payload) = broadcaster.Events[0];
            Assert.That(type, Is.EqualTo(WsEventCatalog.CameraExposureFailed));
            Assert.That(payload.GetProperty("frame_id").GetString(), Is.EqualTo(id.ToString()));
            Assert.That(payload.GetProperty("kind").GetString(), Is.EqualTo("light"));
            Assert.That(payload.GetProperty("reason").GetString(), Is.EqualTo("cancelled"));
        }

        [Test]
        public void Exposure_events_swallow_a_sync_broadcaster_fault() {
            var (publisher, broadcaster) = NewPublisher();
            broadcaster.ThrowSync = true;
            Assert.DoesNotThrow(() => publisher.ExposureStarted(Guid.NewGuid(), 1, DateTimeOffset.UtcNow, "light", null));
            Assert.DoesNotThrow(() => publisher.ExposureComplete(Guid.NewGuid(), 1, DateTimeOffset.UtcNow, "light", 1000));
            Assert.DoesNotThrow(() => publisher.ExposureFailed(Guid.NewGuid(), "light", "x"));
        }

    }
}
