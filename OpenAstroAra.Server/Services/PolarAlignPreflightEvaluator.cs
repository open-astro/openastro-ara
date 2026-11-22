#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using ASCOM.Common.DeviceInterfaces;
using OpenAstroAra.Equipment.Equipment.MyTelescope;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.Refusals;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>#2068 (ARA-18) — what the pre-flight list reads. Every member may throw: the evaluator turns a failed read
/// into its check's status, never into an exception.</summary>
/// <param name="Stage">The measurement the next Start makes (<see cref="PolarAlignStages"/>).</param>
/// <param name="MountInfo">The mount's cached state (connected, parked, its identity).</param>
/// <param name="Site">The profile's site.</param>
/// <param name="Settings">The polar-align section, clamped (<see cref="PolarAlignSettingsClamp"/>).</param>
/// <param name="ClockRefusal">#2087's gate (<see cref="IMountClockGate.MountClockMotionRefusal"/>): null when the mount's
/// clock is verified; null delegate when this daemon wires no gate.</param>
/// <param name="PoseUnverifiedReason">#2070's position trust: null when trusted, else the reason.</param>
/// <param name="Plan">The confirm step's plan from a fresh reading of the mount (its refusals: on-axis, no fresh
/// reading, Dec past the pole, the sweep limit).</param>
/// <param name="AlignerPresent">A Polar Aligner on the rig (#1971); null delegate when this daemon wires none.</param>
/// <param name="AlignmentMode">#2083 — the mount type read at connect (<see cref="IMountMotionControl.MountAlignmentMode"/>):
/// null when unknown; null delegate when this daemon reads none.</param>
/// <param name="CanMovePrimaryAxis">#2083 — CanMoveAxis(Primary) read at connect
/// (<see cref="IMountMotionControl.CanMovePrimaryAxis"/>): null when unknown; null delegate when this daemon reads none.</param>
internal sealed record PolarAlignPreflightInputs(
    string Stage,
    Func<TelescopeInfo?> MountInfo,
    Func<SiteSettingsDto> Site,
    Func<PolarAlignSettingsDto> Settings,
    Func<string?>? ClockRefusal,
    Func<CancellationToken, Task<string?>> PoseUnverifiedReason,
    Func<CancellationToken, Task<PolarAlignStartPlanDto>> Plan,
    Func<bool>? AlignerPresent,
    Func<AlignmentMode?>? AlignmentMode = null,
    Func<bool?>? CanMovePrimaryAxis = null);

/// <summary>The pre-flight list and, for a start, the exception the first failing check refuses with (null when none
/// fails), with the confirm step's plan when it could be made.</summary>
internal sealed record PolarAlignPreflightResult(PolarAlignPreflightDto Dto, Exception? FirstFailure, PolarAlignStartPlanDto? Plan);

/// <summary>
/// #2068 (ARA-18) — Polar Align's pre-flight checks, one list for the app's Start card
/// (<c>GET /polaralign/preflight</c>) and for every start (<c>POST /polaralign/start</c>, <c>GET /polaralign/start-plan</c>,
/// the adjust screen's Check), which refuses on the first check that fails, with that check's cause. It never throws:
/// a read that fails becomes its check's status (<c>fail</c> for a check a start refuses on, so a start refuses with
/// the read's own exception, as it did before the list existed).
/// <para>The checks, in the order a start makes them: the mount connected and not parked, the site latitude, then
/// (#2083) the longitude and LST, #2087's mount clock gate, #2070's position trust, the per-mount clearance (ARA-8: no
/// motor motion until it is confirmed), the start pointing (the confirm step's plan), and (#2083) the mount type (alt-az
/// refused) and CanMoveAxis(Primary) (false refuses: Ara turns RA only with the mount's motor, #2170). The Polar Aligner's check passes: with no aligner or its motors off the user turns the bolts, and with its motors on
/// its Stop is verified and its turns capped (#2069). A last <c>warn</c> asks the user to set the
/// mount's own limits.</para>
/// </summary>
internal static class PolarAlignPreflightEvaluator {

    /// <summary>The key of a mount without a reported identity (no Alpaca UniqueID and no name).</summary>
    public const string UnidentifiedMountKey = "unidentified-mount";

