using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Core.Model.Equipment;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Model;
using OpenAstroAra.Image.Interfaces;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Equipment.Equipment {
    public class OfflineDevice : IDevice, ICamera, IDome, IFilterWheel, IFlatDevice, IFocuser, IRotator, ISafetyMonitor, ISwitch, ITelescope, IWeatherData {
        private string originalName = string.Empty;

        public OfflineDevice(string id, string name) {
            originalName = name;
            if (string.IsNullOrWhiteSpace(name)) {
                Name = id + " (OFFLINE)";
            } else {
                Name = name + " (OFFLINE)";
            }
            Id = id;
        }

        public bool HasSetupDialog => false;

        public string Category { get; } = "OFFLINE";

        public string Id { get; private set; } = string.Empty;

        public string Name { get; private set; } = string.Empty;
        public string DisplayName => Name;

        public bool Connected => false;

        public string Description => string.Empty;

        public string DriverInfo => string.Empty;

        public string DriverVersion => string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

        public async Task<bool> Connect(CancellationToken token) {
            throw new InvalidOperationException($"Unable to connect to device '{originalName}' (ID: {Id}). Make sure it's plugged in, turned on, and set up correctly.");
        }

        public void Disconnect() {
        }

        public void SetupDialog() {
        }

        public IList<string> SupportedActions => new List<string>();

        public bool HasShutter => throw new InvalidOperationException("Device is offline.");

        public double Temperature => throw new InvalidOperationException("Device is offline.");

        public double TemperatureSetPoint { get => throw new InvalidOperationException("Device is offline."); set => throw new InvalidOperationException("Device is offline."); }
        public short BinX { get => throw new InvalidOperationException("Device is offline."); set => throw new InvalidOperationException("Device is offline."); }
        public short BinY { get => throw new InvalidOperationException("Device is offline."); set => throw new InvalidOperationException("Device is offline."); }

        public string SensorName => throw new InvalidOperationException("Device is offline.");

        public SensorType SensorType => throw new InvalidOperationException("Device is offline.");

        public short BayerOffsetX => throw new InvalidOperationException("Device is offline.");

        public short BayerOffsetY => throw new InvalidOperationException("Device is offline.");

        public int CameraXSize => throw new InvalidOperationException("Device is offline.");

        public int CameraYSize => throw new InvalidOperationException("Device is offline.");

        public double ExposureMin => throw new InvalidOperationException("Device is offline.");

        public double ExposureMax => throw new InvalidOperationException("Device is offline.");

        public short MaxBinX => throw new InvalidOperationException("Device is offline.");

        public short MaxBinY => throw new InvalidOperationException("Device is offline.");

        public double PixelSizeX => throw new InvalidOperationException("Device is offline.");

        public double PixelSizeY => throw new InvalidOperationException("Device is offline.");

        public bool CanSetTemperature => throw new InvalidOperationException("Device is offline.");

        public bool CoolerOn { get => throw new InvalidOperationException("Device is offline."); set => throw new InvalidOperationException("Device is offline."); }

        public double CoolerPower => throw new InvalidOperationException("Device is offline.");

        public bool HasDewHeater => throw new InvalidOperationException("Device is offline.");

        public bool DewHeaterOn { get => throw new InvalidOperationException("Device is offline."); set => throw new InvalidOperationException("Device is offline."); }

        public CameraStates CameraState => throw new InvalidOperationException("Device is offline.");

        public bool CanSubSample => throw new InvalidOperationException("Device is offline.");

        public bool EnableSubSample { get => throw new InvalidOperationException("Device is offline."); set => throw new InvalidOperationException("Device is offline."); }
        public int SubSampleX { get => throw new InvalidOperationException("Device is offline."); set => throw new InvalidOperationException("Device is offline."); }
        public int SubSampleY { get => throw new InvalidOperationException("Device is offline."); set => throw new InvalidOperationException("Device is offline."); }
        public int SubSampleWidth { get => throw new InvalidOperationException("Device is offline."); set => throw new InvalidOperationException("Device is offline."); }
        public int SubSampleHeight { get => throw new InvalidOperationException("Device is offline."); set => throw new InvalidOperationException("Device is offline."); }

        public bool CanShowLiveView => throw new InvalidOperationException("Device is offline.");

        public bool LiveViewEnabled => throw new InvalidOperationException("Device is offline.");

        public bool HasBattery => throw new InvalidOperationException("Device is offline.");

        public int BatteryLevel => throw new InvalidOperationException("Device is offline.");

        public int BitDepth => throw new InvalidOperationException("Device is offline.");

        public bool CanSetOffset => throw new InvalidOperationException("Device is offline.");

        public int Offset { get => throw new InvalidOperationException("Device is offline."); set => throw new InvalidOperationException("Device is offline."); }

        public int OffsetMin => throw new InvalidOperationException("Device is offline.");

        public int OffsetMax => throw new InvalidOperationException("Device is offline.");

        public bool CanSetUSBLimit => throw new InvalidOperationException("Device is offline.");

        public int USBLimit { get => throw new InvalidOperationException("Device is offline."); set => throw new InvalidOperationException("Device is offline."); }

        public int USBLimitMin => throw new InvalidOperationException("Device is offline.");

        public int USBLimitMax => throw new InvalidOperationException("Device is offline.");

        public int USBLimitStep => throw new InvalidOperationException("Device is offline.");

        public bool CanGetGain => throw new InvalidOperationException("Device is offline.");

        public bool CanSetGain => throw new InvalidOperationException("Device is offline.");

        public int GainMax => throw new InvalidOperationException("Device is offline.");

        public int GainMin => throw new InvalidOperationException("Device is offline.");

        public int Gain { get => throw new InvalidOperationException("Device is offline."); set => throw new InvalidOperationException("Device is offline."); }

        public double ElectronsPerADU => throw new InvalidOperationException("Device is offline.");

        public IList<string> ReadoutModes => throw new InvalidOperationException("Device is offline.");

        public short ReadoutMode { get => throw new InvalidOperationException("Device is offline."); set => throw new InvalidOperationException("Device is offline."); }
        public short ReadoutModeForSnapImages { get => throw new InvalidOperationException("Device is offline."); set => throw new InvalidOperationException("Device is offline."); }
        public short ReadoutModeForNormalImages { get => throw new InvalidOperationException("Device is offline."); set => throw new InvalidOperationException("Device is offline."); }

        public IList<int> Gains => throw new InvalidOperationException("Device is offline.");

        public AsyncObservableCollection<BinningMode> BinningModes => throw new InvalidOperationException("Device is offline.");

        public ShutterState ShutterStatus => throw new InvalidOperationException("Device is offline.");

        public bool DriverCanFollow => throw new InvalidOperationException("Device is offline.");

        public bool CanSetShutter => throw new InvalidOperationException("Device is offline.");

        public bool CanSetPark => throw new InvalidOperationException("Device is offline.");

        public bool CanSetAzimuth => throw new InvalidOperationException("Device is offline.");

        public bool CanSyncAzimuth => throw new InvalidOperationException("Device is offline.");

        public bool CanPark => throw new InvalidOperationException("Device is offline.");

        public bool CanFindHome => throw new InvalidOperationException("Device is offline.");

        public double Azimuth => throw new InvalidOperationException("Device is offline.");

        public bool AtPark => throw new InvalidOperationException("Device is offline.");

        public bool AtHome => throw new InvalidOperationException("Device is offline.");

        public bool DriverFollowing { get => throw new InvalidOperationException("Device is offline."); set => throw new InvalidOperationException("Device is offline."); }

        public bool Slewing => throw new InvalidOperationException("Device is offline.");

        public IReadOnlyList<int> FocusOffsets => throw new InvalidOperationException("Device is offline.");

        public IReadOnlyList<string> Names => throw new InvalidOperationException("Device is offline.");

        public short Position { get => throw new InvalidOperationException("Device is offline."); set => throw new InvalidOperationException("Device is offline."); }

        public AsyncObservableCollection<FilterInfo> Filters => throw new InvalidOperationException("Device is offline.");

        public CoverState CoverState => throw new InvalidOperationException("Device is offline.");

        public int MaxBrightness => throw new InvalidOperationException("Device is offline.");

        public int MinBrightness => throw new InvalidOperationException("Device is offline.");

        public bool LightOn { get => throw new InvalidOperationException("Device is offline."); set => throw new InvalidOperationException("Device is offline."); }
        public int Brightness { get => throw new InvalidOperationException("Device is offline."); set => throw new InvalidOperationException("Device is offline."); }
        public string PortName { get => throw new InvalidOperationException("Device is offline."); set => throw new InvalidOperationException("Device is offline."); }

        public bool SupportsOpenClose => throw new InvalidOperationException("Device is offline.");

        public bool SupportsOnOff => throw new InvalidOperationException("Device is offline.");

        public bool IsMoving => throw new InvalidOperationException("Device is offline.");

        public int MaxIncrement => throw new InvalidOperationException("Device is offline.");

        public int MaxStep => throw new InvalidOperationException("Device is offline.");

        int IFocuser.Position => throw new InvalidOperationException("Device is offline.");

        public double StepSize => throw new InvalidOperationException("Device is offline.");

        public bool TempCompAvailable => throw new InvalidOperationException("Device is offline.");

        public bool TempComp { get => throw new InvalidOperationException("Device is offline."); set => throw new InvalidOperationException("Device is offline."); }

        public bool CanReverse => throw new InvalidOperationException("Device is offline.");

        public bool Reverse { get => throw new InvalidOperationException("Device is offline."); set => throw new InvalidOperationException("Device is offline."); }

        public bool Synced => throw new InvalidOperationException("Device is offline.");

        float IRotator.Position => throw new InvalidOperationException("Device is offline.");

        public float MechanicalPosition => throw new InvalidOperationException("Device is offline.");

        float IRotator.StepSize => throw new InvalidOperationException("Device is offline.");

        public bool IsSafe => throw new InvalidOperationException("Device is offline.");

        short ISwitch.Id => throw new InvalidOperationException("Device is offline.");

        public double Value => throw new InvalidOperationException("Device is offline.");

        public Coordinates Coordinates => throw new InvalidOperationException("Device is offline.");

        public double RightAscension => throw new InvalidOperationException("Device is offline.");

        public string RightAscensionString => throw new InvalidOperationException("Device is offline.");

        public double Declination => throw new InvalidOperationException("Device is offline.");

        public string DeclinationString => throw new InvalidOperationException("Device is offline.");

        public double SiderealTime => throw new InvalidOperationException("Device is offline.");

        public string SiderealTimeString => throw new InvalidOperationException("Device is offline.");

        public double Altitude => throw new InvalidOperationException("Device is offline.");

        public string AltitudeString => throw new InvalidOperationException("Device is offline.");

        public string AzimuthString => throw new InvalidOperationException("Device is offline.");

        public double HoursToMeridian => throw new InvalidOperationException("Device is offline.");

        public string HoursToMeridianString => throw new InvalidOperationException("Device is offline.");

        public double TimeToMeridianFlip => throw new InvalidOperationException("Device is offline.");

        public string TimeToMeridianFlipString => throw new InvalidOperationException("Device is offline.");

        public double PrimaryMovingRate { get => throw new InvalidOperationException("Device is offline."); set => throw new InvalidOperationException("Device is offline."); }
        public double SecondaryMovingRate { get => throw new InvalidOperationException("Device is offline."); set => throw new InvalidOperationException("Device is offline."); }

        public PierSide SideOfPier => throw new InvalidOperationException("Device is offline.");

        public bool CanSetTrackingEnabled => throw new InvalidOperationException("Device is offline.");

        public bool TrackingEnabled { get => throw new InvalidOperationException("Device is offline."); set => throw new InvalidOperationException("Device is offline."); }

        public IList<TrackingMode> TrackingModes => throw new InvalidOperationException("Device is offline.");

        public TrackingRate TrackingRate => throw new InvalidOperationException("Device is offline.");

        public TrackingMode TrackingMode { get => throw new InvalidOperationException("Device is offline."); set => throw new InvalidOperationException("Device is offline."); }
        public double SiteLatitude { get => throw new InvalidOperationException("Device is offline."); set => throw new InvalidOperationException("Device is offline."); }
        public double SiteLongitude { get => throw new InvalidOperationException("Device is offline."); set => throw new InvalidOperationException("Device is offline."); }
        public double SiteElevation { get => throw new InvalidOperationException("Device is offline."); set => throw new InvalidOperationException("Device is offline."); }

        public bool CanUnpark => throw new InvalidOperationException("Device is offline.");

        public Epoch EquatorialSystem => throw new InvalidOperationException("Device is offline.");

        public bool HasUnknownEpoch => throw new InvalidOperationException("Device is offline.");

        public Coordinates TargetCoordinates => throw new InvalidOperationException("Device is offline.");

        public PierSide? TargetSideOfPier => throw new InvalidOperationException("Device is offline.");

        public double GuideRateRightAscensionArcsecPerSec => throw new InvalidOperationException("Device is offline.");

        public double GuideRateDeclinationArcsecPerSec => throw new InvalidOperationException("Device is offline.");

        public bool CanMovePrimaryAxis => throw new InvalidOperationException("Device is offline.");

        public bool CanMoveSecondaryAxis => throw new InvalidOperationException("Device is offline.");

        public bool CanSetDeclinationRate => throw new InvalidOperationException("Device is offline.");

        public bool CanSetRightAscensionRate => throw new InvalidOperationException("Device is offline.");

        public AlignmentMode AlignmentMode => throw new InvalidOperationException("Device is offline.");

        public bool CanPulseGuide => throw new InvalidOperationException("Device is offline.");

        public bool IsPulseGuiding => throw new InvalidOperationException("Device is offline.");

        public bool CanSetPierSide => throw new InvalidOperationException("Device is offline.");

        public bool CanSlew => throw new InvalidOperationException("Device is offline.");
        public bool CanSlewAltAz => throw new InvalidOperationException("Device is offline.");

        public DateTime UTCDate => throw new InvalidOperationException("Device is offline.");

        public double AveragePeriod => throw new InvalidOperationException("Device is offline.");

        public double CloudCover => throw new InvalidOperationException("Device is offline.");

        public double DewPoint => throw new InvalidOperationException("Device is offline.");

        public double Humidity => throw new InvalidOperationException("Device is offline.");

        public double Pressure => throw new InvalidOperationException("Device is offline.");

        public double RainRate => throw new InvalidOperationException("Device is offline.");

        public double SkyBrightness => throw new InvalidOperationException("Device is offline.");

        public double SkyQuality => throw new InvalidOperationException("Device is offline.");

        public double SkyTemperature => throw new InvalidOperationException("Device is offline.");

        public double StarFWHM => throw new InvalidOperationException("Device is offline.");

        public double WindDirection => throw new InvalidOperationException("Device is offline.");

        public double WindGust => throw new InvalidOperationException("Device is offline.");

        public double WindSpeed => throw new InvalidOperationException("Device is offline.");

        public string Action(string actionName, string actionParameters) {
            throw new InvalidOperationException("Device is offline.");
        }

        public string SendCommandString(string command, bool raw) {
            throw new InvalidOperationException("Device is offline.");
        }

        public bool SendCommandBool(string command, bool raw) {
            throw new InvalidOperationException("Device is offline.");
        }

        public void SendCommandBlind(string command, bool raw) {
            throw new InvalidOperationException("Device is offline.");
        }

        public void SetBinning(short x, short y) {
            throw new InvalidOperationException("Device is offline.");
        }

        public void StartExposure(CaptureSequence sequence) {
            throw new InvalidOperationException("Device is offline.");
        }

        public Task WaitUntilExposureIsReady(CancellationToken token) {
            throw new InvalidOperationException("Device is offline.");
        }

        public void StopExposure() {
            throw new InvalidOperationException("Device is offline.");
        }

        public void AbortExposure() {
            throw new InvalidOperationException("Device is offline.");
        }

        public Task<IExposureData?> DownloadExposure(CancellationToken token) {
            throw new InvalidOperationException("Device is offline.");
        }

        public void StartLiveView(CaptureSequence sequence) {
            throw new InvalidOperationException("Device is offline.");
        }

        public Task<IExposureData?> DownloadLiveView(CancellationToken token) {
            throw new InvalidOperationException("Device is offline.");
        }

        public void StopLiveView() {
            throw new InvalidOperationException("Device is offline.");
        }

        public Task SlewToAzimuth(double azimuth, CancellationToken ct) {
            throw new InvalidOperationException("Device is offline.");
        }

        public Task StopSlewing() {
            throw new InvalidOperationException("Device is offline.");
        }

        public Task StopShutter() {
            throw new InvalidOperationException("Device is offline.");
        }

        public Task StopAll() {
            throw new InvalidOperationException("Device is offline.");
        }

        public Task OpenShutter(CancellationToken ct) {
            throw new InvalidOperationException("Device is offline.");
        }

        public Task CloseShutter(CancellationToken ct) {
            throw new InvalidOperationException("Device is offline.");
        }

        public Task FindHome(CancellationToken ct) {
            throw new InvalidOperationException("Device is offline.");
        }

        public Task Park(CancellationToken ct) {
            throw new InvalidOperationException("Device is offline.");
        }

        public void SetPark() {
            throw new InvalidOperationException("Device is offline.");
        }

        public void SyncToAzimuth(double azimuth) {
            throw new InvalidOperationException("Device is offline.");
        }

        public Task<bool> Open(CancellationToken ct, int delay = 300) {
            throw new InvalidOperationException("Device is offline.");
        }

        public Task<bool> Close(CancellationToken ct, int delay = 300) {
            throw new InvalidOperationException("Device is offline.");
        }

        public Task Move(int position, CancellationToken ct, int waitInMs = 1000) {
            throw new InvalidOperationException("Device is offline.");
        }

        public void Halt() {
            throw new InvalidOperationException("Device is offline.");
        }

        public void Sync(float skyAngle) {
            throw new InvalidOperationException("Device is offline.");
        }

        public Task<bool> Move(float position, CancellationToken ct) {
            throw new InvalidOperationException("Device is offline.");
        }

        public Task<bool> MoveAbsolute(float position, CancellationToken ct) {
            throw new InvalidOperationException("Device is offline.");
        }

        public Task<bool> MoveAbsoluteMechanical(float position, CancellationToken ct) {
            throw new InvalidOperationException("Device is offline.");
        }

        public bool Poll() {
            throw new InvalidOperationException("Device is offline.");
        }

        public IList<(double, double)> GetAxisRates(TelescopeAxes axis) {
            throw new InvalidOperationException("Device is offline.");
        }

        public Task<bool> MeridianFlip(Coordinates targetCoordinates, CancellationToken token) {
            throw new InvalidOperationException("Device is offline.");
        }

        public void MoveAxis(TelescopeAxes axis, double rate) {
            throw new InvalidOperationException("Device is offline.");
        }

        public void PulseGuide(GuideDirections direction, int duration) {
            throw new InvalidOperationException("Device is offline.");
        }

        public Task<bool> SlewToCoordinates(Coordinates coordinates, CancellationToken token) {
            throw new InvalidOperationException("Device is offline.");
        }

        public void StopSlew() {
            throw new InvalidOperationException("Device is offline.");
        }

        public bool Sync(Coordinates coordinates) {
            throw new InvalidOperationException("Device is offline.");
        }

        public Task Unpark(CancellationToken token) {
            throw new InvalidOperationException("Device is offline.");
        }

        public void SetCustomTrackingRate(double rightAscensionRate, double declinationRate) {
            throw new InvalidOperationException("Device is offline.");
        }

        public PierSide DestinationSideOfPier(Coordinates coordinates) {
            throw new InvalidOperationException("Device is offline.");
        }

        public Task<bool> SlewToAltAz(TopocentricCoordinates coordinates, CancellationToken token) {
            throw new InvalidOperationException("Device is offline.");
        }

        public void UpdateSubSampleArea() {
            throw new InvalidOperationException("Device is offline.");
        }
    }
}