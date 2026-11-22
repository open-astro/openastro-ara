using Newtonsoft.Json;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Sequencer.Connections;
using OpenAstroAra.Sequencer.Validations;
using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.SequenceItem.Connect {
    [JsonObject(MemberSerialization.OptIn)]
    public sealed class DisconnectEquipment : SequenceItem, IValidatable {
        private readonly IEquipmentConnections connections;

        [OnDeserialized]
        public void OnDeserialized(StreamingContext context) {
            if (SelectedDevice == "Telescope") {
                SelectedDevice = "Mount";
            }
        }

        public DisconnectEquipment(IEquipmentConnections connections) {
            this.connections = connections;
            Devices = new List<string>() {
                "Camera",
                "Filter Wheel",
                "Focuser",
                "Rotator",
                "Mount",
                "Guider",
                "Switch",
                "Flat Panel",
                "Weather",
                "Dome",
                "Safety Monitor"
            };
            SelectedDevice = "Camera";
        }

        public IReadOnlyList<string> Devices { get; }
        private string selectedDevice = string.Empty;

        [JsonProperty]
        public string SelectedDevice {
            get => selectedDevice;
            set {
                selectedDevice = value;
                RaisePropertyChanged();
            }
        }

        private DisconnectEquipment(DisconnectEquipment copyMe) : this(copyMe.connections) {
            CopyMetaData(copyMe);
        }

        private IList<string> issues = new List<string>();
        public IList<string> Issues { get => issues; set { issues = value; RaisePropertyChanged(); } }

        public override object Clone() {
            var clone = new DisconnectEquipment(this) {
                SelectedDevice = this.SelectedDevice
            };

            return clone;
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            if (ConnectEquipment.RoleOf(SelectedDevice) is not { } role) {
                return;
            }
            // Only a user disconnect already on record is skipped: a tripped or still-connecting device is not
            // Connected, yet the service must still record the disconnect and supersede its connect.
            if (connections.IsDisconnectedByUser(role)) {
                Logger.Info($"{SelectedDevice} is already disconnected");
                return;
            }
            try {
                await connections.DisconnectAsync(role, token).ConfigureAwait(false);
            } catch (EquipmentRefusedException ex) when (ex.Cause == EquipmentRefusalCause.NotKnown) {
                Logger.Info($"{SelectedDevice} is not known to this rig, so there is nothing to disconnect");
            }
        }

        public bool Validate() {
            var i = new List<string>();

            Issues = i;
            return i.Count == 0;
        }

        public override string ToString() {
            return $"Category: {Category}, Item: {nameof(DisconnectEquipment)}, Selected device: {SelectedDevice}";
        }
    }
}