    /// <summary>A mount's key in <see cref="PolarAlignSettingsDto.MountClearance"/>: the identity the daemon already keeps
    /// per device (its Alpaca UniqueID, <see cref="TelescopeInfo.DeviceId"/>: the pose store, #1362's equipment
    /// identity), else its name, else <see cref="UnidentifiedMountKey"/>. Null with no mount connected.</summary>
    public static string? MountKey(TelescopeInfo? info) {
        if (info?.Connected != true) {
            return null;
        }
        if (!string.IsNullOrWhiteSpace(info.DeviceId)) {
            return info.DeviceId.Trim();
        }
        return string.IsNullOrWhiteSpace(info.Name) ? UnidentifiedMountKey : info.Name.Trim();
    }

    /// <summary>The connected mount's clearance entry, or the defaults (60°, not confirmed) when it has none.</summary>
    public static PolarAlignMountClearanceDto ClearanceFor(PolarAlignSettingsDto settings, string? mountKey) =>
        mountKey is not null && settings.MountClearance is { } entries && entries.TryGetValue(mountKey, out var entry) && entry is not null
            ? entry
            : new PolarAlignMountClearanceDto();

    internal const string MountNotConnectedMessage = "mount is not connected";
    internal const string SiteLatitudeMessage =
        "site latitude must be configured (non-zero) before polar alignment — the routine measures against your site's celestial pole";

    private const string WaitsForMount = "Waits for the mount to connect.";

    private sealed class Row(string id) {
        public string Id { get; } = id;
        public string Status { get; set; } = PolarAlignPreflightStatuses.Unknown;
        public string Detail { get; set; } = "";
        /// <summary>What a start throws on this check (status fail).</summary>
        public Exception? Refusal { get; set; }

        public void Ok(string detail) {
            Status = PolarAlignPreflightStatuses.Ok;
            Detail = detail;
        }

        public void Fail(Exception refusal, string? detail = null) {
            Status = PolarAlignPreflightStatuses.Fail;
            Refusal = refusal;
            Detail = detail ?? refusal.Message;
        }

        public void Unknown(string detail) {
            Status = PolarAlignPreflightStatuses.Unknown;
            Detail = detail;
        }

        public void Warn(string detail) {
            Status = PolarAlignPreflightStatuses.Warn;
            Detail = detail;
        }
    }

