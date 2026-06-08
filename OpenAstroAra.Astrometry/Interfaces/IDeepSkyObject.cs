using OpenAstroAra.Core.Model;
using OxyPlot;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
namespace OpenAstroAra.Astrometry.Interfaces {
    public interface IDeepSkyObject : INotifyPropertyChanged {
        string Id { get; set; }
        string Name { get; set; }
        string NameAsAscii { get; }
        Coordinates Coordinates { get; set; }
        Coordinates CoordinatesAt(DateTime at);
        SiderealShiftTrackingRate ShiftTrackingRate { get; }
        SiderealShiftTrackingRate ShiftTrackingRateAt(DateTime at);
        string DSOType { get; set; }
        string Constellation { get; set; }
        double? Magnitude { get; set; }
        Angle PositionAngle { get; set; }
        double? SizeMin { get; set; }
        double? Size { get; set; }
        double? SurfaceBrightness { get; set; }

        [Obsolete("Use RotationPositionAngle instead")]
        double Rotation { get; set; }
        double RotationPositionAngle { get; set; }
        DataPoint MaxAltitude { get; }
        Collection<DataPoint> Altitudes { get; }
        Collection<DataPoint> Horizon { get; }
        Collection<string> AlsoKnownAs { get; }
        bool DoesTransitSouth { get; }

        [SuppressMessage("Performance", "CA1819:Properties should not return arrays",
            Justification = "Image is a raw binary image payload; byte[] is the natural representation and is what consumers (encoders/serializers) expect.")]
        byte[]? Image { get; }

        void SetDateAndPosition(DateTime start, double latitude, double longitude);
        void SetCustomHorizon(CustomHorizon customHorizon);
    }
}