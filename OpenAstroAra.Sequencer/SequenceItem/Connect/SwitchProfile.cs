using Newtonsoft.Json;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Sequencer.Connections;
using OpenAstroAra.Sequencer.Validations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.SequenceItem.Connect {
    /// <summary>
    /// The daemon runs one profile, so a sequence cannot switch it (#1545, ADR 0023): the item refuses before it
    /// touches a device, and a stored sequence that holds one still loads (ADR 0018).
    /// </summary>
    [JsonObject(MemberSerialization.OptIn)]
    public class SwitchProfile : SequenceItem, IValidatable {
        private const string RefusalMessage = "The daemon runs one profile; switch it from the Profiles page.";

        private IProfileService profileService;

        public SwitchProfile(IProfileService profileService) {
            this.profileService = profileService;
            SelectedProfileId = profileService.Profiles.FirstOrDefault()?.Id ?? Guid.Empty;
            Reconnect = true;
        }

        public IProfileService ProfileService => profileService;

        private Guid selectedProfileId;

        [JsonProperty]
        public Guid SelectedProfileId {
            get => selectedProfileId;
            set => SetProperty(ref selectedProfileId, value);
        }

        private bool reconnect;

        [JsonProperty]
        public bool Reconnect {
            get => reconnect;
            set => SetProperty(ref reconnect, value);
        }

        private SwitchProfile(SwitchProfile copyMe) : this(copyMe.profileService) {
            CopyMetaData(copyMe);
        }

        private IList<string> issues = new List<string>();
        public IList<string> Issues { get => issues; set { issues = value; RaisePropertyChanged(); } }

        public override object Clone() {
            var clone = new SwitchProfile(this) {
                SelectedProfileId = this.SelectedProfileId,
                Reconnect = this.Reconnect
            };

            return clone;
        }

        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            throw new EquipmentRefusedException(EquipmentRefusalCause.NotSupported, RefusalMessage);
        }

        public bool Validate() {
            Issues = new List<string> { RefusalMessage };
            return false;
        }

        public override string ToString() {
            return $"Category: {Category}, Item: {nameof(SwitchProfile)}, ProfileId: {SelectedProfileId}, Reconnect: {Reconnect}";
        }
    }
}