    /// <summary>The list for <paramref name="inputs"/>. Never throws; a cancelled <paramref name="ct"/> fails the checks
    /// that read the mount (a start then throws the cancellation, as before).</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The evaluator never throws (#2068): every failed read becomes its check's status, and a start rethrows the read's own exception.")]
    public static async Task<PolarAlignPreflightResult> EvaluateAsync(PolarAlignPreflightInputs inputs, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(inputs);
        var rows = new Dictionary<string, Row>(StringComparer.Ordinal);
        foreach (var id in PolarAlignPreflightChecks.All) {
            rows[id] = new Row(id);
        }
        var stage = inputs.Stage;

        // The settings first: the clearance, the Polar Aligner and the plan read them.
        PolarAlignSettingsDto settings;
        Exception? settingsError = null;
        try {
            settings = inputs.Settings();
        } catch (Exception ex) {
            settingsError = ex;
            settings = PolarAlignSettingsClamp.Apply(new PolarAlignSettingsDto());
        }

        // The mount.
        TelescopeInfo? info = null;
        var connected = false;
        var mountRow = rows[PolarAlignPreflightChecks.MountConnected];
        try {
            info = inputs.MountInfo();
            connected = info?.Connected == true;
            if (connected) {
                mountRow.Ok(string.IsNullOrWhiteSpace(info!.Name) ? "The mount is connected." : $"{info.Name} is connected.");
            } else {
                mountRow.Fail(new InvalidOperationException(MountNotConnectedMessage), "The mount is not connected.");
            }
        } catch (Exception ex) {
            mountRow.Fail(ex, $"The mount's state could not be read: {ex.Message}");
        }
        var mountKey = connected ? MountKey(info) : null;

        var parkedRow = rows[PolarAlignPreflightChecks.MountParked];
        if (!connected) {
            parkedRow.Unknown(WaitsForMount);
        } else if (info!.AtPark) {
            parkedRow.Fail(new RefusalException(RefusalCatalog.PolarAlignMountParked, PolarAlignService.MountParkedMessage),
                "The mount is parked. Polar Align never unparks it: unpark it and point the tube near the pole first.");
        } else {
            parkedRow.Ok("The mount is not parked.");
        }

        // The site.
        var latRow = rows[PolarAlignPreflightChecks.SiteLatitude];
        var lonRow = rows[PolarAlignPreflightChecks.SiteLongitudeLst];
        try {
            var site = inputs.Site();
            if (site.LatitudeDeg == 0 && site.LongitudeDeg == 0) {
                // #2083 — 0°, 0° is a site that was never set: refuse as unset, not as a bad latitude.
                latRow.Fail(new RefusalException(RefusalCatalog.PolarAlignSiteNotSet, SiteNotSetMessage), "Set your site in Settings › Site.");
            } else if (double.IsFinite(site.LatitudeDeg) && Math.Abs(site.LatitudeDeg) > 0 && Math.Abs(site.LatitudeDeg) <= 90) {
                latRow.Ok(string.Create(CultureInfo.InvariantCulture, $"The site's latitude is {site.LatitudeDeg:0.0}°."));
            } else {
                latRow.Fail(new InvalidOperationException(SiteLatitudeMessage), "Set the site's latitude (not 0°) in Settings › Site.");
            }
            EvaluateLongitudeAndLst(lonRow, site, connected ? info : null);
        } catch (Exception ex) {
            latRow.Fail(ex, $"The site could not be read: {ex.Message}");
            lonRow.Fail(ex, $"The site could not be read: {ex.Message}");
        }

        // #2087's mount clock gate: the one SiderealTime check.
        var clockRow = rows[PolarAlignPreflightChecks.MountClockOrSite];
        if (!connected) {
            clockRow.Unknown(WaitsForMount);
        } else if (inputs.ClockRefusal is null) {
            clockRow.Unknown("This daemon does not check the mount's clock.");
        } else {
            try {
                if (inputs.ClockRefusal() is { } refusal) {
                    clockRow.Fail(new RefusalException(RefusalCatalog.MountClockNotVerified, refusal), refusal);
                } else {
                    clockRow.Ok("The mount's clock is set from the synced rig clock and checked.");
                }
            } catch (Exception ex) {
                clockRow.Fail(ex, $"The mount's clock check could not be read: {ex.Message}");
            }
        }

        // #2070's position trust.
        var poseRow = rows[PolarAlignPreflightChecks.PoseVerified];
        if (!connected) {
            poseRow.Unknown(WaitsForMount);
        } else {
            try {
                if (await inputs.PoseUnverifiedReason(ct).ConfigureAwait(false) is { } reason) {
                    poseRow.Fail(TelescopeService.PositionUnverifiedRefusal(reason), TelescopeService.PositionUnverifiedDetail(reason));
                } else {
                    poseRow.Ok("The mount's position is trusted.");
                }
            } catch (Exception ex) {
                poseRow.Fail(ex, $"The mount's position could not be checked: {ex.Message}");
            }
        }

        // ARA-8: no motor motion until the user confirmed this mount's clearance.
        var clearance = ClearanceFor(settings, mountKey);
        var clearanceRow = rows[PolarAlignPreflightChecks.ClearanceConfirmed];
        if (settingsError is not null) {
            clearanceRow.Fail(settingsError, $"The polar-align settings could not be read: {settingsError.Message}");
        } else if (!connected) {
            clearanceRow.Unknown(WaitsForMount);
        } else if (clearance.ClearanceConfirmed) {
            clearanceRow.Ok(string.Create(CultureInfo.InvariantCulture,
                $"You confirmed this mount clears the pier and tripod with the counterweight up to {clearance.MaxCwAngleDeg:0}° from down."));
        } else {
            var limit = clearance.MaxCwAngleDeg;
            clearanceRow.Fail(new RefusalException(RefusalCatalog.PolarAlignClearanceNotConfirmed,
                    string.Create(CultureInfo.InvariantCulture,
                        $"Confirm this mount's clearance first: check with the hand controller that the counterweight, tube and cables clear the pier and tripod up to {limit:0}° from counterweight-down, then confirm it in Polar Align"),
                    new Dictionary<string, object?> { ["max_cw_angle_deg"] = limit }),
                "Not confirmed for this mount: check its counterweight and cable clearance, then confirm it. Polar Align does not move the mount until then.");
        }

        // The start pointing: the confirm step's plan, from a fresh reading.
        PolarAlignStartPlanDto? plan = null;
        var pointingRow = rows[PolarAlignPreflightChecks.StartPointing];
        if (!connected) {
            pointingRow.Unknown(WaitsForMount);
        } else {
            try {
                plan = await inputs.Plan(ct).ConfigureAwait(false);
                pointingRow.Ok(string.Create(CultureInfo.InvariantCulture,
                    $"RA turns {plan.StepDeg:0}° twice toward the {plan.Direction}, at most {plan.MaxSweepDeg:0}° from where the mount points."));
            } catch (Exception ex) {
                pointingRow.Fail(ex);
            }
        }

        // #2083 — the mount type and CanMoveAxis(Primary), read at connect.
        EvaluateMountType(rows[PolarAlignPreflightChecks.MountType], inputs.AlignmentMode, connected);
        EvaluateCanMoveAxis(rows[PolarAlignPreflightChecks.CanMoveAxis], inputs.CanMovePrimaryAxis, connected);

        var alignerRow = rows[PolarAlignPreflightChecks.Aligner];
        try {
            if (inputs.AlignerPresent?.Invoke() != true) {
                alignerRow.Ok("No Polar Aligner: you turn the altitude and azimuth bolts.");
            } else if (!settings.PolarAlignerAuto) {
                alignerRow.Ok("The Polar Aligner's motors are off: you turn the bolts.");
            } else {
                alignerRow.Ok("The Polar Aligner's Stop is checked and its turns are capped.");
            }
        } catch (Exception ex) {
            alignerRow.Unknown($"The Polar Aligner could not be read: {ex.Message}");
        }

        rows[PolarAlignPreflightChecks.MountLimits].Warn(
            "Set the mount's own slew and altitude limits in its hand controller or app: Ara's checks do not replace them.");

        var checks = new List<PolarAlignPreflightCheckDto>(rows.Count);
        Exception? firstFailure = null;
        var ready = true;
        foreach (var id in PolarAlignPreflightChecks.All) {
            var row = rows[id];
            checks.Add(new PolarAlignPreflightCheckDto(row.Id, row.Status, row.Detail));
            if (row.Status == PolarAlignPreflightStatuses.Fail) {
                firstFailure ??= row.Refusal;
                ready = false;
            }
        }
        var turnStep = settings.SeedRotationDeg;
        var stageStep = stage == PolarAlignStages.Check ? settings.CheckStepDeg : turnStep;
        var planned = new PolarAlignPreflightPlannedDto(
            Stage: stage,
            Direction: plan?.Direction,
            TurnStepDeg: turnStep,
            CheckStepDeg: settings.CheckStepDeg,
            Turns: plan?.Turns ?? 2,
            MaxSweepDeg: plan?.MaxSweepDeg ?? 2.0 * stageStep,
            NewOrigin: plan?.NewOrigin,
            OriginOffsetDeg: plan?.OriginOffsetDeg,
            SweepLimitDeg: plan?.SweepLimitDeg,
            MaxCwAngleDeg: clearance.MaxCwAngleDeg,
            StartCwAngleDeg: null,
            MinAltitudeDeg: null);
        var dto = new PolarAlignPreflightDto(ready, checks, planned, mountKey);
        return new PolarAlignPreflightResult(dto, firstFailure, plan);
    }

