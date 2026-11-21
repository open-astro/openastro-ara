using Newtonsoft.Json;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Sequencer.Connections;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.SequenceItem;
using OpenAstroAra.Sequencer.SequenceItem.Connect;
using OpenAstroAra.Sequencer.Validations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.Trigger.Connect {
    [JsonObject(MemberSerialization.OptIn)]
    public class ReconnectTrigger : SequenceTrigger, IValidatable {
        private readonly IEquipmentConnections connections;
        private ConnectEquipment connectEquipmentInstruction;

        [OnDeserialized]
        public void OnDeserialized(StreamingContext context) {
            if (SelectedDevice == "Telescope") {
                SelectedDevice = "Mount";
            }
            // The base [OnDeserializing] clears the ctor-added instruction before Newtonsoft
            // repopulates the runner from JSON, leaving two broken shapes (flagged on #746):
            // a WILMA-built node ships the runner EMPTY (Execute silently runs zero items and
            // the FAILED check reads an instruction that never ran), and a NINA export carries
            // its OWN ConnectEquipment instance that the trigger's SelectedDevice delegate and
            // Status check do NOT point at. Re-bind so the runner holds exactly the bound
            // instruction — SelectedDevice was already populated onto it via the [JsonProperty]
            // delegate, so the JSON's device survives either way.
            if (TriggerRunner != null) {
                TriggerRunner.Items.Clear();
                TriggerRunner.Add(connectEquipmentInstruction);
            }
        }

        public ReconnectTrigger(IEquipmentConnections connections) {
            this.connections = connections;
            // Set once, here: OnDeserialized re-adds this same instance, so the recover path survives a round trip.
            connectEquipmentInstruction = new ConnectEquipment(connections) { Recover = true };
            TriggerRunner.Add(connectEquipmentInstruction);
        }

        [JsonProperty]
        public string SelectedDevice {
            get => connectEquipmentInstruction.SelectedDevice;
            set {
                connectEquipmentInstruction.SelectedDevice = value;
                Validate();
                RaisePropertyChanged();
            }
        }

        public override bool AllowMultiplePerSet => true;

        public ConnectEquipment ConnectEquipmentInstruction { get => connectEquipmentInstruction; }

        private IList<string> issues = new List<string>();
        public IList<string> Issues { get => issues; set { issues = value; RaisePropertyChanged(); } }

        private ReconnectTrigger(ReconnectTrigger copyMe) : this(copyMe.connections) {
            CopyMetaData(copyMe);
            SelectedDevice = copyMe.SelectedDevice;
        }
        public override object Clone() {
            return new ReconnectTrigger(this);
        }

        public bool Validate() {
            var i = new List<string>();
            if (this.Parent is ISequenceRootContainer) {
                i.Add("The Reconnect Equipment Trigger should not be put into the global trigger section, but rather into specific sections where reconnection should be attempted");
            }
            connectEquipmentInstruction.Validate();
            i.AddRange(connectEquipmentInstruction.Issues);
            Issues = i;
            return Issues.Count == 0;
        }

        public override string ToString() {
            return $"Category: {Category}, Item: {nameof(ReconnectTrigger)}, Selected device: {connectEquipmentInstruction.SelectedDevice}";
        }

        public override bool ShouldTrigger(ISequenceItem? previousItem, ISequenceItem? nextItem) {
            var isConnected = connectEquipmentInstruction.IsConnected();
            // A device the user disconnected is not a dropped device: stay quiet instead of failing before every item.
            var byUser = !isConnected && connectEquipmentInstruction.IsDisconnectedByUser();
            Logger.Debug($"The {connectEquipmentInstruction.SelectedDevice} is {(isConnected ? "connected" : byUser ? "disconnected by the user" : "not connected. Trigger should fire.")}");
            return !isConnected && !byUser;
        }

        public override async Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) {
            await TriggerRunner.Run(progress, token);
            if (ConnectEquipmentInstruction.Status == Core.Enums.SequenceEntityStatus.FAILED) {
                throw new SequenceEntityFailedException($"Failed to connect to {ConnectEquipmentInstruction.SelectedDevice}");
            }
        }
    }
}