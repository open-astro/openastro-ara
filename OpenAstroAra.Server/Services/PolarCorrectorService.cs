#region "copyright"
/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors
    This file is part of OpenAstro Ara (forked from N.I.N.A.).
    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/
#endregion "copyright"

using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>#1972/#1973 — the rig's Polar Corrector for the API and the emergency stop.</summary>
public interface IPolarCorrectorService {
    /// <summary>True while a connected device offers the PolarAlign actions.</summary>
    bool IsPresent { get; }
    /// <summary>Never throws for a device error: what could not be read is reported as unknown.</summary>
    Task<PolarCorrectorDto> GetAsync(CancellationToken ct);
    /// <summary>Stops both axes. Throws InvalidOperationException (→ 409) with no Polar Corrector
    /// connected or when the device refuses, carrying the device's text.</summary>
    Task<OperationAcceptedDto> StopAsync(string? idempotencyKey, CancellationToken ct);
    /// <summary>Sends the device home; same refusals as <see cref="StopAsync"/>.</summary>
    Task<OperationAcceptedDto> HomeAsync(string? idempotencyKey, CancellationToken ct);
}

/// <summary>One connected device that lends its driver actions: a Switch, or the mount.</summary>
internal sealed record DriverActionsCarrier(string DeviceId, string Name, IDriverActions Actions);

/// <summary>The connected devices of one service that can carry driver actions.</summary>
internal interface IDriverActionsCarriers {
    IReadOnlyList<DriverActionsCarrier> ConnectedActionCarriers();
}

/// <summary>A connected device's actions, read once per connect. <see cref="Action"/> refuses once
/// that connection is no longer live, so a call never reaches a superseded or disposed client.</summary>
internal sealed class LiveDriverActions(IReadOnlyList<string> supportedActions, Func<bool> isLive,
        Func<string, string, string> action) : IDriverActions {
    public IReadOnlyList<string> SupportedActions => supportedActions;

    public string Action(string name, string parameters) =>
        isLive() ? action(name, parameters) : throw new ASCOM.NotConnectedException("the device is no longer connected");

    /// <summary>A driver that throws or NotImplements SupportedActions lists nothing.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A driver may NotImplement SupportedActions or fail the read; no actions is the correct answer.")]
    public static IReadOnlyList<string> Read(Func<IList<string>?> supportedActions) {
        try {
            return supportedActions()?.ToArray() ?? [];
        } catch (Exception) {
            return [];
        }
    }
}

