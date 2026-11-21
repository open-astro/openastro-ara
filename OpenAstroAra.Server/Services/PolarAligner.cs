#region "copyright"
/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors
    This file is part of OpenAstro Ara (forked from N.I.N.A.).
    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/
#endregion "copyright"

using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
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

    /// <summary>#2069 (ARA-19) — the last verified Stop of the Polar Aligner connected now was not confirmed: whoever
    /// sent it (the routine, the Stop route, Emergency Stop, a park), the next one to drive the device sends Stop
    /// again first, and moves nothing until a Stop is confirmed. False with no Polar Aligner connected: nothing can
    /// be stopped or moved then, and the Stop stays owed by its device if it comes back.</summary>
    bool StopOwed => false;

    /// <summary>Records the outcome of a verified Stop (<see cref="PolarAlignerVerifiedStop"/>) under the device it was
    /// sent to: <paramref name="device"/> is the object <see cref="Current"/> handed out for it, which may be older than
    /// the latest one handed out.</summary>
    void NoteStopOutcome(IPolarAligner device, bool confirmed) { }
}

/// <summary>What one verified Stop found: whether the device reported both axes still, how many Stops went out,
/// and why not, from the latest call: a failed call's text, or <see cref="PolarAlignerVerifiedStop.StillMoving"/>.
/// <see cref="Abandoned"/> counts the device calls still waiting at the window's end that the stop left running.</summary>
internal readonly record struct PolarAlignerStopOutcome(bool Confirmed, int Sends, string? Error, int Abandoned = 0);

/// <summary>
/// #2069 (ARA-19) — every Stop of the Polar Aligner is a verified stop, as the mount's is (#2048). Stop goes out,
/// then Status is polled until neither axis reports Moving, for up to <see cref="DefaultWindow"/>. While an axis
/// still moves (or the device does not answer) Stop is sent again, evenly over the window, <see cref="Sends"/>
/// times in all. Only a still read after a Stop the device answered (accepted, or refused with its own error)
/// confirms it: a read that fails never counts as still, and nor does a still read while no Stop got through (a
/// move sent before a link drop may yet start).
/// Not confirmed at the end of the window: the caller
/// reports <c>stop_not_confirmed</c> and owes the device a Stop.
/// The window bounds the whole stop, a slow link included: no call starts after it, and a call still waiting at its end
/// is left to finish on its own (a Stop that lands late is harmless) and counts as no answer, so Emergency Stop's aligner
/// step (the routine's stop, then the route's: two windows) stays well inside its 10 s bound.
/// </summary>
internal static partial class PolarAlignerVerifiedStop {
    internal static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan DefaultPoll = TimeSpan.FromMilliseconds(250);
    internal const int Sends = 3;
    private static readonly TimeSpan MinCallTime = TimeSpan.FromMilliseconds(1);

    private const string NoAnswerInWindow = "the Polar Aligner did not answer within the stop's window";
    /// <summary>The outcome's <c>Error</c> when the last thing the stop heard was a delivered Stop and a moving read.</summary>
    internal const string StillMoving = "an axis still reports moving";

