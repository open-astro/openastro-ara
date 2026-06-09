using OpenAstroAra.Astrometry.Body;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OpenAstroAra.Astrometry.RiseAndSet {
    public class SunCustomRiseAndSet : RiseAndSetEvent {
        [Obsolete("Use method with elevation parameter instead")]
        public SunCustomRiseAndSet(DateTime date, double latitude, double longitude, double sunAltitude) : this(date, latitude, longitude, elevation: 0, sunAltitude) { }
        public SunCustomRiseAndSet(DateTime date, double latitude, double longitude, double elevation, double sunAltitude) : base(date, latitude, longitude, elevation) {
            SunAltitude = sunAltitude;
        }

        private double SunAltitude { get; }

        protected override double AdjustAltitude(BasicBody body) {
            return body.Altitude - SunAltitude;
        }

        protected override BasicBody GetBody(DateTime dateTime) {
            return new Sun(dateTime, Latitude, Longitude, Elevation);
        }
    }
}