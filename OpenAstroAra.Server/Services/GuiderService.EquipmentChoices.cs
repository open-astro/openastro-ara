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
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

// §63.17 (PR 1) — surface the guider's equipment-selection reads over the §6 REST surface: the per-slot
// device choices (drives the client's equipment pickers) and the daemon-side Alpaca discovery sweep. The
// set_selected_* apply path + profile push is §63.17 PR 2.
public sealed partial class GuiderService {

    /// <summary>§63.17 — read the daemon's per-slot equipment choices. Same disconnected contract as
    /// <see cref="GetCalibrationFilesStatusAsync"/>: null (not an error) when no guider is connected, including
    /// when a disconnect races the read.</summary>
    public async Task<GuiderEquipmentChoicesDto?> GetEquipmentChoicesAsync(CancellationToken ct) {
        PHD2Guider? guider;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            guider = _state == EquipmentConnectionState.Connected ? _guider : null;
        }
        if (guider is null) {
            return null;
        }
        Phd2EquipmentChoices choices;
        try {
            choices = await guider.GetEquipmentChoicesAsync(ct).ConfigureAwait(false);
        } catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or GuiderRpcException) {
            // Raced with a disconnect/dispose after capturing the guider outside the gate — a drop mid-RPC
            // surfaces as a transport error, not always InvalidOperationException. All map to the disconnected
            // contract (null) rather than a 500.
            return null;
        }
        return new GuiderEquipmentChoicesDto(
            Cameras: choices.Camera ?? [],
            Mounts: choices.Mount ?? [],
            AuxMounts: choices.AuxMount ?? [],
            AdaptiveOptics: choices.AdaptiveOptics ?? [],
            Rotators: choices.Rotator ?? []);
    }

    /// <summary>§63.17 — daemon-side Alpaca discovery. Unlike the choices read this is an explicit user action,
    /// so a disconnected guider is an error (409 via InvalidOperationException), a bad range is a 400
    /// (ArgumentOutOfRangeException, validated before the socket), and a daemon rejection is a 422
    /// (GuiderRpcException) — the caller asked for a sweep ARA can't run and must be told why.</summary>
    // Non-async outer so RequireConnectedGuider's "not connected" and the range validation surface
    // synchronously — same contract as the other explicit guide ops.
    public Task<GuiderAlpacaDiscoveryDto> DiscoverAlpacaServersAsync(
            DiscoverAlpacaServersRequestDto request, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        // Validate the ranges now (throws ArgumentOutOfRangeException → 400) so a bad request never dispatches.
        PHD2Guider.DiscoverAlpacaServersRequest(request.NumQueries, request.TimeoutSeconds);
        var guider = RequireConnectedGuider();
        return RunDiscoveryAsync(guider, request, ct);
    }

    private static async Task<GuiderAlpacaDiscoveryDto> RunDiscoveryAsync(
            PHD2Guider guider, DiscoverAlpacaServersRequestDto request, CancellationToken ct) {
        IReadOnlyList<string> servers = await guider
            .DiscoverAlpacaServersAsync(request.NumQueries, request.TimeoutSeconds, ct)
            .ConfigureAwait(false);
        return new GuiderAlpacaDiscoveryDto(servers);
    }
}
