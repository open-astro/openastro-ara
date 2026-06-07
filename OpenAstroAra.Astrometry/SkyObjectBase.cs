using OpenAstroAra.Astrometry.Interfaces;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Utility;
using OxyPlot;
using OxyPlot.Axes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
namespace OpenAstroAra.Astrometry {
    public abstract class SkyObjectBase : BaseINPC, IDeepSkyObject {
        [Obsolete]
        protected SkyObjectBase(string id, string imageRepository, CustomHorizon customHorizon) : this(id, null as Func<SkyObjectBase, Task<byte[]>>, customHorizon) {
        }

        protected SkyObjectBase(string id, Func<SkyObjectBase, Task<byte[]>>? imageFactory, CustomHorizon customHorizon) {
            Id = id;
            Name = id;
            this.customHorizon = customHorizon;
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
                RaisePropertyChanged("NameAsAscii");
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
                RaisePropertyChanged(nameof(Rotation));
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

        private List<DataPoint>? _altitudes;

        public List<DataPoint> Altitudes {
            get {
                if (_altitudes == null) {
                    _altitudes = new List<DataPoint>();
                    UpdateHorizonAndTransit();
                }
                return _altitudes;
            }
            private set {
                _altitudes = value;
                RaisePropertyChanged();
            }
        }

        private List<DataPoint>? _horizon;

        public List<DataPoint> Horizon {
            get {
                if (_horizon == null) {
                    _horizon = new List<DataPoint>();
                }
                return _horizon;
            }
            private set {
                _horizon = value;
                RaisePropertyChanged();
            }
        }

        private List<string>? _alsoKnownAs;

        public List<string> AlsoKnownAs {
            get {
                if (_alsoKnownAs == null) {
                    _alsoKnownAs = new List<string>();
                }
                return _alsoKnownAs;
            }
            set {
                _alsoKnownAs = value;
                RaisePropertyChanged();
            }
        }

        protected DateTime _referenceDate = DateTime.UtcNow;
        protected double _latitude;
        protected double _longitude;

        public void SetDateAndPosition(DateTime start, double latitude, double longitude) {
            this._referenceDate = start;
            this._latitude = latitude;
            this._longitude = longitude;
            this._altitudes = null;
        }

        public void SetCustomHorizon(CustomHorizon customHorizon) {
            this.customHorizon = customHorizon;
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
        protected CustomHorizon customHorizon;

        private readonly Func<SkyObjectBase, Task<byte[]>>? imageFactory;

        public byte[]? Image {
            get {
                if (_image == null) {
                    if (imageFactory != null) {
                        _ = Task.Run(async () => {
                            _image = await Task.Run(() => imageFactory(this));
                            // .Freeze() was the WPF BitmapSource thread-affinity
                            // release; byte[] is immutable so nothing to do.
                            RaisePropertyChanged(nameof(Image));
                        });
                    }
                }
                return _image;
            }
        }
    }
}