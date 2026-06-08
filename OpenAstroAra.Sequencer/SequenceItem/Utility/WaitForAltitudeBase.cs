using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.SequenceItem.Utility {
    using Newtonsoft.Json;
    using OpenAstroAra.Astrometry;
    using OpenAstroAra.Core.Enums;
    using OpenAstroAra.Profile.Interfaces;
    using System.Collections.Generic;

    public abstract class WaitForAltitudeBase : SequenceItem {

        private IList<string> issues = new List<string>();

        protected WaitForAltitudeBase(IProfileService profileService, bool useCustomHorizon) {
            ProfileService = profileService;
            Data = new WaitLoopData(profileService, useCustomHorizon, CalculateExpectedTime, GetType().Name);
        }

        public IProfileService ProfileService { get; set; }

        [JsonProperty]
        public WaitLoopData Data { get; set; }

        public IList<string> Issues {
            get => issues;
            private protected set {
                issues = value;
                RaisePropertyChanged();
            }
        }

        public abstract void CalculateExpectedTime();

        #region Obsolete Migration Properties

        // The following write-only setters exist solely to migrate legacy JSON sequences whose
        // values used to live directly on this item before they moved onto WaitLoopData (Data).
        // The JSON property names below are a serialization wire-contract for those old sequences
        // and must remain stable regardless of any C# symbol names.

        [JsonProperty(propertyName: "Comparator")]
        private ComparisonOperator DeprecatedComparator {
            set {
                switch (value) {
                    case ComparisonOperator.GreaterThanOrEqual:
                        value = ComparisonOperator.GreaterThan;
                        break;
                    case ComparisonOperator.LessThanOrEqual:
                        value = ComparisonOperator.LessThan;
                        break;
                }
                Data.Comparator = value;
            }
        }

        [JsonProperty(propertyName: "UserMoonAltitude")]
        private double DeprecatedUserMoonAltitude { set => Data.Offset = value; }

        [JsonProperty(propertyName: "UserSunAltitude")]
        private double DeprecatedUserSunAltitude { set => Data.Offset = value; }

        [JsonProperty(propertyName: "AltitudeOffset")]
        private double DeprecatedAltitudeOffset { set => Data.Offset = value; }

        [JsonProperty(propertyName: "Altitude")]
        private double DeprecatedAltitude { set => Data.Offset = value; }

        [JsonProperty(propertyName: "Coordinates")]
        private InputCoordinates DeprecatedCoordinates { set => Data.Coordinates = value; }
        #endregion
    }
}