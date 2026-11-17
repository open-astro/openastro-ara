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
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>#1971 — the two axes a Polar Aligner moves.</summary>
internal enum PolarAlignerAxis { Azimuth, Altitude }

/// <summary>What one axis can do: the largest single move the device accepts and its step size
/// (0 while the device has no calibration), both in arcminutes, and whether the device takes up its
/// own backlash on a reversal.</summary>
internal sealed record PolarAlignerAxisInfo(double MaxMoveArcmin, double ResolutionArcmin, bool BacklashHandled = false);

/// <summary>A Polar Aligner's capabilities, as its <c>Info</c> reply states them. An axis the
/// device does not describe is null. <see cref="HostDriven"/> is the one question the Polar Align
/// Routine asks: can Ara send the moves itself (tier 1, both axes)?</summary>
internal sealed record PolarAlignerInfo(
    IReadOnlyList<int> Tiers,
    string DeviceName,
    string DeviceVersion,
    PolarAlignerAxisInfo? Azimuth,
    PolarAlignerAxisInfo? Altitude,
    bool SimultaneousMoves,
    bool MovesWhileTracking,
    bool MovesWhileSlewing,
    bool CanStop,
    double SettleSeconds) {
    public bool HostDriven => Tiers.Contains(1) && Azimuth is not null && Altitude is not null;
}

/// <summary>One axis right now. <c>PositionArcmin</c> and <c>Fault</c> are absent (null), never
/// zero or empty, when the device does not report them. A fault is the device's own text for a
/// move that failed; the device clears it on the next move, stop or home.</summary>
internal sealed record PolarAlignerAxisStatus(bool Moving, double? PositionArcmin, string? Fault);

internal sealed record PolarAlignerStatus(PolarAlignerAxisStatus Azimuth, PolarAlignerAxisStatus Altitude) {
    public bool Moving => Azimuth.Moving || Altitude.Moving;
}

/// <summary>Why a call to the Polar Aligner did not work.</summary>
internal enum PolarAlignerFailure {
    /// <summary>The device refused a move because it has no calibration yet.</summary>
    NotCalibrated,
    /// <summary>The device answered with any other error.</summary>
    Refused,
    /// <summary>The device did not answer, or is not connected.</summary>
    LinkLost,
    /// <summary>The device answered with something that is not the PolarAlign JSON.</summary>
    Protocol,
}

internal sealed class PolarAlignerException : Exception {
    public PolarAlignerException() { }
    public PolarAlignerException(string message) : base(message) { }
    public PolarAlignerException(string message, Exception innerException) : base(message, innerException) { }
    public PolarAlignerException(PolarAlignerFailure failure, string message, Exception? innerException = null)
        : base(message, innerException) {
        Failure = failure;
    }
    public PolarAlignerFailure Failure { get; } = PolarAlignerFailure.Refused;
}

/// <summary>
/// #1971 — a Polar Aligner: a motorised device that moves the mount's polar axis in azimuth
/// and altitude. Five calls, and every one throws <see cref="PolarAlignerException"/> when it
/// does not work. Moves are signed arcminutes in the wire convention of
/// <see cref="PolarAlignGeometry.AlignerMove"/>: positive azimuth increases the azimuth
/// coordinate, positive altitude raises the axis. <see cref="Move"/> returns once the device has
/// accepted the move, not when it has finished: poll <see cref="Status"/>.
/// </summary>
internal interface IPolarAligner {
    PolarAlignerInfo Info();
    PolarAlignerStatus Status();
    void Move(PolarAlignerAxis axis, double arcmin);
    /// <summary>Stops both axes.</summary>
    void Stop();
    void Home();
}

/// <summary>The Alpaca driver actions of one connected device: what a Switch or the Telescope
/// lends to a Polar Aligner adapter. <see cref="Action"/> throws when the device is not
/// connected or the call fails.</summary>
internal interface IDriverActions {
    IReadOnlyList<string> SupportedActions { get; }
    string Action(string name, string parameters);
}

/// <summary>What is driving the Polar Aligner right now, if anything: the Polar Align Routine. A stop
/// that comes from outside it (the emergency stop, the device's Stop route) must end its motor loop
/// first, or the loop reads the stopped move as a finished one and moves the motors again.</summary>
internal interface IPolarAlignerDriver {
    bool DrivesPolarAligner { get; }
    Task StopPolarAlignerAsync();
}

/// <summary>Where the Polar Align Routine gets the rig's Polar Aligner: the connected device that
/// offers the actions (a Switch before the mount; no setting names it). Null when there is none.</summary>
internal interface IPolarAlignerSource {
    IPolarAligner? Current();
}

/// <summary>
/// #1971 — the Polar Aligner over the interim <c>PolarAlign:*</c> Alpaca actions (OpenAstroTech
/// draft v0.1, with the <c>fault</c> string proposed for v0.2). The only code that knows the
/// action names and their JSON; when the ASCOM aligner interface is final, a second
/// <see cref="IPolarAligner"/> sits beside this one. Unknown fields are ignored and an absent
/// optional field stays absent.
/// </summary>
internal sealed class PolarAlignActionsAligner(IDriverActions carrier) : IPolarAligner {
    private const string InfoAction = "PolarAlign:Info";
    private const string StatusAction = "PolarAlign:Status";
    private const string MoveAction = "PolarAlign:Move";
    private const string StopAction = "PolarAlign:Stop";
    private const string HomeAction = "PolarAlign:Home";

