#region "copyright"
/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors
    This file is part of OpenAstro Ara (forked from N.I.N.A.).
    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/
#endregion "copyright"

using System;
using System.Linq;
using System.Text.Json;
using ASCOM.Alpaca.Clients;
using OpenAstroAra.Server.Contracts;

namespace OpenAstroAra.Server.Services;

/// <summary>Where the connected mount's spec sheet (if any) can be read from: the telescope service
/// keeps the last capabilities read, the tracking probe asks for the sheet to seed its prior.</summary>
public interface IMountSpecSheetSource {
    MountSpecSheetDto? CurrentMountSpecs { get; }
}

/// <summary>
/// #1264 stage 1 — the mount's spec sheet as AlpacaBridge #815 publishes it: a custom Alpaca action
/// <c>openastro:specs</c> on the telescope device returning one JSON block (schema
/// <c>openastro.mount-specs/1</c>). Read once per connect alongside the other capabilities; a driver
/// without the action, or a sheet that does not parse, simply yields no sheet (never a guess).
/// The prior maths lives here too so the daemon and its tests share one formula (the issue's):
/// <code>
/// tolerance  = max(1 px × pixel scale, 0.5 × seeing FWHM)         [arcsec]
/// pe_rate    = 2π · (periodic_error_pp / 2) / worm_period          [arcsec/s; 0 with encoders]
/// pa_rate    = 15.04 · sin(polar-align error)                       [worst case cos(dec) = 1]
/// t_tracking = tolerance / (pe_rate + pa_rate), floored at 5 s
/// </code>
/// </summary>
public static class MountSpecSheet {
    /// <summary>The Alpaca action name the bridge lists in <c>SupportedActions</c>.</summary>
    public const string ActionName = "openastro:specs";

    /// <summary>The shortest prior ever suggested (the ladder's first rung is 10 s).</summary>
    public const double MinPriorSeconds = 5;

    /// <summary>Read the sheet from a connected telescope, or null when the driver does not list the
    /// action, the call fails, or the JSON does not parse. Never throws.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A driver may NotImplement SupportedActions/Action or return junk; no sheet is the correct answer.")]
    public static MountSpecSheetDto? TryRead(AlpacaTelescope telescope) {
        ArgumentNullException.ThrowIfNull(telescope);
        try {
            var actions = telescope.SupportedActions;
            if (actions is null || !actions.Any(a => string.Equals(a, ActionName, StringComparison.OrdinalIgnoreCase))) {
                return null;
            }
            var json = telescope.Action(ActionName, string.Empty);
            return Parse(json);
        } catch (Exception) {
            return null;
        }
    }

    /// <summary>Parse the bridge's JSON (tolerant: an unknown field is null, never a guess). Null when
    /// the text is not an object or carries neither of the two figures Ara needs and no model name.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Malformed JSON from a third-party driver is 'no sheet', not a daemon fault.")]
    public static MountSpecSheetDto? Parse(string? json) {
        if (string.IsNullOrWhiteSpace(json)) {
            return null;
        }
        try {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) {
                return null;
            }
            var sheet = new MountSpecSheetDto(
                DriverId: Str(root, "driver_id"),
                Model: Str(root, "model"),
                Source: Str(root, "source"),
                WormPeriodS: Num(root, "worm_period_s"),
                PeriodicErrorPpArcsec: Num(root, "periodic_error_pp_arcsec"),
                HasEncoders: Bool(root, "has_encoders"),
                HasPec: Bool(root, "has_pec"),
                MountType: Str(root, "mount_type"),
                MaxPayloadKg: Num(root, "max_payload_kg"),
                Notes: Str(root, "notes"));
            return sheet.Model is null && sheet.WormPeriodS is null && sheet.PeriodicErrorPpArcsec is null ? null : sheet;
        } catch (Exception) {
            return null;
        }
    }

    /// <summary>The unguided exposure prior the sheet implies for THIS rig, in seconds, or null when the
    /// sheet has no usable tracking figures (no worm period or periodic error, and no polar-align error
    /// either) or no pixel scale/seeing is known to set the tolerance. Pure.</summary>
    public static double? UnguidedPriorSeconds(MountSpecSheetDto? sheet, double? pixelScaleArcsec, double? seeingFwhmArcsec, double? polarAlignErrorArcmin) {
        var tolerance = Tolerance(pixelScaleArcsec, seeingFwhmArcsec);
        if (tolerance is not double tol || tol <= 0) {
            return null;
        }
        var rate = 0.0;
        var anyTerm = false;
        if (sheet is { HasEncoders: false, WormPeriodS: double worm, PeriodicErrorPpArcsec: double pp } && worm > 0 && pp > 0) {
            rate += 2 * Math.PI * (pp / 2) / worm;
            anyTerm = true;
        }
        if (polarAlignErrorArcmin is double pa && pa > 0) {
            rate += 15.04 * Math.Sin(pa / 60 * Math.PI / 180);
            anyTerm = true;
        }
        if (!anyTerm || rate <= 0) {
            return null;
        }
        return Math.Max(MinPriorSeconds, tol / rate);
    }

    /// <summary>The trailing tolerance in arcseconds: a pixel, or half the seeing disc, whichever is larger.</summary>
    public static double? Tolerance(double? pixelScaleArcsec, double? seeingFwhmArcsec) {
        var px = pixelScaleArcsec is double s && s > 0 ? s : (double?)null;
        var see = seeingFwhmArcsec is double f && f > 0 ? 0.5 * f : (double?)null;
        if (px is null && see is null) {
            return null;
        }
        return Math.Max(px ?? 0, see ?? 0);
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;

    private static bool Bool(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