    /// <summary>The device calls block (HTTP): call this off a caller's thread. Never throws for a device call: any failure
    /// of one is no answer, and a stop without a confirming read is not confirmed (owed).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A driver may throw anything (a socket error, a disposed client, a bug); an exception out of a Stop would skip the caller's own clean-up (the run's end, Emergency Stop's rung). Any failure is no answer: the stop is not confirmed and stays owed.")]
    public static async Task<PolarAlignerStopOutcome> StopAsync(IPolarAligner device, TimeSpan window, TimeSpan poll,
            ILogger? logger = null) {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var resendEvery = window / Sends;
        var sends = 0;
        var delivered = false;
        var abandoned = 0;
        string? error = null;
        void Abandon() => abandoned++;
        // The window is over once a call used up what was left of it (it was abandoned), or under a millisecond is left.
        // A wait's timer counts whole milliseconds and may end a hair before the window by the stopwatch; a pass started
        // in that hair would start a device call with no time to answer, a thread and an abandoned call more.
        bool Over() => abandoned > 0 || window - clock.Elapsed < MinCallTime;
        PolarAlignerStopOutcome Done(bool confirmed) {
            if (abandoned > 0 && logger is not null) {
                LogCallsAbandoned(logger, abandoned, window.TotalSeconds, StillRunning);
            }
            return new PolarAlignerStopOutcome(confirmed, sends, confirmed ? null : error, abandoned);
        }
        while (true) {
            // The first Stop always goes out; a resend only inside the window.
            if (sends == 0 || (sends < Sends && clock.Elapsed >= resendEvery * sends && !Over())) {
                sends++;
                try {
                    await WithinAsync(() => { device.Stop(); return true; }, window - clock.Elapsed, Abandon).ConfigureAwait(false);
                    delivered = true;
                    error = null; // an earlier send's failure is not this outcome's
                } catch (PolarAlignerException ex) {
                    error = ex.Message;
                    // A refusal is an answer: the link works and nothing sent before it is still on the way, so a still
                    // read from here on confirms the stop. A device that does not implement Stop (often one whose
                    // canStop is false) would otherwise owe a Stop for ever, and no later run could move it.
                    delivered |= ex.Failure == PolarAlignerFailure.Refused;
                } catch (TimeoutException) {
                    error = NoAnswerInWindow;
                } catch (Exception ex) {
                    error = ex.Message; // not the device's own refusal: no answer
                }
            }
            if (Over()) {
                return Done(false);
            }
            try {
                if (delivered) {
                    if (!(await WithinAsync(device.Status, window - clock.Elapsed, Abandon).ConfigureAwait(false)).Moving) {
                        return Done(true);
                    }
                    error = StillMoving; // the latest outcome: the device answered, and an axis still moves
                }
            } catch (PolarAlignerException ex) {
                error = ex.Message;
            } catch (TimeoutException) {
                // A read the window's end cut short does not hide what the last read that answered said.
                error ??= NoAnswerInWindow;
            } catch (Exception ex) {
                error = ex.Message;
            }
            if (Over()) {
                return Done(false);
            }
            var left = window - clock.Elapsed;
            await Task.Delay(poll < left ? poll : left).ConfigureAwait(false);
        }
    }

    private static int _stillRunning;

    /// <summary>Device calls that a verified stop left running at its window's end and that have not finished yet, across
    /// every stop: each holds a thread of its own until the call returns (a dead link: its HTTP timeout).</summary>
    internal static int StillRunning => Volatile.Read(ref _stillRunning);

    // One blocking device call, waited on for at most what is left of the window (TimeoutException past it). The call
    // itself cannot be cut short: it finishes on its own thread, and its failure is observed there. A thread of its own,
    // not the pool's: on a starved pool (Emergency Stop's ladder, #1985) a queued call would not even start in the window.
    // A call still running when the wait ends is abandoned: counted (onAbandoned, StillRunning) until it finishes.
    private static async Task<T> WithinAsync<T>(Func<T> call, TimeSpan left, Action onAbandoned) {
        var running = Task.Factory.StartNew(call, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _ = running.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try {
            return await running.WaitAsync(left > TimeSpan.Zero ? left : TimeSpan.Zero).ConfigureAwait(false);
        } catch (TimeoutException) when (!running.IsCompleted) {
            onAbandoned();
            Interlocked.Increment(ref _stillRunning);
            _ = running.ContinueWith(static _ => Interlocked.Decrement(ref _stillRunning), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    [LoggerMessage(EventId = 4644, Level = LogLevel.Warning, Message = "§45 Polar Aligner verified stop: {Abandoned} device call(s) still waiting at the end of its {WindowSeconds:F1} s window left running; {StillRunning} such call(s) in all hold a thread until they return (a dead link: its HTTP timeout)")]
    private static partial void LogCallsAbandoned(ILogger logger, int abandoned, double windowSeconds, int stillRunning);
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
