#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace OpenAstroAra.Server.Services;

// #1362 / §63.20 — re-read the guide camera's PixelSizeX and UniqueID once the guider has connected, and
// correct the active profile (decision: GuiderCameraReconcile). Runs after the connect succeeded, off the
// #1308 pin-check refusal path, and never changes the connect's outcome.
public sealed partial class GuiderService {

    private static readonly TimeSpan ReconcileTimeout = TimeSpan.FromSeconds(10);

    // The last reconcile and how many have started, for tests to await; the connect itself never waits on it.
    // A reconcile that finishes synchronously returns the cached completed task, so a test cannot tell a
    // new one from the initial value by reference: it waits for the count instead.
    private int _reconcileStarts;
    internal Task ReconcileTask { get; private set; } = Task.CompletedTask;
    internal int ReconcileStarts => Volatile.Read(ref _reconcileStarts);

    /// <summary>The connect hook: called by <c>ConnectInBackground</c> after the guider reached Connected.
    /// Also runs on a §63.3 recovery reconnect, which is the same event for the camera.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort boundary after a successful connect: a camera that is unreachable, an Alpaca error or a store failure must leave the profile as it is and never fail the connect. Log-and-recover.")]
    internal async Task ReconcileGuideCameraAsync(PHD2Guider guider, CancellationToken ct) {
        try {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ReconcileTimeout);
            var settings = _profileService.ActiveProfile.GuiderSettings;
            var camera = settings.GuiderCamera;
            var pinnedId = settings.GuiderCameraAlpacaUniqueId;
            var oldPixelSize = settings.GuidePixelSize;
            if (GuiderCameraPinCheck.CameraEndpoint(settings) is not { } ep) {
                return;
            }
            IReadOnlyDictionary<string, AlpacaConfiguredDevice> devices = new Dictionary<string, AlpacaConfiguredDevice>();
            try {
                if (_alpaca is not null) {
                    devices = await _alpaca.GetConfiguredDevicesAsync(ep.Host, ep.Port, cts.Token).ConfigureAwait(false);
                }
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                // The client already maps failures to an empty map; a throwing stand-in means the same: unknown.
            }
            var endpoint = $"{GuiderCameraReconcile.ForLog(ep.Host)}:{ep.Port.ToString(CultureInfo.InvariantCulture)}/{ep.Device.ToString(CultureInfo.InvariantCulture)}";
            // Identity first: a pin mismatch is logged whatever else holds the slot, even the main camera,
            // and the slot is not read because its size may belong to the other camera.
            if (GuiderCameraReconcile.Decide(pinnedId, oldPixelSize, ep.Device, devices, readPixelSize: null).Outcome
                    == GuiderCameraReconcileOutcome.PinMismatch) {
                var slotId = devices.TryGetValue(GuiderCameraReconcile.SlotKey(ep.Device), out var d) ? d.UniqueId : "";
                var pinText = GuiderCameraReconcile.ForLog(pinnedId);
                var slotText = GuiderCameraReconcile.ForLog(slotId);
                LogGuideCameraPinMismatch(endpoint, slotText, pinText);
                return;
            }
            // The slot resolves to the main imaging camera (a renumber): its id and pixel size are not the
            // guide camera's, so learn nothing. The push refuses this selection with its own reason (#1308).
            if (_mainCameraResolver?.Invoke() is { } main
                && GuiderCameraPinCheck.Describe(settings, devices, main) is not null) {
                return;
            }
            double? read = null;
            try {
                read = await guider.GetAlpacaCameraPixelSizeAsync(ep.Host, ep.Port, ep.Device, cts.Token).ConfigureAwait(false);
            } catch (Exception ex) when (ex is InvalidOperationException or GuiderRpcException) {
                // Unreadable: no size to learn; an id can still be filled below.
            }

            var decision = GuiderCameraReconcile.Decide(pinnedId,
                oldPixelSize, ep.Device, devices, read);
            if (decision.Outcome != GuiderCameraReconcileOutcome.Update) {
                return;
            }
            if (!TryApplyGuideCameraProfile(camera, pinnedId, decision)) {
                return; // the selection or the pin changed while we read: do not overwrite the newer edit
            }
            var oldSizeText = oldPixelSize.ToString("R", CultureInfo.InvariantCulture);
            var newSizeText = (decision.NewPixelSize ?? oldPixelSize).ToString("R", CultureInfo.InvariantCulture);
            var oldIdText = GuiderCameraReconcile.ForLog(pinnedId);
            var newIdText = GuiderCameraReconcile.ForLog(decision.NewUniqueId ?? pinnedId);
            LogGuideCameraReconciled(endpoint, oldSizeText, newSizeText, oldIdText, newIdText);
        } catch (OperationCanceledException) {
            // Superseded or timed out: leave the profile as it is.
        } catch (Exception ex) {
            LogGuideCameraReconcileFailed(ex);
        }
    }

    // The store is the source of truth (the in-memory settings are hydrated from it on every Put), so the
    // write goes through it; a headless service with no store sets the active profile directly.
    private bool TryApplyGuideCameraProfile(
            string camera, string pinnedId, GuiderCameraReconcileDecision decision) {
        var live = _profileService.ActiveProfile.GuiderSettings;
        if (live.GuiderCamera != camera || live.GuiderCameraAlpacaUniqueId != pinnedId) {
            return false;
        }
        if (_profileStore is null) {
            if (decision.NewPixelSize is { } p) live.GuidePixelSize = p;
            if (decision.NewUniqueId is { } id) live.GuiderCameraAlpacaUniqueId = id;
            return true;
        }
        var now = _profileStore.GetPhd2Settings();
        if (now.GuiderCamera.Trim() != camera || now.GuiderCameraAlpacaUniqueId.Trim() != pinnedId) {
            return false;
        }
        _profileStore.PutPhd2Settings(now with {
            GuidePixelSize = decision.NewPixelSize ?? now.GuidePixelSize,
            GuiderCameraAlpacaUniqueId = decision.NewUniqueId ?? now.GuiderCameraAlpacaUniqueId,
        });
        return true;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Guide camera {Endpoint} re-read on connect: pixel size {OldPixelSize} -> {NewPixelSize}, UniqueID '{OldUniqueId}' -> '{NewUniqueId}' (#1362)")]
    private partial void LogGuideCameraReconciled(string endpoint, string oldPixelSize, string newPixelSize, string oldUniqueId, string newUniqueId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Guide camera {Endpoint} reports UniqueID '{SlotUniqueId}' but the profile pins '{PinnedUniqueId}': profile left unchanged, pick the guide camera again (#1308, #1362)")]
    private partial void LogGuideCameraPinMismatch(string endpoint, string slotUniqueId, string pinnedUniqueId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Guide camera re-read on connect failed; profile left unchanged (#1362)")]
    private partial void LogGuideCameraReconcileFailed(Exception ex);
}
