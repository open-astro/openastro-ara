using System;

namespace OpenAstroAra.Astrometry {
    public class SiderealShiftTrackingRate {
        public static readonly SiderealShiftTrackingRate Disabled = CreateDisabled();
        public const double SiderealSecPerSiSec = 1.00273791552838d;
        public const double SiderealRate = 15.0d * SiderealSecPerSiSec;

        private SiderealShiftTrackingRate(bool enabled, double raDegreesPerHour, double decDegreesPerHour) {
            this.Enabled = enabled;
            this.RADegreesPerHour = raDegreesPerHour;
            this.DecDegreesPerHour = decDegreesPerHour;
        }

        public bool Enabled { get; private set; }
        public double RASecondsPerSiderealSecond => RAArcsecsPerSec / SiderealRate;
        public double RADegreesPerHour { get; private set; }
        public double DecDegreesPerHour { get; private set; }
        public double RAArcsecsPerHour => RADegreesPerHour * 3600.0d;
        public double DecArcsecsPerHour => DecDegreesPerHour * 3600.0d;
        public double RAArcsecsPerSec => RADegreesPerHour;
        public double DecArcsecsPerSec => DecDegreesPerHour;

        public static SiderealShiftTrackingRate CreateDisabled() {
            return new SiderealShiftTrackingRate(false, 0.0d, 0.0d);
        }

        public static SiderealShiftTrackingRate Create(double raDegreesPerHour, double decDegreesPerHour) {
            return new SiderealShiftTrackingRate(true, raDegreesPerHour, decDegreesPerHour);
        }

        public static SiderealShiftTrackingRate Create(Coordinates start, Coordinates end, TimeSpan between) {
            var raDiff = end.RADegrees - start.RADegrees;
            // Wrap into (-180, 180] so a move across the 0h/24h RA boundary tracks the short arc.
            raDiff -= 360.0d * Math.Ceiling((raDiff - 180.0d) / 360.0d);
            var decDiff = end.Dec - start.Dec;
            var hoursDiff = between.TotalSeconds / 3600.0d;
            return new SiderealShiftTrackingRate(true, raDiff / hoursDiff, decDiff / hoursDiff);
        }
    }
}