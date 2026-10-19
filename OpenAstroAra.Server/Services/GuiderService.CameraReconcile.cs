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
using OpenAstroAra.Server.Contracts;
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

    /// <summary>The on-demand entry point (a sequence start, #1362): the same reconcile as the connect hook,
    /// a no-op while the guider is not connected.</summary>
    internal Task ReconcileGuideCameraIfConnectedAsync(CancellationToken ct) {
        PHD2Guider guider;
        lock (_gate) {
            if (_disposed || _guider is null || _state != EquipmentConnectionState.Connected) {
                return Task.CompletedTask;
            }
            guider = _guider;
        }
        return ReconcileGuideCameraAsync(guider, ct);
    }

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
            devices.TryGetValue(GuiderCameraReconcile.SlotKey(ep.Device), out var atSlot);
            // Identity first. The slot resolving to the main imaging camera (a renumber) is never learned
            // from: its id and pixel size are not the guide camera's, and the push refuses it (#1308).
            var isMain = _mainCameraResolver?.Invoke() is { } main
                && GuiderCameraPinCheck.IsMainCamera(settings, devices, main);
            var mismatch = GuiderCameraReconcile.Decide(pinnedId, oldPixelSize, ep.Device, devices, readPixelSize: null).Outcome
                == GuiderCameraReconcileOutcome.PinMismatch;
            if (mismatch && isMain) {
                LogGuideCameraPinMismatch(endpoint, GuiderCameraReconcile.ForLog(atSlot?.UniqueId), GuiderCameraReconcile.ForLog(pinnedId));
                return;
            }
            if (isMain) {
                return;
            }
            if (mismatch && GuiderCameraPinCheck.MovedTo(pinnedId, devices) is { } movedTo) {
                // The pinned camera is still on the server at another number (a bridge renumber): #1308's
                // "has moved" case. The push refuses it and the user picks again; re-pinning whatever sits at
                // the old number would follow the wrong camera and silence that refusal for good.
                LogGuideCameraMoved(endpoint, GuiderCameraReconcile.ForLog(pinnedId), movedTo, GuiderCameraReconcile.ForLog(atSlot?.UniqueId));
                return;
            }
            if (mismatch && atSlot is null) {
                return; // the pinned camera is gone and nothing answers at its number: nothing to learn from
            }
            if (mismatch) {
                // #1362 — a different camera answers in the slot and it is not the imaging camera: the gear was
                // swapped behind the same connection. The slot is now the guide camera, so its id is pinned and
                // its pixel size read; the identity reconciler tells the user and clears the darks and calibration.
                LogGuideCameraSwapped(endpoint, GuiderCameraReconcile.ForLog(atSlot!.UniqueId), GuiderCameraReconcile.ForLog(pinnedId));
            }
            double? read = null;
            try {
                read = await guider.GetAlpacaCameraPixelSizeAsync(ep.Host, ep.Port, ep.Device, cts.Token).ConfigureAwait(false);
            } catch (Exception ex) when (ex is InvalidOperationException or GuiderRpcException) {
                // Unreadable: no size to learn; an id can still be filled below.
            }

            // A swap decides as if unpinned (the old pin is the previous camera's); the apply below still
            // demands the stored pin is the one this reconcile started from.
            var decision = GuiderCameraReconcile.Decide(mismatch ? string.Empty : pinnedId,
                oldPixelSize, ep.Device, devices, read);
            if (decision.Outcome == GuiderCameraReconcileOutcome.Update
                    && !TryApplyGuideCameraProfile(camera, pinnedId, decision)) {
                return; // the selection or the pin changed while we read: do not overwrite the newer edit
            }
            if (_identity is not null && atSlot is not null) {
                // On a swap the old pin is the previous camera's identity: a pre-#1362 profile has no stored
                // guidecamera entry, and the notice and invalidation must not be lost to a "first sighting".
                var previous = mismatch ? new DeviceIdentityDto(pinnedId, string.Empty, DateTimeOffset.MinValue) : null;
                await _identity.ObserveAsync(GearSlot.GuideCamera, atSlot, cts.Token, previous).ConfigureAwait(false);
            }
            if (decision.Outcome != GuiderCameraReconcileOutcome.Update) {
                return;
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
        Message = "Guide camera {Endpoint} reports UniqueID '{SlotUniqueId}' but the profile pins '{PinnedUniqueId}' and the slot is the main imaging camera: profile left unchanged, pick the guide camera again (#1308, #1362)")]
    private partial void LogGuideCameraPinMismatch(string endpoint, string slotUniqueId, string pinnedUniqueId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Guide camera {Endpoint}: the pinned camera '{PinnedUniqueId}' moved to {MovedTo} and the slot now reports '{SlotUniqueId}': profile left unchanged, pick the guide camera again (#1308, #1362)")]
    private partial void LogGuideCameraMoved(string endpoint, string pinnedUniqueId, string movedTo, string slotUniqueId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Guide camera {Endpoint} reports UniqueID '{SlotUniqueId}' but the profile pinned '{PinnedUniqueId}': the guide camera was swapped, re-pinning to the camera in the slot (#1362)")]
    private partial void LogGuideCameraSwapped(string endpoint, string slotUniqueId, string pinnedUniqueId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Guide camera re-read on connect failed; profile left unchanged (#1362)")]
    private partial void LogGuideCameraReconcileFailed(Exception ex);
}