/// <summary>
/// #1972/#1973 — derives the rig's Polar Corrector from the connected devices: a Switch that offers
/// the PolarAlign actions wins over the mount (a separate base was added for the job), and among such
/// switches the lowest UniqueId (ordinal) wins. No profile field names it.
/// </summary>
internal sealed class PolarCorrectorService(IDriverActionsCarriers switches, IDriverActionsCarriers mount,
        Func<IPolarCorrectorDriver?>? routine = null)
        : IPolarCorrectorService, IPolarCorrectorSource {
    private readonly object _gate = new();
    // Info is static per connection; one connection's reply is kept (the carrier object is per connect).
    private (IDriverActions Carrier, PolarCorrectorInfo Info)? _info;

    private sealed record Chosen(string Carrier, DriverActionsCarrier Device);

    private static readonly PolarCorrectorDto None = new(
        Present: false, Carrier: null, DeviceId: null, Name: null, Drivable: false, NotDrivableReason: null,
        Capabilities: null, Moving: false, AzimuthPositionArcmin: null, AltitudePositionArcmin: null,
        AzimuthFault: null, AltitudeFault: null);

    public bool IsPresent => Choose() is not null;

    public IPolarCorrector? Current() => Choose() is { } chosen ? new PolarAlignActionsCorrector(chosen.Device.Actions) : null;

    public async Task<PolarCorrectorDto> GetAsync(CancellationToken ct) {
        var chosen = Choose();
        return chosen is null ? None : await Task.Run(() => Read(chosen), ct).ConfigureAwait(false);
    }

    public async Task<OperationAcceptedDto> StopAsync(string? idempotencyKey, CancellationToken ct) {
        // A routine that is moving the motors ends its loop first: to it a device Stop alone looks like
        // a finished move, and its next measurement would move them again.
        if (routine?.Invoke() is { DrivesPolarCorrector: true } driver) {
            await driver.StopPolarCorrectorAsync().ConfigureAwait(false);
        }
        return await CommandAsync(c => c.Stop(), "polarcorrector.stop", idempotencyKey).ConfigureAwait(false);
    }

    public Task<OperationAcceptedDto> HomeAsync(string? idempotencyKey, CancellationToken ct) =>
        routine?.Invoke() is { DrivesPolarCorrector: true }
            ? throw new InvalidOperationException("polar alignment is moving the Polar Corrector; stop the motors first")
            : CommandAsync(c => c.Home(), "polarcorrector.home", idempotencyKey);

    private Chosen? Choose() {
        var sw = switches.ConnectedActionCarriers()
            .Where(c => PolarAlignActionsCorrector.IsOfferedBy(c.Actions.SupportedActions))
            .OrderBy(c => c.DeviceId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (sw is not null) {
            return new Chosen("switch", sw);
        }
        var m = mount.ConnectedActionCarriers().FirstOrDefault(c => PolarAlignActionsCorrector.IsOfferedBy(c.Actions.SupportedActions));
        return m is null ? null : new Chosen("mount", m);
    }

    private async Task<OperationAcceptedDto> CommandAsync(Action<IPolarCorrector> command, string operationType, string? idempotencyKey) {
        var corrector = Current() ?? throw new InvalidOperationException("no Polar Corrector is connected");
        try {
            await Task.Run(() => command(corrector), CancellationToken.None).ConfigureAwait(false);
        } catch (PolarCorrectorException ex) {
            throw new InvalidOperationException(ex.Message, ex);
        }
        return new OperationAcceptedDto(Guid.NewGuid(), operationType, DateTimeOffset.UtcNow, idempotencyKey);
    }

    private PolarCorrectorDto Read(Chosen chosen) {
        var corrector = new PolarAlignActionsCorrector(chosen.Device.Actions);
        var info = CachedInfo(chosen.Device.Actions, corrector);
        PolarCorrectorStatus? status;
        try {
            status = corrector.Status();
        } catch (PolarCorrectorException) {
            status = null;
        }
        return new PolarCorrectorDto(
            Present: true,
            Carrier: chosen.Carrier,
            DeviceId: chosen.Carrier == "switch" ? chosen.Device.DeviceId : null,
            Name: chosen.Device.Name,
            Drivable: info?.HostDriven == true,
            NotDrivableReason: info is null ? "info_unreadable" : info.HostDriven ? null : "device_runs_its_own_loop",
            Capabilities: info is null ? null : new PolarCorrectorCapabilitiesDto(
                info.DeviceName, info.DeviceVersion,
                info.Azimuth?.MaxMoveArcmin, info.Azimuth?.ResolutionArcmin,
                info.Altitude?.MaxMoveArcmin, info.Altitude?.ResolutionArcmin,
                info.SimultaneousMoves, info.MovesWhileTracking, info.MovesWhileSlewing, info.CanStop, info.SettleSeconds),
            Moving: status?.Moving == true,
            AzimuthPositionArcmin: status?.Azimuth.PositionArcmin,
            AltitudePositionArcmin: status?.Altitude.PositionArcmin,
            AzimuthFault: status?.Azimuth.Fault,
            AltitudeFault: status?.Altitude.Fault);
    }

    private PolarCorrectorInfo? CachedInfo(IDriverActions carrier, PolarAlignActionsCorrector corrector) {
        lock (_gate) {
            if (_info is { } cached && ReferenceEquals(cached.Carrier, carrier)) {
                return cached.Info;
            }
        }
        try {
            var info = corrector.Info();
            lock (_gate) {
                _info = (carrier, info);
            }
            return info;
        } catch (PolarCorrectorException) {
            return null; // not cached: the next GET asks again
        }
    }
}
