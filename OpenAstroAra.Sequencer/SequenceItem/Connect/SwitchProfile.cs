using Newtonsoft.Json;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Equipment.Equipment;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Sequencer.Validations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.SequenceItem.Connect {
    [JsonObject(MemberSerialization.OptIn)]
    public class SwitchProfile : SequenceItem, IValidatable {
        private IProfileService profileService;
        private ICameraMediator cameraMediator;
        private IFilterWheelMediator fwMediator;
        private IFocuserMediator focuserMediator;
        private IRotatorMediator rotatorMediator;
        private ITelescopeMediator telescopeMediator;
        private IGuiderMediator guiderMediator;
        private ISwitchMediator switchMediator;
        private IFlatDeviceMediator flatDeviceMediator;
        private IWeatherDataMediator weatherDataMediator;
        private IDomeMediator domeMediator;
        private ISafetyMonitorMediator safetyMonitorMediator;

        public SwitchProfile(IProfileService profileService,
                                ICameraMediator cameraMediator,
                                IFilterWheelMediator fwMediator,
                                IFocuserMediator focuserMediator,
                                IRotatorMediator rotatorMediator,
                                ITelescopeMediator telescopeMediator,
                                IGuiderMediator guiderMediator,
                                ISwitchMediator switchMediator,
                                IFlatDeviceMediator flatDeviceMediator,
                                IWeatherDataMediator weatherDataMediator,
                                IDomeMediator domeMediator,
                                ISafetyMonitorMediator safetyMonitorMediator) {
            this.profileService = profileService;
            this.cameraMediator = cameraMediator;
            this.fwMediator = fwMediator;
            this.focuserMediator = focuserMediator;
            this.rotatorMediator = rotatorMediator;
            this.telescopeMediator = telescopeMediator;
            this.guiderMediator = guiderMediator;
            this.switchMediator = switchMediator;
            this.flatDeviceMediator = flatDeviceMediator;
            this.weatherDataMediator = weatherDataMediator;
            this.domeMediator = domeMediator;
            this.safetyMonitorMediator = safetyMonitorMediator;
            Devices = new List<string>() {
                "Camera",
                "Filter Wheel",
                "Focuser",
                "Rotator",
                "Telescope",
                "Guider",
                "Switch",
                "Flat Panel",
                "Weather",
                "Dome",
                "Safety Monitor"
            };
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

        public IReadOnlyList<string> Devices { get; }


        private object? GetMediator(string device) {
            switch (device) {
                case "Camera": return cameraMediator;
                case "Filter Wheel": return fwMediator;
                case "Focuser": return focuserMediator;
                case "Rotator": return rotatorMediator;
                case "Telescope": return telescopeMediator;
                case "Guider": return guiderMediator;
                case "Switch": return switchMediator;
                case "Flat Panel": return flatDeviceMediator;
                case "Weather": return weatherDataMediator;
                case "Dome": return domeMediator;
                case "Safety Monitor": return safetyMonitorMediator;
                default: return null;
            }
        }

        private SwitchProfile(SwitchProfile copyMe) : this(copyMe.profileService,
                                                                 copyMe.cameraMediator,
                                                                 copyMe.fwMediator,
                                                                 copyMe.focuserMediator,
                                                                 copyMe.rotatorMediator,
                                                                 copyMe.telescopeMediator,
                                                                 copyMe.guiderMediator,
                                                                 copyMe.switchMediator,
                                                                 copyMe.flatDeviceMediator,
                                                                 copyMe.weatherDataMediator,
                                                                 copyMe.domeMediator,
                                                                 copyMe.safetyMonitorMediator) {
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

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            if (SelectedProfileId == Guid.Empty) {
                throw new SequenceItemSkippedException($"No profile was selected");
            }
            if (profileService.ActiveProfile.Id == SelectedProfileId) {
                throw new SequenceItemSkippedException($"Selected profile is already active ({SelectedProfileId})");
            }

            var errors = new List<Exception>();

            foreach (var device in Devices) {
                token.ThrowIfCancellationRequested();
                var mediator = GetMediator(device);
                if (mediator == null) { continue; }

                var type = mediator.GetType();
                var GetInfo = type.GetMethod("GetInfo");
                DeviceInfo info = (DeviceInfo)GetInfo!.Invoke(mediator, null)!;


                if (info.Connected) {
                    var Disconnect = type.GetMethod("Disconnect");
                    await (Task)Disconnect!.Invoke(mediator, null)!;

                    DeviceInfo infoAfterConnect = (DeviceInfo)GetInfo!.Invoke(mediator, null)!;
                    var success = !infoAfterConnect.Connected;
                    if (!success) {
                        errors.Add(new SequenceEntityFailedException($"Failed to disconnect to {device}"));
                    }
                } else {
                    Logger.Info($"{device} is already disconnected");
                }
            }

            if (errors.Count > 0) {
                throw new AggregateException(errors);
            }
            var p = profileService.Profiles.FirstOrDefault(x => x.Id == SelectedProfileId);
            if (p != null) {
                profileService.SelectProfile(p);
            } else {
                throw new SequenceEntityFailedException("Unknown profile selected");
            }


            if (Reconnect) {
                foreach (var device in Devices) {
                    token.ThrowIfCancellationRequested();
                    if (!IsConnected(device)) {
                        var profileId = GetProfileId(device);
                        if (!(profileId == "No_Device" || profileId == "No_Guider")) {
                            var mediator = GetMediator(device);
                            if (mediator == null) { continue; }

                            var type = mediator.GetType();
                            var GetInfo = type.GetMethod("GetInfo");
                            var Rescan = type.GetMethod("Rescan");
                            var devices = await (Task<IList<string>>)Rescan!.Invoke(mediator, null)!;

                            if (profileId != null && devices.Contains(profileId)) {
                                var Connect = type.GetMethod("Connect");
                                var success = await (Task<bool>)Connect!.Invoke(mediator, null)!;

                                DeviceInfo infoAfterConnect = (DeviceInfo)GetInfo!.Invoke(mediator, null)!;
                                success = success && infoAfterConnect.Connected;
                                if (!success) {
                                    errors.Add(new SequenceEntityFailedException($"Failed to connect to {device}"));
                                }
                            } else {
                                errors.Add(new SequenceEntityFailedException($"Failed to connect to {device} as it was not found"));
                            }

                        }
                    } else {
                        Logger.Info($"{device} is already connected");
                    }
                }
            }
        }

        public bool IsConnected(string device) {
            var mediator = GetMediator(device);
            if (mediator == null) { return false; }

            var type = mediator.GetType();
            var GetInfo = type.GetMethod("GetInfo");
            DeviceInfo info = (DeviceInfo)GetInfo!.Invoke(mediator, null)!;
            return info.Connected;
        }

        public string? GetProfileId(string device) {
            switch (device) {
                case "Camera": return profileService.ActiveProfile.CameraSettings.Id;
                case "Filter Wheel": return profileService.ActiveProfile.FilterWheelSettings.Id;
                case "Focuser": return profileService.ActiveProfile.FocuserSettings.Id;
                case "Rotator": return profileService.ActiveProfile.RotatorSettings.Id;
                case "Telescope": return profileService.ActiveProfile.TelescopeSettings.Id;
                case "Guider": return profileService.ActiveProfile.GuiderSettings.GuiderName;
                case "Switch": return profileService.ActiveProfile.SwitchSettings.Id;
                case "Flat Panel": return profileService.ActiveProfile.FlatDeviceSettings.Id;
                case "Weather": return profileService.ActiveProfile.WeatherDataSettings.Id;
                case "Dome": return profileService.ActiveProfile.DomeSettings.Id;
                case "Safety Monitor": return profileService.ActiveProfile.SafetyMonitorSettings.Id;
                default: return null;
            }
        }

        public bool Validate() {
            return true;
        }

        public override string ToString() {
            return $"Category: {Category}, Item: {nameof(SwitchProfile)}, ProfileId: {SelectedProfileId}, Reconnect: {Reconnect}";
        }
    }
}