    /// <summary>A device is a Polar Aligner when its driver lists the Info action (any letter
    /// case). Nothing else is read: not its name, description or driver string.</summary>
    public static bool IsOfferedBy(IEnumerable<string>? supportedActions) =>
        supportedActions is not null
        && supportedActions.Any(a => string.Equals(a, InfoAction, StringComparison.OrdinalIgnoreCase));

    public PolarAlignerInfo Info() => ParseInfo(Call(InfoAction, string.Empty));

    public PolarAlignerStatus Status() => ParseStatus(Call(StatusAction, string.Empty));

    public void Move(PolarAlignerAxis axis, double arcmin) =>
        Call(MoveAction, new JsonObject {
            ["axis"] = axis == PolarAlignerAxis.Azimuth ? "az" : "alt",
            ["arcmin"] = arcmin,
        }.ToJsonString());

    public void Stop() => Call(StopAction, string.Empty);

    public void Home() => Call(HomeAction, string.Empty);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Anything that is not a driver's own error (a socket error, a timeout, a device that is no longer connected) is a lost link.")]
    private string Call(string action, string parameters) {
        try {
            return carrier.Action(action, parameters);
        } catch (ASCOM.NotConnectedException ex) {
            throw new PolarAlignerException(PolarAlignerFailure.LinkLost, ex.Message, ex);
        } catch (ASCOM.InvalidOperationException ex) {
            // The one documented InvalidOperation of a move: the device has no calibration factors.
            throw new PolarAlignerException(
                action == MoveAction ? PolarAlignerFailure.NotCalibrated : PolarAlignerFailure.Refused, ex.Message, ex);
        } catch (ASCOM.DriverException ex) {
            throw new PolarAlignerException(PolarAlignerFailure.Refused, ex.Message, ex);
        } catch (Exception ex) {
            throw new PolarAlignerException(PolarAlignerFailure.LinkLost, ex.Message, ex);
        }
    }

    internal static PolarAlignerInfo ParseInfo(string json) {
        using var doc = Parse(json, InfoAction);
        var root = doc.RootElement;
        var tiers = root.TryGetProperty("tiers", out var t) && t.ValueKind == JsonValueKind.Array
            ? t.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out _))
                .Select(e => e.GetInt32()).ToArray()
            : [];
        var axes = root.TryGetProperty("axes", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
        return new PolarAlignerInfo(
            tiers,
            Text(root, "deviceName") ?? string.Empty,
            Text(root, "deviceVersion") ?? string.Empty,
            AxisInfo(axes, "az"),
            AxisInfo(axes, "alt"),
            Flag(root, "simultaneousMoves") ?? false,
            Flag(root, "movesWhileTracking") ?? true,
            Flag(root, "movesWhileSlewing") ?? false,
            Flag(root, "canStop") ?? false,
            Math.Max(0, Number(root, "settleSeconds") ?? 0));
    }

    internal static PolarAlignerStatus ParseStatus(string json) {
        using var doc = Parse(json, StatusAction);
        return new PolarAlignerStatus(AxisStatus(doc.RootElement, "az"), AxisStatus(doc.RootElement, "alt"));
    }

    private static PolarAlignerAxisInfo? AxisInfo(JsonElement axes, string name) {
        if (axes.ValueKind != JsonValueKind.Object || !axes.TryGetProperty(name, out var axis)
                || axis.ValueKind != JsonValueKind.Object || Number(axis, "maxMove") is not { } maxMove || maxMove <= 0) {
            return null;
        }
        return new PolarAlignerAxisInfo(maxMove, Math.Max(0, Number(axis, "resolution") ?? 0), Flag(axis, "backlashHandled") ?? false);
    }

    private static PolarAlignerAxisStatus AxisStatus(JsonElement root, string name) {
        // "Not said" must never read as "not moving": a move in flight would be taken for a finished one.
        if (!root.TryGetProperty(name, out var axis) || axis.ValueKind != JsonValueKind.Object
                || Flag(axis, "moving") is not { } moving) {
            throw new PolarAlignerException(PolarAlignerFailure.Protocol,
                $"the device's {StatusAction} reply does not say whether the {name} axis is moving");
        }
        var fault = Text(axis, "fault");
        return new PolarAlignerAxisStatus(moving, Number(axis, "position"), string.IsNullOrWhiteSpace(fault) ? null : fault);
    }

    private static JsonDocument Parse(string json, string action) {
        try {
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object) {
                return doc;
            }
            doc.Dispose();
        } catch (JsonException) {
            // Reported below, with the action's name.
        } catch (ArgumentNullException) {
            // A null reply is no JSON either.
        }
        throw new PolarAlignerException(PolarAlignerFailure.Protocol, $"the device's {action} reply is not a JSON object");
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)
            && double.IsFinite(d) ? d : null;

    private static bool? Flag(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
}