    internal const string SiteNotSetMessage =
        "set the site's latitude and longitude before polar alignment — the routine steers by the hour angle, which needs the site's longitude and the local sidereal time";

    /// <summary>#2083 — a usable longitude, not the unset site at 0°, 0°, and (with a mount) a finite local sidereal
    /// time, which the daemon works out from the site's longitude (<see cref="TelescopeInfo.SiderealTime"/>).</summary>
    private static void EvaluateLongitudeAndLst(Row row, SiteSettingsDto site, TelescopeInfo? connectedMount) {
        RefusalException NotSet() => new(RefusalCatalog.PolarAlignSiteNotSet, SiteNotSetMessage);
        if (!double.IsFinite(site.LongitudeDeg) || Math.Abs(site.LongitudeDeg) > 180) {
            row.Fail(NotSet(), "Set the site's longitude in Settings › Site.");
        } else if (site.LatitudeDeg == 0 && site.LongitudeDeg == 0) {
            row.Fail(NotSet(), "The site is at latitude 0° and longitude 0°, a site that was never set: set your site in Settings › Site.");
        } else if (connectedMount is null) {
            row.Unknown(string.Create(CultureInfo.InvariantCulture,
                $"The site's longitude is {site.LongitudeDeg:0.0}°. The sidereal time is worked out once the mount connects."));
        } else if (!double.IsFinite(connectedMount.SiderealTime)) {
            row.Fail(NotSet(), "The local sidereal time could not be worked out from the site's longitude: check the site in Settings › Site.");
        } else {
            row.Ok(string.Create(CultureInfo.InvariantCulture,
                $"The site's longitude is {site.LongitudeDeg:0.0}°; the local sidereal time is {connectedMount.SiderealTime:0.00} h."));
        }
    }

