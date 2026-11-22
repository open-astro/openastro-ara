using Newtonsoft.Json;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Sequencer.Connections;
using OpenAstroAra.Sequencer.Validations;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.SequenceItem.Connect {
    /// <summary>
    /// Connects the rig through the connections port (#1545, ADR 0023): the Safety Monitor first, then every
    /// other role with a remembered or retained device, then the guider when the profile names a guider type.
    /// The guider is not Alpaca and not in the selection store, so the profile (not the rig's memory) says whether it is wanted.
    /// </summary>
    [JsonObject(MemberSerialization.OptIn)]
    public class ConnectAllEquipment : SequenceItem, IValidatable {
        private readonly IEquipmentConnections connections;
        private readonly IProfileService profileService;

        public ConnectAllEquipment(IEquipmentConnections connections, IProfileService profileService) {
            this.connections = connections;
            this.profileService = profileService;
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

        private ConnectAllEquipment(ConnectAllEquipment copyMe) : this(copyMe.connections, copyMe.profileService) {
            CopyMetaData(copyMe);
        }

        private IList<string> issues = new List<string>();
        public IList<string> Issues { get => issues; set { issues = value; RaisePropertyChanged(); } }

        public override object Clone() {
            var clone = new ConnectAllEquipment(this) {
            };

            return clone;
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            // A rig whose safety is unknown must not connect anything else (a remembered monitor that fails throws).
            await ConnectAsync("Safety Monitor", token).ConfigureAwait(false);

            var errors = new List<Exception>();
            foreach (var device in Devices) {
                if (device == "Safety Monitor") { continue; }
                if (device == "Guider" && profileService.ActiveProfile.GuiderSettings.GuiderName == "No_Guider") { continue; }
                try {
                    await ConnectAsync(device, token).ConfigureAwait(false);
                } catch (SequenceEntityFailedException ex) {
                    errors.Add(ex);
                }
            }

            if (errors.Count > 0) {
                throw new AggregateException(errors);
            }
        }

        private async Task ConnectAsync(string device, CancellationToken token) {
            if (ConnectEquipment.RoleOf(device) is not { } role) { return; }

            if (connections.IsConnected(role)) {
                Logger.Info($"{device} is already connected");
                return;
            }
            try {
                await connections.ConnectAsync(role, token).ConfigureAwait(false);
            } catch (EquipmentRefusedException ex) when (ex.Cause == EquipmentRefusalCause.NotKnown && role != EquipmentRole.Guider) {
                Logger.Info($"{device} is not known to this rig, so there is nothing to connect");
            } catch (SequenceEntityFailedException ex) {
                throw new SequenceEntityFailedException($"Failed to connect to {device}: {ex.Message}", ex);
            }
        }

        public bool Validate() {
            return true;
        }

        public override string ToString() {
            return $"Category: {Category}, Item: {nameof(ConnectAllEquipment)}";
        }
    }
}