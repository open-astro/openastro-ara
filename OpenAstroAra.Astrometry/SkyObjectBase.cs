using OpenAstroAra.Astrometry.Interfaces;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Utility;
using OxyPlot;
using OxyPlot.Axes;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
namespace OpenAstroAra.Astrometry {
    public abstract class SkyObjectBase : BaseINPC, IDeepSkyObject {
        [Obsolete("The imageRepository overload is retained for source compatibility; the headless build ignores imageRepository. Use the imageFactory overload.")]
        protected SkyObjectBase(string id, string imageRepository, CustomHorizon? customHorizon) : this(id, null as Func<SkyObjectBase, Task<byte[]>>, customHorizon) {
        }

        protected SkyObjectBase(string id, Func<SkyObjectBase, Task<byte[]>>? imageFactory, CustomHorizon? customHorizon) {
            Id = id;
            Name = id;
            this.CustomHorizon = customHorizon;
            this.imageFactory = imageFactory;
        }

        private string id = string.Empty;

        public string Id {
            get => id;
            set {
                id = value;
                RaisePropertyChanged();
            }
        }

        private string _name = string.Empty;

        public string Name {
            get => _name;
            set {
                _name = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(NameAsAscii));
            }
        }

        public string NameAsAscii => TextEncoding.UnicodeToAscii(TextEncoding.GreekToLatinAbbreviation(_name));

        public abstract Coordinates Coordinates { get; set; }
        public abstract Coordinates CoordinatesAt(DateTime at);
        public abstract SiderealShiftTrackingRate ShiftTrackingRate { get; }
        public abstract SiderealShiftTrackingRate ShiftTrackingRateAt(DateTime at);

        private string _dSOType = string.Empty;

        public string DSOType {
            get => _dSOType;
            set {
                _dSOType = value;
                RaisePropertyChanged();
            }
        }

        private string _constellation = string.Empty;

        public string Constellation {
            get => _constellation;
            set {
                _constellation = value;
                RaisePropertyChanged();
            }
        }

        private double? _magnitude;

        public double? Magnitude {
            get => _magnitude;
            set {
                _magnitude = value;
                RaisePropertyChanged();
            }
        }

        private Angle _positionAngle = Angle.Zero;

        public Angle PositionAngle {
            get => _positionAngle;
            set {
                _positionAngle = value;
                RaisePropertyChanged();
            }
        }

        private double? _sizeMin;

        public double? SizeMin {
            get => _sizeMin;
            set {
                _sizeMin = value;
                RaisePropertyChanged();
            }
        }

        private double? _size;

        public double? Size {
            get => _size;
            set {
                _size = value;
                RaisePropertyChanged();
            }
        }

        private double? _surfaceBrightness;

        public double? SurfaceBrightness {
            get => _surfaceBrightness;
            set {
                _surfaceBrightness = value;
                RaisePropertyChanged();
            }
        }

        [Obsolete("Use RotationPositionAngle instead")]
        public double Rotation {
            get => AstroUtil.EuclidianModulus(360 - RotationPositionAngle, 360);
            set {
                RotationPositionAngle = AstroUtil.EuclidianModulus(360 - value, 360);
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(RotationPositionAngle));
            }
        }

        private double rotationRotationPositionAngle;

        public double RotationPositionAngle {
            get => rotationRotationPositionAngle;
            set {
                rotationRotationPositionAngle = value;
                RaisePropertyChanged();
                // Rotation is a retained obsolete alias of RotationPositionAngle;
                // still notify it so legacy bindings update.
#pragma warning disable CS0618
                RaisePropertyChanged(nameof(Rotation));
#pragma warning restore CS0618
            }
        }

        private DataPoint _maxAltitude;

        public DataPoint MaxAltitude {
            get => _maxAltitude;
            protected set {
                _maxAltitude = value;
                RaisePropertyChanged();
            }
        }

        private Collection<DataPoint>? _altitudes;

        public Collection<DataPoint> Altitudes {
            get {
                if (_altitudes == null) {
                    _altitudes = new Collection<DataPoint>();
                    UpdateHorizonAndTransit();
                }
                return _altitudes;
            }
            private set {
                _altitudes = value;
                RaisePropertyChanged();
            }
        }

        private Collection<DataPoint>? _horizon;

        public Collection<DataPoint> Horizon {
            get {
                if (_horizon == null) {
                    _horizon = new Collection<DataPoint>();
                }
                return _horizon;
            }
            private set {
                _horizon = value;
                RaisePropertyChanged();
            }
        }

        private Collection<string>? _alsoKnownAs;

        public Collection<string> AlsoKnownAs => _alsoKnownAs ??= new Collection<string>();

        // CA1051: expose the shared inheritance state as properties rather than visible fields.
        public DateTime ReferenceDate { get; set; } = DateTime.UtcNow;
        protected double Latitude { get; set; }
        protected double Longitude { get; set; }

        public void SetDateAndPosition(DateTime start, double latitude, double longitude) {
            this.ReferenceDate = start;
            this.Latitude = latitude;
            this.Longitude = longitude;
            this._altitudes = null;
        }

        public void SetCustomHorizon(CustomHorizon? customHorizon) {
            this.CustomHorizon = customHorizon;
            this.UpdateHorizonAndTransit();
        }

        protected abstract void UpdateHorizonAndTransit();

        private bool _doesTransitSouth;

        public bool DoesTransitSouth {
            get => _doesTransitSouth;
            protected set {
                _doesTransitSouth = value;
                RaisePropertyChanged();
            }
        }

        private byte[]? _image;
        protected CustomHorizon? CustomHorizon { get; set; }

        private readonly Func<SkyObjectBase, Task<byte[]>>? imageFactory;

        // CA1819: a byte[] payload is exposed as a method rather than a property.
        // Lazily loads the image bytes from the factory on first request.
        public async Task<byte[]?> GetImageAsync() {
            if (_image == null && imageFactory != null) {
                _image = await imageFactory(this);
            }
            return _image;
        }
    }
}