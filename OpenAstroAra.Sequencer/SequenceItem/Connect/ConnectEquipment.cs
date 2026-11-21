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
    public class ConnectEquipment : SequenceItem, IValidatable {
        private readonly IEquipmentConnections connections;

        [OnDeserialized]
        public void OnDeserialized(StreamingContext context) {
            if (SelectedDevice == "Telescope") {
                SelectedDevice = "Mount";
            }
        }

        public ConnectEquipment(IEquipmentConnections connections) {
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
                Validate();
                RaisePropertyChanged();
            }
        }

        /// <summary>True for the Reconnect trigger's instruction: it recovers, so a device the user disconnected
        /// stays off. Not serialized (ADR 0018): the trigger sets it in its constructor.</summary>
        internal bool Recover { get; init; }

        /// <summary>The role <see cref="SelectedDevice"/> names, or null for a name no role has.</summary>
        public EquipmentRole? Role => SelectedDevice switch {
            "Camera" => EquipmentRole.Camera,
            "Filter Wheel" => EquipmentRole.FilterWheel,
            "Focuser" => EquipmentRole.Focuser,
            "Rotator" => EquipmentRole.Rotator,
            "Telescope" or "Mount" => EquipmentRole.Mount,
            "Guider" => EquipmentRole.Guider,
            "Switch" => EquipmentRole.Switch,
            "Flat Panel" => EquipmentRole.FlatPanel,
            "Weather" => EquipmentRole.Weather,
            "Dome" => EquipmentRole.Dome,
            "Safety Monitor" => EquipmentRole.SafetyMonitor,
            _ => null,
        };

        private ConnectEquipment(ConnectEquipment copyMe) : this(copyMe.connections) {
            Recover = copyMe.Recover;
            CopyMetaData(copyMe);
        }

        private IList<string> issues = new List<string>();
        public IList<string> Issues { get => issues; set { issues = value; RaisePropertyChanged(); } }

        public override object Clone() {
            var clone = new ConnectEquipment(this) {
                SelectedDevice = this.SelectedDevice
            };

            return clone;
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            if (Role is not { } role) {
                throw new SequenceEntityFailedException($"Failed to connect to {SelectedDevice} as it is not a known device");
            }
            if (connections.IsConnected(role)) {
                Logger.Info($"{SelectedDevice} is already connected");
                return;
            }
            await (Recover ? connections.RecoverAsync(role, token) : connections.ConnectAsync(role, token)).ConfigureAwait(false);
        }

        public bool IsConnected() => Role is { } role && connections.IsConnected(role);

        /// <summary>True when the user disconnected the selected device, so the Reconnect trigger stays quiet.</summary>
        public bool IsDisconnectedByUser() => Role is { } role && connections.IsDisconnectedByUser(role);

        public bool Validate() {
            var i = new List<string>();
            if (Role is null) {
                i.Add($"{SelectedDevice} is not a device the Connect Equipment instruction knows.");
            }
            Issues = i;
            return i.Count == 0;
        }

        public override string ToString() {
            return $"Category: {Category}, Item: {nameof(ConnectEquipment)}, Selected device: {SelectedDevice}";
        }
    }
}