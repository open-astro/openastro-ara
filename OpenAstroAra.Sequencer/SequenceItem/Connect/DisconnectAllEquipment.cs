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
    public sealed class DisconnectAllEquipment : SequenceItem, IValidatable {
        private readonly IEquipmentConnections connections;

        public DisconnectAllEquipment(IEquipmentConnections connections) {
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
        }

        public IReadOnlyList<string> Devices { get; }

        private DisconnectAllEquipment(DisconnectAllEquipment copyMe) : this(copyMe.connections) {
            CopyMetaData(copyMe);
        }

        private IList<string> issues = new List<string>();
        public IList<string> Issues { get => issues; set { issues = value; RaisePropertyChanged(); } }

        public override object Clone() {
            var clone = new DisconnectAllEquipment(this) {
            };

            return clone;
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            var errors = new List<Exception>();

            foreach (var device in Devices) {
                if (ConnectEquipment.RoleOf(device) is not { } role) { continue; }

                if (!connections.IsConnected(role)) {
                    Logger.Info($"{device} is already disconnected");
                    continue;
                }
                try {
                    await connections.DisconnectAsync(role, token).ConfigureAwait(false);
                } catch (EquipmentRefusedException ex) when (ex.Cause == EquipmentRefusalCause.NotKnown) {
                    Logger.Info($"{device} is not known to this rig, so there is nothing to disconnect");
                } catch (SequenceEntityFailedException ex) {
                    errors.Add(ex);
                }
            }

            if (errors.Count > 0) {
                throw new AggregateException(errors);
            }
        }

        public bool Validate() {
            return true;
        }

        public override string ToString() {
            return $"Category: {Category}, Item: {nameof(DisconnectAllEquipment)}";
        }
    }
}