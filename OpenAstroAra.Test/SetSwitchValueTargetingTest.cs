#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Newtonsoft.Json;
using NUnit.Framework;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Equipment.MySwitch;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Sequencer.SequenceItem.Switch;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §10.6 Switch multi-instance PR3 — the <c>SetSwitchValue</c> instruction's device selector:
    /// the JSON contract (absent property → -1 primary, so every pre-PR3 / NINA-imported sequence
    /// keeps its behaviour), the clamp, Clone fidelity, and the Execute routing (named device →
    /// the <see cref="ISwitchDeviceTargeting"/> capability; -1 or a capability-less mediator →
    /// the single-target path).
    /// </summary>
    [TestFixture]
    public class SetSwitchValueTargetingTest {

        /// <summary>Records which mediator surface Execute used, and with what arguments.</summary>
        private sealed class RecordingMediator : ISwitchMediator, ISwitchDeviceTargeting {
            public (short Index, double Value)? SingleTargetCall;
            public (int Device, short Index, double Value)? TargetedCall;

            public Task SetSwitchValue(short switchIndex, double value, IProgress<ApplicationStatus> progress, CancellationToken ct) {
                SingleTargetCall = (switchIndex, value);
                return Task.CompletedTask;
            }

            public Task SetSwitchValue(int alpacaDeviceNumber, short switchIndex, double value, IProgress<ApplicationStatus> progress, CancellationToken ct) {
                TargetedCall = (alpacaDeviceNumber, switchIndex, value);
                return Task.CompletedTask;
            }

            public SwitchInfo GetInfo() => new() { Connected = false };
            public SwitchInfo GetInfo(int alpacaDeviceNumber) => new() { Connected = false };

            // Unused mediator plumbing.
            public void RegisterHandler(object handler) { }
            public void RegisterConsumer(ISwitchConsumer consumer) { }
            public void RemoveConsumer(ISwitchConsumer consumer) { }
            public Task<IList<string>> Rescan() => Task.FromResult<IList<string>>(new List<string>());
            public Task<bool> Connect() => Task.FromResult(false);
            public Task Disconnect() => Task.CompletedTask;
            public void Broadcast(SwitchInfo deviceInfo) { }
            public string Action(string actionName, string actionParameters) => string.Empty;
            public string SendCommandString(string command, bool raw = true) => string.Empty;
            public bool SendCommandBool(string command, bool raw = true) => false;
            public void SendCommandBlind(string command, bool raw = true) { }
            public IDevice GetDevice() => throw new NotSupportedException();
            public event Func<object, EventArgs, Task>? Connected { add { } remove { } }
            public event Func<object, EventArgs, Task>? Disconnected { add { } remove { } }
        }

        /// <summary>A mediator WITHOUT the targeting capability — the headless-stub shape.</summary>
        private sealed class SingleTargetOnlyMediator : ISwitchMediator {
            public (short Index, double Value)? SingleTargetCall;

            public Task SetSwitchValue(short switchIndex, double value, IProgress<ApplicationStatus> progress, CancellationToken ct) {
                SingleTargetCall = (switchIndex, value);
                return Task.CompletedTask;
            }

            public SwitchInfo GetInfo() => new() { Connected = false };
            public void RegisterHandler(object handler) { }
            public void RegisterConsumer(ISwitchConsumer consumer) { }
            public void RemoveConsumer(ISwitchConsumer consumer) { }
            public Task<IList<string>> Rescan() => Task.FromResult<IList<string>>(new List<string>());
            public Task<bool> Connect() => Task.FromResult(false);
            public Task Disconnect() => Task.CompletedTask;
            public void Broadcast(SwitchInfo deviceInfo) { }
            public string Action(string actionName, string actionParameters) => string.Empty;
            public string SendCommandString(string command, bool raw = true) => string.Empty;
            public bool SendCommandBool(string command, bool raw = true) => false;
            public void SendCommandBlind(string command, bool raw = true) { }
            public IDevice GetDevice() => throw new NotSupportedException();
            public event Func<object, EventArgs, Task>? Connected { add { } remove { } }
            public event Func<object, EventArgs, Task>? Disconnected { add { } remove { } }
        }

        [Test]
        public void Defaults_to_the_primary_device_and_clamps_below_minus_one() {
            var item = new SetSwitchValue(new RecordingMediator());
            Assert.That(item.AlpacaDeviceNumber, Is.EqualTo(-1), "the pre-PR3-compatible default");
            item.AlpacaDeviceNumber = -7;
            Assert.That(item.AlpacaDeviceNumber, Is.EqualTo(-1), "below -1 normalizes to -1");
            item.AlpacaDeviceNumber = 3;
            Assert.That(item.AlpacaDeviceNumber, Is.EqualTo(3));
        }

        [Test]
        public void Clone_carries_the_device_selector() {
            var item = new SetSwitchValue(new RecordingMediator()) {
                SwitchIndex = 2, Value = 55, AlpacaDeviceNumber = 4,
            };
            var clone = (SetSwitchValue)item.Clone();
            Assert.That(clone.AlpacaDeviceNumber, Is.EqualTo(4));
            Assert.That(clone.SwitchIndex, Is.EqualTo((short)2));
            Assert.That(clone.Value, Is.EqualTo(55));
        }

        [Test]
        public void Json_without_the_property_deserializes_to_primary() {
            // Every pre-PR3 ARA sequence and every NINA import carries no AlpacaDeviceNumber —
            // populating such JSON must leave the default -1 (the old single-target behaviour).
            var item = new SetSwitchValue(new RecordingMediator()) { AlpacaDeviceNumber = 5 };
            JsonConvert.PopulateObject(/*lang=json*/ """{"SwitchIndex": 1, "Value": 42.0}""", item);
            Assert.That(item.AlpacaDeviceNumber, Is.EqualTo(5),
                "an absent property must not touch the value at all");

            var fresh = new SetSwitchValue(new RecordingMediator());
            JsonConvert.PopulateObject(/*lang=json*/ """{"SwitchIndex": 1, "Value": 42.0}""", fresh);
            Assert.That(fresh.AlpacaDeviceNumber, Is.EqualTo(-1));
        }

        [Test]
        public void Json_round_trips_the_device_selector() {
            var item = new SetSwitchValue(new RecordingMediator()) {
                SwitchIndex = 1, Value = 42, AlpacaDeviceNumber = 2,
            };
            var json = JsonConvert.SerializeObject(item);
            Assert.That(json, Does.Contain("\"AlpacaDeviceNumber\":2"));

            var rehydrated = new SetSwitchValue(new RecordingMediator());
            JsonConvert.PopulateObject(json, rehydrated);
            Assert.That(rehydrated.AlpacaDeviceNumber, Is.EqualTo(2));
            Assert.That(rehydrated.SwitchIndex, Is.EqualTo((short)1));
            Assert.That(rehydrated.Value, Is.EqualTo(42));
        }

        [Test]
        public async Task Execute_routes_a_named_device_through_the_targeting_capability() {
            var mediator = new RecordingMediator();
            var item = new SetSwitchValue(mediator) {
                SwitchIndex = 2, Value = 55, AlpacaDeviceNumber = 4,
            };
            await item.Execute(progress: null!, CancellationToken.None);
            Assert.That(mediator.TargetedCall, Is.EqualTo((4, (short)2, 55.0)));
            Assert.That(mediator.SingleTargetCall, Is.Null);
        }

        [Test]
        public async Task Execute_keeps_the_single_target_path_for_the_primary_default() {
            var mediator = new RecordingMediator();
            var item = new SetSwitchValue(mediator) { SwitchIndex = 1, Value = 7 };
            await item.Execute(progress: null!, CancellationToken.None);
            Assert.That(mediator.SingleTargetCall, Is.EqualTo(((short)1, 7.0)));
            Assert.That(mediator.TargetedCall, Is.Null);
        }

        [Test]
        public async Task Execute_degrades_to_single_target_when_the_mediator_lacks_the_capability() {
            var mediator = new SingleTargetOnlyMediator();
            var item = new SetSwitchValue(mediator) {
                SwitchIndex = 1, Value = 7, AlpacaDeviceNumber = 4,
            };
            await item.Execute(progress: null!, CancellationToken.None);
            Assert.That(mediator.SingleTargetCall, Is.EqualTo(((short)1, 7.0)),
                "a capability-less mediator (headless stub) keeps working — primary semantics");
        }
    }
}
