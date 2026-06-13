#region "copyright"

/*
    Copyright (c) 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Core.Utility.Notification;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenAstroAra.Core.Interfaces;
using OpenAstroAra.Core.Locale;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2.PhdEvents;
using OpenAstroAra.Astrometry;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Net;
using ASCOM.Common.Helpers;

// Recovered inherited PHD2 client (deleted in #242, restored from 840893eb8^). Nullable-reference
// WARNINGS are disabled file-locally to preserve NINA's original, battle-tested null-handling on the
// JSON-RPC/connection path rather than introduce ~37 speculative annotations that risk a runtime NRE.
// The annotation context stays on (the connect-time field/property `?` annotations above are real).
// TODO(§63): replace with proper per-site annotations once the live PHD2 path is exercised.
#nullable disable warnings

// CA1031: the JSON-RPC listener + connection/command methods catch-and-log broadly on purpose — a
// socket/parse/PHD2-protocol fault must never crash the background listener, and narrowing risks
// missing an exception type and tearing down guiding. This mirrors NINA's original handling; the
// top-level boundaries log and recover. TODO(§63): revisit per-method narrowing if the live path
// surfaces specific recoverable types.
#pragma warning disable CA1031 // Do not catch general exception types

namespace OpenAstroAra.Equipment.Equipment.MyGuider.PHD2 {

    public sealed partial class PHD2Guider : BaseINPC, IGuider, IDisposable {

        public PHD2Guider(IProfileService profileService) {
            this.profileService = profileService;
        }

        public void Dispose() {
            _clientCTS?.Dispose();
            GC.SuppressFinalize(this);
        }

        private readonly IProfileService profileService;

        private PhdEventVersion? _version;

        public string Name => "PHD2";
        public string DisplayName => Name;

        public string Id => "PHD2_Single";

        public PhdEventVersion? Version {
            get => _version;
            set {
                _version = value;
                RaisePropertyChanged();
            }
        }

        private PhdEventAppState? _appState;

        public PhdEventAppState? AppState {
            get => _appState;
            set {
                _appState = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(State));
            }
        }

        private bool settling;

        public bool Settling {
            get {
                lock (lockobj) {
                    return settling;
                }
            }
            private set {
                lock (lockobj) {
                    settling = value;
                }
            }
        }

        private PhdEventGuidingDithered? _guidingDithered;

        public PhdEventGuidingDithered GuidingDithered {
            get => _guidingDithered;
            set {
                _guidingDithered = value;
                RaisePropertyChanged();
            }
        }

        private CancellationTokenSource? _clientCTS;

        private static object lockobj = new object();

        private bool _connected;

        public bool Connected {
            get => _connected;
            private set {
                lock (lockobj) {
                    _connected = value;
                    RaisePropertyChanged();
                }
            }
        }

        private double _pixelScale;

        public double PixelScale {
            get => _pixelScale;
            set {
                _pixelScale = value;
                RaisePropertyChanged();
            }
        }

        public string State => AppState?.State ?? string.Empty;

        public bool HasSetupDialog => !Connected;

        public string Category => "Guiders";

        public string Description => "PHD2 Guider";

        public string DriverInfo => "PHD2 Guider";

        public string DriverVersion => "1.0";

        // _activeProfile represents whatever GetProfile last returned
        private Phd2ProfileResponse? _activeProfile;

        private Phd2Profile? _selectedProfile;

        public Phd2Profile? SelectedProfile {
            get => _selectedProfile;
            set {
                if (value != _selectedProfile) {
                    _selectedProfile = value;
                    RaisePropertyChanged();
                }
            }
        }

        public AsyncObservableCollection<Phd2Profile> AvailableProfiles { get; private set; } = new AsyncObservableCollection<Phd2Profile>();

        private TaskCompletionSource<bool>? _tcs;

        private bool initialized;

        public async Task<bool> Connect(CancellationToken token) {
            bool connected = false;
            IPHostEntry hostEntry;
            _tcs = new TaskCompletionSource<bool>();

            var serverHost = profileService.ActiveProfile.GuiderSettings.PHD2ServerHost;
            var serverPort = profileService.ActiveProfile.GuiderSettings.PHD2ServerPort;

            if (string.IsNullOrEmpty(serverHost)) {
                Notifier.ShowError(Loc.Instance["LblPhd2ServerHostNotSet"]);
                return connected;
            }

            try {
                hostEntry = DnsHelper.GetIPHostEntryByName(serverHost);
                phd2Ip = hostEntry.AddressList.First();
            } catch (Exception ex) {
                if (ex is SocketException se) {
                    // Error Code 11001 WSAHOST_NOT_FOUND - https://learn.microsoft.com/en-us/windows/win32/winsock/windows-sockets-error-codes-2
                    if (se.ErrorCode == 11001 && IPAddress.TryParse(profileService.ActiveProfile.GuiderSettings.PHD2ServerHost, out var address)) {
                        phd2Ip = address;
                    }
                }
                Logger.Error($"Failed to resolve PHD2 server {serverHost}: {ex.Message}");
                Notifier.ShowError(string.Format(CultureInfo.InvariantCulture, Loc.Instance["LblPhd2ServerHostNotResolved"], serverHost));
                return connected;
            }

            Logger.Info($"Connecting to PHD2 server at {phd2Ip}:{serverPort}");

            // §63: the guider (openastro-guider / PHD2) runs as its own systemd/docker
            // service and is already listening before ARA connects (the Pi boots PHD2,
            // ARA core, ASTAP, and the Alpaca bridge in parallel at power-on). ARA is a
            // pure client — it never spawns the guider. Ensuring the service is up if it
            // isn't is the daemon's job (GuiderService asks the systemd supervisor to
            // start it before connecting); here we just open the event-stream connection.
            // (The legacy NINA-desktop StartPHD2Process — which called WaitForInputIdle,
            // a GUI-only Win32 call — has been retired for the headless port.)
            _ = Task.Run(RunListener, token);

            connected = await _tcs.Task;

            try {
                if (connected) {
                    // Phase 3 (PORT_PLAYBOOK.md §7): log PHD2 version + identify openastro-phd2.
                    // RunListener resolves _tcs as soon as the TCP socket is up; the "Version"
                    // event from PHD2 may not have arrived yet, so we briefly wait for it
                    // (bounded by token + a 2-second cap) before logging identification.
                    await WaitForVersionAsync(maxWaitMs: 2000, token).ConfigureAwait(false);

                    if (Version != null) {
                        var phdVersionString = Version.PHDVersion ?? "(unknown)";
                        var phdSubver = Version.PHDSubver ?? string.Empty;
                        // openastro-phd2 advertises itself via PHDSubver ("openastroara" prefix)
                        // or via dev-version semver suffix; both forms are treated as openastro-phd2.
                        bool isOpenAstroPhd2 =
                            phdSubver.Contains("openastroara", StringComparison.OrdinalIgnoreCase) ||
                            phdSubver.Contains("openastro-phd2", StringComparison.OrdinalIgnoreCase) ||
                            phdVersionString.Contains("openastro-phd2", StringComparison.OrdinalIgnoreCase);
                        if (isOpenAstroPhd2) {
                            Logger.Info($"Connected to openastro-phd2 v{phdVersionString} (subver {phdSubver}).");
                        } else {
                            Logger.Warning($"Connected to upstream PHD2 v{phdVersionString} (subver {phdSubver}). " +
                                $"OpenAstro Ara is designed against openastro-phd2; some §63 / §63.2 features may not be available.");
                        }
                    } else {
                        Logger.Warning("PHD2 version event did not arrive within 2s after connect; cannot identify openastro-phd2 vs upstream. Some §63 features may behave unexpectedly.");
                    }

                    await GetProfiles();
                    await EnsureAraGuiderProfileAsync(token);
                    await PushGuiderEngineConfigAsync(token);
                    await EnsurePHD2EquipmentConnected();
                    await TryRefreshShiftLockParams();
                    await SetPixelScale();
                    initialized = true;
                }

            } catch (OperationCanceledException) {
            } catch (Exception ex) {
                Logger.Error(ex);
                Notifier.ShowError(ex.Message);
            }

            return connected;
        }

        public Task SetPixelScale() {
            return Task.Run(async () => {
                try {
                    var msg = new Phd2GetPixelScale();
                    var resp = await SendMessage(msg);
                    if (resp.result != null) {
                        PixelScale = double.Parse(resp.result.ToString().Replace(",", ".", StringComparison.Ordinal), CultureInfo.InvariantCulture);
                    }
                } catch (Exception ex) {
                    Logger.Error(ex);
                }
            });
        }

        private async Task<bool> ProfileSelectionChanged() {
            if (SelectedProfile == null) {
                Logger.Error("No profile selected");
                return false;
            }

            if (SelectedProfile.Id == _activeProfile?.id) {
                return true;
            }

            return await ChangeProfile(SelectedProfile.Id);
        }

        private async Task<bool> ChangeProfile(int id) {
            // Trigger a GetProfiles operation in the background after either a success or failure, which will refresh the profile list and
            // set both SelectedProfile and _activeProfile to their latest values
            var targetProfile = AvailableProfiles.FirstOrDefault(x => x.Id == id);
            if (targetProfile == null) {
                Logger.Error($"PHD2 profile {id} could not be found");
                await GetProfiles();
                Notifier.ShowWarning(String.Format(CultureInfo.InvariantCulture, Loc.Instance["LblPhd2ProfileNotFound"], id, _activeProfile?.name));
                // Clear the saved id so we don't try and restore the missing profile next time
                profileService.ActiveProfile.GuiderSettings.PHD2ProfileId = null;
                return false;
            }

            await DisconnectPHD2Equipment();
            var setProfile = new Phd2SetProfile() { Parameters = new int[] { id } };
            var setProfileResponse = await SendMessage(setProfile);
            if (setProfileResponse.error != null) {
                Logger.Error($"Failed SetProfile({id}): {setProfileResponse.error}");
                Notifier.ShowWarning(Loc.Instance["LblPhd2ProfileChangeFailed"]);
                await GetProfiles();
                return false;
            }

            profileService.ActiveProfile.GuiderSettings.PHD2ProfileId = id;
            await EnsurePHD2EquipmentConnected();
            await GetProfiles();
            return true;
        }

        public async Task<bool> Dither(IProgress<ApplicationStatus> progress, CancellationToken ct) {
            if (Connected) {
                var state = await GetAppState();
                if (state != PhdAppState.GUIDING) {
                    if (state == PhdAppState.LOSTLOCK) {
                        Notifier.ShowWarning(Loc.Instance["LblDitherSkippedBecauseNotLostLock"]);
                    } else {
                        Notifier.ShowWarning(Loc.Instance["LblDitherSkippedBecauseNotGuiding"]);
                    }

                    return false;
                }

                await WaitForSettling(progress, ct);

                var ditherMsg = new Phd2Dither() {
                    Parameters = new Phd2DitherParameter() {
                        Amount = profileService.ActiveProfile.GuiderSettings.DitherPixels,
                        RaOnly = profileService.ActiveProfile.GuiderSettings.DitherRAOnly,
                        Settle = new Phd2Settle() {
                            Pixels = profileService.ActiveProfile.GuiderSettings.SettlePixels,
                            Time = profileService.ActiveProfile.GuiderSettings.SettleTime,
                            Timeout = profileService.ActiveProfile.GuiderSettings.SettleTimeout
                        }
                    }
                };

                var ditherMsgResponse = await SendMessage(ditherMsg);
                if (ditherMsgResponse.error != null) {
                    /* Dither failed */
                    return false;
                }
                Settling = true;
                await WaitForSettling(progress, ct);
            }
            return true;
        }

        private async Task WaitForSettling(IProgress<ApplicationStatus> progress, CancellationToken ct) {
            try {
                await Task.Run<bool>(async () => {
                    var elapsed = new TimeSpan();
                    while (Settling == true) {
                        progress?.Report(new ApplicationStatus { Status = Loc.Instance["LblPHD2Settling"] });
                        elapsed += await CoreUtil.Delay(500, ct);

                        var timeout = profileService.ActiveProfile.GuiderSettings.SettleTimeout;
                        if (elapsed.TotalSeconds > (timeout + 10)) {
                            //Failsafe when phd is not sending settlingdone message
                            Notifier.ShowWarning(string.Format(CultureInfo.InvariantCulture, Loc.Instance["LblGuiderNoSettleDone"], timeout));
                            Logger.Warning($"Phd2 - Guider did not send SettleDone message in expected time  ({timeout}s + 10s). Skipping.");
                            Settling = false;
                        }
                    }
                    return true;
                });
            } catch (OperationCanceledException) {
                Settling = false;
            } finally {
                progress?.Report(new ApplicationStatus { Status = string.Empty });
            }
        }

        public async Task<bool> Pause(bool pause, CancellationToken ct) {
            if (Connected) {
                var msg = new Phd2Pause() { Parameters = new bool[] { true } };
                await SendMessage(msg);

                if (pause) {
                    var elapsed = new TimeSpan();
                    while (!(AppState.State == PhdAppState.PAUSED)) {
                        elapsed += await CoreUtil.Delay(500, ct);
                    }
                } else {
                    var elapsed = new TimeSpan();
                    while ((AppState.State == PhdAppState.PAUSED)) {
                        elapsed += await CoreUtil.Delay(500, ct);
                        if (elapsed.TotalSeconds > 60) {
                            //Failsafe when phd is not sending resume message
                            Notifier.ShowWarning(Loc.Instance["LblGuiderNoResume"]/*, ToastNotifications.NotificationsSource.NeverEndingNotification*/);
                            break;
                        }
                    }
                }
            }
            return true;
        }

        private static void CheckPhdError(PhdMethodResponse m) {
            if (m.error != null) {
                Notifier.ShowError(String.Format(CultureInfo.InvariantCulture, Loc.Instance["LblPHDError"], m.error.message, m.error.code));
                Logger.Warning("PHDError: " + m.error.message + " CODE: " + m.error.code);
            }
        }

        public async Task<bool> AutoSelectGuideStar() {
            if (Connected) {
                var state = await GetAppState();
                if (state != PhdAppState.LOOPING) {
                    var loopMsg = new Phd2Loop();
                    await SendMessage(loopMsg);
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }

                // Wait for at least one exposure to finish
                var exposureDurationResponse = await SendMessage<GetExposureResponse>(new Phd2GetExposure());
                var durationMs = exposureDurationResponse.result;
                await Task.Delay(TimeSpan.FromMilliseconds((durationMs ?? 0) + 1000));

                var findStarMsg = new Phd2FindStar() {
                    Parameters = new Phd2FindStarParameter() {
                        Roi = await GetROI()
                    }
                };

                await SendMessage(findStarMsg);

                return true;
            }
            return false;
        }

        private async Task<int[]> GetROI() {
            if (profileService.ActiveProfile.GuiderSettings.PHD2ROIPct < 100) {
                var cameraSize = new Phd2GetCameraFrameSize();
                var size = await SendMessage<GetCameraFrameSizeResponse>(cameraSize);
                if (size.result.Count == 2) {
                    int width = size.result[0];
                    int height = size.result[1];
                    double pct = profileService.ActiveProfile.GuiderSettings.PHD2ROIPct / 100d;

                    int halfWidth = width / 2;
                    int halfHeight = height / 2;

                    int roiX = (int)(halfWidth - halfWidth * pct);
                    int roiY = (int)(halfHeight - halfHeight * pct);
                    int roiWidth = (int)(width * pct);
                    int roiHeight = (int)(height * pct);

                    return [roiX, roiY, roiWidth, roiHeight];
                }
            }
            return null;
        }

        public async Task<LockPosition> GetLockPosition() {
            return await GetLockPositionInternal(5000);
        }

        private async Task<LockPosition> GetLockPositionInternal(
            int receiveTimeout = 0) {
            var msg = new Phd2GetLockPosition();
            var lockPositionResponse = await SendMessage<GetLockPositionResponse>(
                msg,
                receiveTimeout);
            if (lockPositionResponse?.result != null && lockPositionResponse.result.Count == 2) {
                return new LockPosition(lockPositionResponse.result[0], lockPositionResponse.result[1]);
            }
            return null;
        }

        /// <summary>
        /// Phase 3 (§7) helper: PHD2's listener emits the "Version" event right after the
        /// TCP socket opens, but it's not necessarily available the instant <c>_tcs.Task</c>
        /// completes. This polls <see cref="Version"/> with a short cap so the connect path
        /// has a useful value to log without blocking the user indefinitely if the event
        /// never arrives (network issues, broken upstream impl, etc.).
        /// </summary>
        private async Task WaitForVersionAsync(int maxWaitMs, CancellationToken token) {
            const int pollIntervalMs = 50;
            var elapsed = 0;
            while (Version == null && elapsed < maxWaitMs && !token.IsCancellationRequested) {
                try {
                    await Task.Delay(pollIntervalMs, token).ConfigureAwait(false);
                } catch (TaskCanceledException) {
                    return;
                }
                elapsed += pollIntervalMs;
            }
        }

        private async Task<string> GetAppState(
            int receiveTimeout = 0) {
            var msg = new Phd2GetAppState();
            var appStateResponse = await SendMessage(
                msg,
                receiveTimeout);
            return appStateResponse?.result?.ToString();
        }

        private async Task<bool> IsCalibrated() {
            var msg = new Phd2GetCalibrated();
            var response = await SendMessage<BooleanPhdMethodResponse>(msg, 5000);

            return response?.result ?? false;
        }

        private Task<bool> WaitForAppState(
            string targetState,
            CancellationToken ct,
            int receiveTimeout = 0) {
            return Task.Run(async () => {
                try {
                    var state = await GetAppState();
                    while (state != targetState) {
                        await Task.Delay(1000, ct);
                        state = await GetAppState();
                    }
                    return true;
                } catch (OperationCanceledException) {
                    return false;
                }
            });
        }

        public Task<bool> StartGuiding(bool forceCalibration, IProgress<ApplicationStatus> progress, CancellationToken ct) {
            return StartGuidingPrivate(forceCalibration, true, progress, ct);
        }

        private async Task<bool> StartGuidingPrivate(bool forceCalibration, bool waitForSettle, IProgress<ApplicationStatus> progress, CancellationToken ct) {
            if (!Connected)
                return false;

            string state = await GetAppState();
            if (state == PhdAppState.GUIDING) {
                Logger.Info("Phd2 - App is already guiding. Skipping start guiding");
                return true;
            }

            if (state == PhdAppState.LOSTLOCK) {
                Logger.Info("Phd2 - App has lost guide star and needs to stop before starting guiding again");
                await StopGuiding(ct);
            }

            if (state == PhdAppState.CALIBRATING) {
                Logger.Info("Phd2 - App is already calibrating. Waiting for calibration to finish");
                await WaitForCalibrationFinished(progress, ct);
            }

            var isCalibrated = forceCalibration ? false : await IsCalibrated();

            int retries = 1;
            int maxRetries = profileService.ActiveProfile.GuiderSettings.AutoRetryStartGuiding ? 3 : 1;
            var retryAfterSeconds = TimeSpan.FromSeconds(profileService.ActiveProfile.GuiderSettings.AutoRetryStartGuidingTimeoutSeconds);
            while (!ct.IsCancellationRequested) {
                if (!await TryStartGuideCommand(forceCalibration, progress, ct)) {
                    return false;
                }

                var starSelected = await WaitForStarSelected(progress, ct);
                if (starSelected) {
                    if (!isCalibrated) {
                        await Task.Delay(5000, ct);
                        await WaitForCalibrationFinished(progress, ct);
                    }

                    using var cancelOnTimeoutOrParent = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var timeout = Task.Delay(
                        retryAfterSeconds,
                        cancelOnTimeoutOrParent.Token);
                    var guidingHasBegun = WaitForGuidingStarted(progress, cancelOnTimeoutOrParent.Token);

                    if ((await Task.WhenAny(timeout, guidingHasBegun)) == guidingHasBegun) {
                        // Guiding has been started successfully in time
                        // Wait for phd2 to settle and exit
                        if (waitForSettle) {
                            await WaitForSettling(progress, ct);
                        }
                        return true;
                    }
                    try { await cancelOnTimeoutOrParent.CancelAsync(); } catch { }
                }
                retries += 1;

                if (retries > maxRetries) {
                    // Max number of unsuccessful retries exceeded. Exit.
                    Logger.Warning($"Phd2 - Start guiding has failed after {maxRetries} retries");
                    return false;
                }

                Logger.Warning($"Phd2 - Start guiding has timed out after {retryAfterSeconds.TotalSeconds}s. Retrying to start guiding. Attempt {retries} / {maxRetries}");
                progress?.Report(new ApplicationStatus { Status = Loc.Instance["LblStartGuiding"], Status2 = Loc.Instance["LblPHD2StartGuidingTimeoutRetry"], Progress2 = retries, MaxProgress2 = maxRetries, ProgressType2 = ApplicationStatus.StatusProgressType.ValueOfMaxValue });

                await Task.Delay(1000, ct); // 1000ms sleep between retries

                await StopGuiding(ct); // used to visual inspect that the guider is in the stopped state before retrying.

                await Task.Delay(5000, ct); // 5000ms sleep between retries
            }
            return false;
        }

        private Task RestartForLostShiftLock() {
            return Task.Run(async () => {
                await this.StopGuiding(CancellationToken.None);

                // Don't wait for settling when restarting due to lost lock shift, which should minimize downtime
                if (!await StartGuidingPrivate(false, false, null, CancellationToken.None)) {
                    Notifier.ShowError(Loc.Instance["LblRestartGuidingAfterLostShiftLockFailed"]);
                    Logger.Error("Failed to restart guiding after lost shift lock");
                    return;
                }

                if (!await SetShiftRate(ShiftRate, CancellationToken.None)) {
                    Notifier.ShowError(Loc.Instance["LblPhd2GuiderRestartShiftLockFailed"]);
                    Logger.Error("Failed to set shift rate after lost shift lock");
                } else {
                    Notifier.ShowInformation(Loc.Instance["LblPhd2GuiderRestartShiftLockSuccess"]);
                    Logger.Info("Successfully restarted shift lock after losing it");
                }
            });
        }

        private async Task<bool> WaitForStarSelected(IProgress<ApplicationStatus> progress, CancellationToken ct) {
            var lockPos = await GetLockPositionInternal(5000);
            if (lockPos == null) {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var timeoutTime = TimeSpan.FromSeconds(30);
                timeoutCts.CancelAfter(timeoutTime);
                try {
                    while (lockPos == null) {
                        await Task.Delay(1000, timeoutCts.Token);
                        lockPos = await GetLockPositionInternal(5000);
                    }
                    return true;
                } catch (OperationCanceledException) {
                    if (ct.IsCancellationRequested) {
                        throw;
                    } else {
                        //After {timeoutTime.TotalSeconds} the state is still in looping or stopped state, so selecting a guide star has failed
                        Logger.Error($"Failed to select guide star after {timeoutTime.TotalSeconds} seconds");
                        return false;
                    }
                }
            }
            return true;
        }

        private async Task WaitForCalibrationFinished(IProgress<ApplicationStatus> progress, CancellationToken ct) {
            string state = await GetAppState(); ;
            while (state == PhdAppState.CALIBRATING) {
                progress?.Report(new ApplicationStatus { Status = Loc.Instance["LblStartGuiding"], Status2 = Loc.Instance["LblPHD2Calibrating"] });
                state = await GetAppState();
                await Task.Delay(1000, ct);
            }
        }

        private async Task<bool> TryStartGuideCommand(bool forceCalibration, IProgress<ApplicationStatus> progress, CancellationToken ct) {
            await WaitForSettling(progress, ct);

            var guideMsg = new Phd2Guide() {
                Parameters = new Phd2GuideParameter() {
                    Settle = new Phd2Settle() {
                        Pixels = profileService.ActiveProfile.GuiderSettings.SettlePixels,
                        Time = profileService.ActiveProfile.GuiderSettings.SettleTime,
                        Timeout = profileService.ActiveProfile.GuiderSettings.SettleTimeout
                    },
                    Recalibrate = forceCalibration,
                    Roi = await GetROI()
                }
            };

            Logger.Info($"Phd2 - Requesting to start guiding. Recalibrate: {forceCalibration}");

            var guideMsgResponse = await SendMessage(guideMsg);
            if (guideMsgResponse.error == null) {
                await TryRefreshShiftLockParams();
                return true;
            }
            return false;
        }

        private async Task<bool> TryRefreshShiftLockParams() {
            var getShiftLockParamsMsg = new Phd2GetLockShiftParams();
            Logger.Trace($"Phd2 - Requesting shift lock params");

            try {
                var getShiftLockParamsResponse = await SendMessage<GetLockShiftParamsResponse>(getShiftLockParamsMsg);
                if (getShiftLockParamsResponse.error != null) {
                    Logger.Error($"Failed to get shift lock params. Code={getShiftLockParamsResponse.error.code}, Message={getShiftLockParamsResponse.error.message}");
                    return false;
                }

                var result = getShiftLockParamsResponse.result;
                if (result.Enabled) {
                    if (result.Units == "pixels/hr") {
                        var raShiftRate = result.Rate[0] * PixelScale / 3600.0d;
                        var decShiftRate = result.Rate[1] * PixelScale / 3600.0d;
                        ShiftRate = SiderealShiftTrackingRate.Create(raShiftRate, decShiftRate);
                    } else {
                        // already arcsec/hr, convert to deg/hr
                        var raShiftRate = result.Rate[0] / 3600.0d;
                        var decShiftRate = result.Rate[1] / 3600.0d;
                        ShiftRate = SiderealShiftTrackingRate.Create(raShiftRate, decShiftRate);
                    }
                    ShiftRateAxis = result.Axes;
                    ShiftEnabled = true;
                } else {
                    ShiftEnabled = false;
                }
                return true;
            } catch (Exception e) {
                ShiftEnabled = false;
                Logger.Error("Failed to get shift lock parameters", e);
                return false;
            }
        }

        private async Task<bool> WaitForGuidingStarted(IProgress<ApplicationStatus> progress, CancellationToken ct) {
            if (await WaitForAppState(PhdAppState.GUIDING, ct)) {
                progress?.Report(new ApplicationStatus { Status = Loc.Instance["LblStartGuiding"], Status2 = Loc.Instance["LblPHD2StartGuiding"] });
                Settling = true;
                return true;
            } else {
                return false;
            }
        }

        public async Task<bool> StopGuiding(CancellationToken ct) {
            if (!Connected) {
                return false;
            }
            try {
                string state = await GetAppState(3000);
                if (state != PhdAppState.GUIDING && state != PhdAppState.CALIBRATING && state != PhdAppState.LOSTLOCK) {
                    Logger.Info($"Phd2 - Stop Guiding skipped, as the app is already in state {state}");
                    return false;
                }
                return await StopCapture(ct);
            } catch (IOException ee) // communication error with phd2
              {
                Logger.Error(ee);
                return false;
            }
        }

        private async Task<bool> StopCapture(CancellationToken token) {
            if (!Connected) {
                return false;
            }
            var stopCapture = new Phd2StopCapture();
            var stopCaptureResult = await SendMessage(
                stopCapture,
                10000); // triage: reported deadlock hanging of phd2+nina - 10s timeout

            if (stopCaptureResult == null || stopCaptureResult.error != null) {
                return false;
            }

            return await WaitForAppState(
                PhdAppState.STOPPED,
                token,
                10000);  // triage: reported deadlock hanging of phd2+nina - 10s timeout
        }

        public bool CanClearCalibration => true;

        public bool CanSetShiftRate => true;

        public bool CanGetLockPosition => true;

        private bool shiftEnabled;
        public bool ShiftEnabled {
            get => shiftEnabled;
            private set {
                shiftEnabled = value;
                RaisePropertyChanged();
            }
        }

        private SiderealShiftTrackingRate shiftRate = SiderealShiftTrackingRate.Disabled;
        public SiderealShiftTrackingRate ShiftRate {
            get => shiftRate;
            private set {
                shiftRate = value;
                RaisePropertyChanged();
            }
        }

        private string? shiftRateAxis;
        private IPAddress? phd2Ip;

        public string? ShiftRateAxis {
            get => shiftRateAxis;
            private set {
                shiftRateAxis = value;
                RaisePropertyChanged();
            }
        }

        public async Task<bool> ClearCalibration(CancellationToken ct) {
            if (Connected) {
                var clearMessage = new Phd2ClearCalibration() {
                    Parameters = new string[] { "Both" }
                };
                var clearGuidance = await SendMessage(clearMessage, 10000);

                if (clearGuidance == null || clearGuidance.error != null) {
                    return false;
                }

                await Task.Delay(100, ct); // give time for PHD2 to clear the guidance
            }
            return true;
        }

        public Task<GenericPhdMethodResponse> SendMessage(Phd2Method msg, int receiveTimeout = 60000) {
            return SendMessage<GenericPhdMethodResponse>(msg, receiveTimeout);
        }

        public async Task<T> SendMessage<T>(Phd2Method msg, int receiveTimeout = 60000) where T : PhdMethodResponse {
            try {
                var serializedMessage = JsonConvert.SerializeObject(msg, new JsonSerializerSettings() { NullValueHandling = NullValueHandling.Ignore });
                Logger.Debug($"Phd2 - Sending message '{serializedMessage}'");

                using var client = new TcpClient() {
                    ReceiveTimeout = receiveTimeout,
                    SendTimeout = receiveTimeout,
                    NoDelay = true,
                };

                await client.ConnectAsync(phd2Ip, profileService.ActiveProfile.GuiderSettings.PHD2ServerPort);
                var stream = client.GetStream();
                var data = Encoding.ASCII.GetBytes(serializedMessage + Environment.NewLine);

                await stream.WriteAsync(data);

                using var reader = new StreamReader(stream, Encoding.UTF8);
                string line;

                while ((line = await reader.ReadLineAsync()) != null) {
                    var o = JObject.Parse(line);
                    string phdevent = "";
                    var t = o.GetValue("id", StringComparison.Ordinal);
                    if (t != null) {
                        phdevent = t.ToString();
                    }
                    if (phdevent == msg.Id) {
                        Logger.Debug($"Phd2 - Received message answer '{line}'");
                        var response = o.ToObject<T>();
                        CheckPhdError(response);
                        return response;
                    }
                }
            } catch (Exception ex) {
                Logger.Error("Phd2 error while sending messge", ex);
            }

            var genericError = Activator.CreateInstance<T>();
            genericError.id = msg.Id.ToString();
            genericError.error = new PhdError() { code = -1, message = "Unable to get response from phd2" };
            return genericError;
        }

        public async Task<bool> SetShiftRate(SiderealShiftTrackingRate shiftTrackingRate, CancellationToken ct) {
            if (!shiftTrackingRate.Enabled) {
                return await StopShifting(ct);
            }

            ShiftRate = shiftTrackingRate;
            double raArcsecPerHour = shiftTrackingRate.RAArcsecsPerHour;
            double decArcsecPerHour = shiftTrackingRate.DecArcsecsPerHour;
            Logger.Info($"Setting shift rate to RA={raArcsecPerHour}, Dec={decArcsecPerHour}");
            try {
                var setLockShiftMsg = new Phd2SetLockShiftParams() {
                    Parameters = new Phd2SetLockShiftParamsParameter() {
                        Axes = "RA/Dec",
                        Units = "arcsec/hr",
                        Rate = [raArcsecPerHour, decArcsecPerHour]
                    }
                };
                var lockShiftResponse = await SendMessage(setLockShiftMsg);
                if (lockShiftResponse.error != null) {
                    Logger.Error($"Failed to set shift rate to RA={raArcsecPerHour}, Dec={decArcsecPerHour}. Code={lockShiftResponse.error.code}, Message={lockShiftResponse.error.message}");
                    return false;
                }

                var setLockShiftEnabledMsg = new Phd2SetLockShiftEnabled() {
                    Parameters = new bool[] { true }
                };
                var setLockShiftEnabledResponse = await SendMessage(setLockShiftEnabledMsg);
                if (setLockShiftEnabledResponse.error != null) {
                    Logger.Error($"Failed to enable lock shift. Code={lockShiftResponse.error.code}, Message={lockShiftResponse.error.message}");
                    return false;
                }

                _ = TryRefreshShiftLockParams();
                return true;
            } catch (Exception e) {
                Logger.Error("Failed to set shift rate", e);
                return false;
            }
        }

        public async Task<bool> StopShifting(CancellationToken ct) {
            Logger.Info($"Stop shifting");
            try {
                if (!Connected || !ShiftEnabled) {
                    return true;
                }

                var setLockShiftEnabledMsg = new Phd2SetLockShiftEnabled() {
                    Parameters = new bool[] { false }
                };
                var setLockShiftEnabledResponse = await SendMessage(setLockShiftEnabledMsg);
                if (setLockShiftEnabledResponse.error != null) {
                    Logger.Error($"Failed to disable lock shift. Code={setLockShiftEnabledResponse.error.code}, Message={setLockShiftEnabledResponse.error.message}");
                    return false;
                }

                _ = TryRefreshShiftLockParams();
                return true;
            } catch (Exception e) {
                Logger.Error("Failed to disable shift", e);
                return false;
            }
        }

        public void Disconnect() {
            initialized = false;
            phd2Ip = null;
            try { _clientCTS?.Cancel(); } catch { }
        }

        private async Task ProcessEvent(string phdevent, JObject message) {
            switch (phdevent) {
                case "Resumed": {
                        break;
                    }
                case "Version": {
                        Version = message.ToObject<PhdEventVersion>();
                        break;
                    }
                case "AppState": {
                        AppState = message.ToObject<PhdEventAppState>();
                        break;
                    }
                case "GuideStep": {
                        AppState = new PhdEventAppState() { State = "Guiding" };
                        var step = message.ToObject<PhdEventGuideStep>();
                        GuideEvent?.Invoke(this, step);
                        break;
                    }
                case "GuidingDithered": {
                        GuidingDithered = message.ToObject<PhdEventGuidingDithered>();
                        break;
                    }
                case "Settling": {
                        var settleInfo = message.ToObject<PhdEventSettling>();
                        Settling = true;
                        Logger.Debug($"PHD2 settling started. Time: {settleInfo.Time}, Distance: {settleInfo.Distance}");
                        break;
                    }
                case "SettleDone": {
                        GuidingDithered = null;
                        Settling = false;
                        var settleDone = message.ToObject<PhdEventSettleDone>();
                        if (settleDone.Error != null) {
                            Logger.Error("PHD2 error:" + settleDone.Error);
                            Notifier.ShowExternalWarning(settleDone.Error, Loc.Instance["LblPhd2Warning"]);
                        } else {
                            Logger.Debug("PHD2 settle completed");
                        }
                        break;
                    }
                case "Paused": {
                        AppState = new PhdEventAppState() { State = "Paused" };
                        break;
                    }
                case "StartCalibration": {
                        AppState = new PhdEventAppState() { State = "Calibrating" };
                        break;
                    }
                case "LoopingExposures": {
                        AppState = new PhdEventAppState() { State = "Looping" };
                        break;
                    }
                case "LoopingExposuresStopped": {
                        AppState = new PhdEventAppState() { State = "Stopped" };
                        break;
                    }
                case "CalibrationComplete": {
                        break;
                    }
                case "StarSelected": {
                        Logger.Debug($"PHD2 - Star selected");
                        break;
                    }
                case "StarLost": {
                        var starlost = message.ToObject<PhdEventStarLost>();
                        Logger.Debug($"PHD2 - Star lost! Status: {starlost.Status}");
                        AppState = new PhdEventAppState() { State = "LostLock" };
                        break;
                    }
                case "StartGuiding": {
                        break;
                    }
                case "LockPositionSet": {
                        var lockPosition = message.ToObject<PhdEventLockPositionSet>();
                        Logger.Debug($"PHD2 - Lock position set at x:{lockPosition.X} y:{lockPosition.Y}");
                        break;
                    }
                case "LockPositionLost": {
                        Logger.Debug($"PHD2 - Lock position lost!");
                        AppState = new PhdEventAppState() { State = "LostLock" };
                        break;
                    }
                case "LockPositionShiftLimitReached": {
                        Logger.Debug($"PHD2 - LockPositionShiftLimitReached!");
                        _ = RestartForLostShiftLock();
                        break;
                    }
                case "ConfigurationChange": {
                        if(initialized) { 
                            Logger.Debug($"PHD2 - ConfigurationChange!");
                            _ = SetPixelScale();
                        }
                        break;
                    }
                default: {
                        break;
                    }
            }
        }

        private async Task GetProfiles() {
            var getProfile = new Phd2GetProfile();
            var getProfileResponse = await SendMessage<GetProfileResponse>(getProfile);
            if (getProfileResponse.error != null) {
                Logger.Error($"Failed GetProfile: {getProfileResponse.error}");
                throw new InvalidOperationException(Loc.Instance["LblPhd2FailedGetProfiles"]);
            }

            var getProfiles = new Phd2GetProfiles();
            var getProfilesResponse = await SendMessage<GetProfilesResponse>(getProfiles);
            if (getProfilesResponse.error != null) {
                Logger.Error($"Failed GetProfiles: {getProfilesResponse.error}");
                throw new InvalidOperationException(Loc.Instance["LblPhd2FailedGetProfiles"]);
            }

            _activeProfile = getProfileResponse.result;
            AvailableProfiles.Clear();
            foreach (var profile in getProfilesResponse.result) {
                AvailableProfiles.Add(new Phd2Profile { Name = profile.name, Id = profile.id ?? 0 });
            }
            SelectedProfile = AvailableProfiles.FirstOrDefault(x => x.Id == _activeProfile?.id);
        }

        private async Task<bool> EnsurePHD2EquipmentConnected() {
            var getConnected = new Phd2GetConnected();
            var getConnectedResult = await SendMessage(getConnected);
            if (getConnectedResult.error != null) {
                Notifier.ShowWarning(Loc.Instance["LblPhd2FailedEquipmentConnection"]);
                return false;
            }

            if (!(bool)getConnectedResult.result) {
                var setConnected = new Phd2SetConnected() {
                    Parameters = new bool[] { true }
                };
                var setConnectedResult = await SendMessage(setConnected);
                if (setConnectedResult.error != null) {
                    Notifier.ShowWarning(Loc.Instance["LblPhd2FailedEquipmentConnection"]);
                    return false;
                }
            }

            return true;
        }

        private async Task DisconnectPHD2Equipment() {
            await StopCapture(default);
            var setDisconnected = new Phd2SetConnected() {
                Parameters = new bool[] { false }
            };
            var setDisconnectedResult = await SendMessage(setDisconnected);
            if (setDisconnectedResult.error != null) {
                Logger.Error($"Failed to disconnect PHD2equipment: {setDisconnectedResult.error}");
            }
        }

        private async Task RunListener() {
            var jls = new JsonLoadSettings() { LineInfoHandling = LineInfoHandling.Ignore, CommentHandling = CommentHandling.Ignore };
            _clientCTS?.Dispose();
            _clientCTS = new CancellationTokenSource();

            try {
                using var client = new TcpClient(AddressFamily.InterNetwork) {
                    NoDelay = true,
                };

                await client.ConnectAsync(phd2Ip, profileService.ActiveProfile.GuiderSettings.PHD2ServerPort);
                Connected = true;
                _tcs.TrySetResult(true);

                using NetworkStream s = client.GetStream();
                // leaveOpen: true — the `using NetworkStream s` (and the TcpClient) own the stream's
                // lifetime; without this the StreamReader would dispose it too (a redundant double-dispose).
                using var reader = new StreamReader(s, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);

                // Read the PHD2 event stream line-by-line. ReadLineAsync blocks until a full line
                // arrives, returns null at EOF (the guider closed the socket), or throws on a reset —
                // so the read itself IS the liveness signal. This replaces the previous
                // GetActiveTcpConnections()/DataAvailable + 500ms poll, which was a busy-loop and
                // crashed on macOS (the TCP-table enumeration returns null/duplicate endpoints).
                // Reaching the end (null) or an exception falls through to the finally, which fires
                // PHD2ConnectionLost — the §63.3 recovery trigger.
                string line;
                while ((line = await reader.ReadLineAsync(_clientCTS.Token).ConfigureAwait(false)) != null) {
                    if (line.Length == 0 || line[0] != '{') {
                        continue;
                    }
                    JObject o;
                    try {
                        o = JObject.Parse(line, jls);
                    } catch (Newtonsoft.Json.JsonReaderException ex) {
                        // A malformed/partial frame (e.g. mid-reset) shouldn't kill the whole listener —
                        // skip the bad line and keep reading. A true close still surfaces as EOF below.
                        Logger.Warning($"Skipping unparseable PHD2 event line: {ex.Message}");
                        continue;
                    }
                    JToken t = o.GetValue("Event", StringComparison.Ordinal);
                    if (t != null) {
                        var phdevent = t.ToString();
                        Logger.Trace($"PHD2 event received - {o}");
                        await ProcessEvent(phdevent, o).ConfigureAwait(false);
                    }
                }

                // EOF (ReadLineAsync == null): the guider closed the connection — a clean shutdown or
                // a crash, indistinguishable at this layer. Either way it's a normal disconnect, not a
                // user-facing error: just log it and fall out of the try so the finally fires
                // PHD2ConnectionLost and §63.3 recovery decides whether to restart. (Only a reset /
                // unexpected read exception below takes the error-notification path.)
                Logger.Info("PHD2 closed the event-stream connection (EOF).");
            } catch (OperationCanceledException) {
            } catch (Exception ex) {
                Logger.Error(ex);
                Notifier.ShowError(String.Format(CultureInfo.InvariantCulture, Loc.Instance["LblPHDErrorMsg"], ex.Message));
                throw;
            } finally {
                Settling = false;
                AppState = new PhdEventAppState() { State = "" };
                PixelScale = 0.0d;
                Connected = false;
                _tcs.TrySetResult(false);
                PHD2ConnectionLost?.Invoke(this, EventArgs.Empty);
            }
        }

        // Headless: no setup dialog. PHD2 host/port/profile come from profile.json (§63).
        public void SetupDialog() { }

        public event EventHandler? PHD2ConnectionLost;

        public event EventHandler<IGuideStep>? GuideEvent;

        public IList<string> SupportedActions => [];

        public string Action(string actionName, string actionParameters) {
            throw new NotImplementedException();
        }

        public string SendCommandString(string command, bool raw) {
            throw new NotImplementedException();
        }

        public bool SendCommandBool(string command, bool raw) {
            throw new NotImplementedException();
        }

        public void SendCommandBlind(string command, bool raw) {
            throw new NotImplementedException();
        }
    }
}
