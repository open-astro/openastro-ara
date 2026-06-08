using Newtonsoft.Json;
using OpenAstroAra.Astrometry.Interfaces;
using OpenAstroAra.Core.Locale;
using OpenAstroAra.Core.Utility;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.Utility.DateTimeProvider {
    [JsonObject(MemberSerialization.OptIn)]
    public class CivilDuskProvider : IDateTimeProvider {
        private INighttimeCalculator nighttimeCalculator;

        public CivilDuskProvider(INighttimeCalculator nighttimeCalculator) {
            this.nighttimeCalculator = nighttimeCalculator;
        }

        public string Name { get; } = Loc.Instance["LblCivilDusk"];
        public ICustomDateTime DateTime { get; set; } = new SystemDateTime();

        public DateTime GetDateTime(ISequenceEntity context) {
            var night = nighttimeCalculator.Calculate().CivilTwilightRiseAndSet?.Set;
            if (!night.HasValue) {
                throw new TimeProviderException("No civil dusk", Loc.Instance["Lbl_TimeProvider_NoCivilDusk"]);
            }
            return night.Value;
        }
        public TimeOnly GetRolloverTime(ISequenceEntity context) {
            var dawn = nighttimeCalculator.Calculate().SunRiseAndSet?.Rise;
            if (!dawn.HasValue) {
                return new TimeOnly(12, 0, 0);
            }
            return TimeOnly.FromDateTime(dawn.Value);
        }
    }
}