    /// <summary>#2083 — an alt-az mount is refused: MoveAxis(Primary) would turn azimuth, not RA. An unknown type is not.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The evaluator never throws (#2068): a failed read lists the check as unknown.")]
    private static void EvaluateMountType(Row row, Func<AlignmentMode?>? read, bool connected) {
        if (!connected) {
            row.Unknown(WaitsForMount);
            return;
        }
        if (read is null) {
            row.Unknown("This daemon does not read the mount type.");
            return;
        }
        AlignmentMode? mode;
        try {
            mode = read();
        } catch (Exception ex) {
            row.Unknown($"The mount type could not be read: {ex.Message}");
            return;
        }
        switch (mode) {
            case AlignmentMode.AltAz:
                row.Fail(new RefusalException(RefusalCatalog.PolarAlignAltAzMount,
                        "polar alignment needs an equatorial mount — this mount reports alt-az, so turning its first axis would turn azimuth, not RA"),
                    "The mount reports alt-az: its first axis turns azimuth, not RA. Polar Align needs an equatorial mount.");
                break;
            case AlignmentMode.GermanPolar:
                row.Ok("A German equatorial mount.");
                break;
            case AlignmentMode.Polar:
                row.Ok("An equatorial (fork or polar) mount.");
                break;
            default:
                row.Unknown("The mount does not report its type.");
                break;
        }
    }

    /// <summary>#2083 — CanMoveAxis(Primary) false refuses the start: Polar Align turns RA only with the mount's motor
    /// (#2170). An unknown answer does not refuse.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The evaluator never throws (#2068): a failed read lists the check as unknown.")]
    private static void EvaluateCanMoveAxis(Row row, Func<bool?>? read, bool connected) {
        if (!connected) {
            row.Unknown(WaitsForMount);
            return;
        }
        if (read is null) {
            row.Unknown("This daemon does not read whether the mount can turn its RA axis by rate.");
            return;
        }
        bool? can;
        try {
            can = read();
        } catch (Exception ex) {
            row.Unknown($"The mount's CanMoveAxis could not be read: {ex.Message}");
            return;
        }
        if (can is null) {
            row.Unknown("The mount does not say whether it can turn its RA axis by rate.");
        } else if (can.Value) {
            row.Ok("The mount can turn its RA axis by rate.");
        } else {
            row.Fail(new RefusalException(RefusalCatalog.PolarAlignCannotMoveAxis,
                    "this mount cannot turn its RA axis by rate (CanMoveAxis is false), so Polar Align cannot turn it — Polar Align needs a mount whose driver supports MoveAxis"),
                "The mount cannot turn its RA axis by rate (CanMoveAxis is false). Polar Align needs a mount whose driver supports MoveAxis.");
        }
    }
}
