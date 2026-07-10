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

namespace OpenAstroAra.Server.Services;

/// <summary>What the reaction service does once the retry schedule is exhausted (or was empty).</summary>
public enum FaultTerminalAction {
    /// <summary>Surface the fault (log + WS + notification) and nothing else.</summary>
    None,

    /// <summary>Arm an instruction-boundary pause on every active sequence run.</summary>
    PauseSequence,

    /// <summary>Abort active runs and best-effort park the mount.</summary>
    AbortAndPark,
}

/// <summary>
/// The resolved reaction plan for one fault episode. <see cref="RetrySchedule"/> is the delay
/// BEFORE each recovery attempt (empty = no recovery, straight to the terminal action);
/// <see cref="PauseWhileRecovering"/> pauses any running sequence immediately, before the first
/// attempt, so a sequence-critical device (camera/mount) can't burn frames or slew unguarded
/// while recovery runs — the runs are resumed if recovery succeeds. <see cref="Escalated"/>
/// marks a §42.2 persistent-op-fault escalation: there is nothing to recover (the device is
/// still connected — it keeps failing its ops), so the terminal action runs immediately and the
/// episode reports as an escalation, not a failed recovery.
/// </summary>
public sealed record FaultPlan(
    IReadOnlyList<TimeSpan> RetrySchedule,
    FaultTerminalAction TerminalAction,
    NotificationSeverity GiveUpSeverity,
    bool PauseWhileRecovering,
    bool Escalated = false);

/// <summary>
/// §42.2 — the pure policy table mapping (device type, fault kind, profile policies) to a
/// <see cref="FaultPlan"/>. All decisions live here so they are testable without a daemon:
/// the <see cref="FaultReactionService"/> only executes plans.
/// </summary>
public static class FaultPolicyMatrix {

    /// <summary>The §42.3 hot-reconnect ladder: attempt immediately, then back off.</summary>
    public static readonly IReadOnlyList<TimeSpan> HotReconnectLadder = [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
    ];

    private static readonly TimeSpan[] NoRetries = [];

    /// <summary>
    /// Resolve the plan for a fault. Returns <c>null</c> when the fault is NOT the reaction
    /// service's to handle: the guider owns its own §42.2 reaction + §63.3 recovery
    /// (GuiderService.FaultReaction — its hub publishes are for logging/broadcast only).
    /// <paramref name="persistentOpFault"/> is the <see cref="OpFaultEscalator"/>'s verdict that
    /// this op fault tipped its device over the persistence threshold.
    /// </summary>
    public static FaultPlan? Resolve(DeviceType deviceType, EquipmentFaultKind kind, SafetyPoliciesDto? policies,
            bool persistentOpFault = false) {
        if (deviceType == DeviceType.Guider) {
            return null;
        }
        var hot = policies?.HotReconnectEnabled ?? true;
        switch (kind) {
            case EquipmentFaultKind.Disconnected:
                return deviceType switch {
                    DeviceType.Camera => FromPolicy(policies?.OnCameraLost, "reconnect_then_pause",
                        hot, NotificationSeverity.Error, pauseWhileRecovering: true),
                    DeviceType.Telescope => FromPolicy(policies?.OnMountLost, "reconnect_then_abort_park",
                        hot, NotificationSeverity.Error, pauseWhileRecovering: true),
                    // Peripheral devices (focuser/FW/rotator/dome/switch/flat/safety/weather):
                    // reconnect quietly; a device that stays gone surfaces through the
                    // instruction that needs it (§42.4 sequence.instruction_failed), not by
                    // halting the whole night here.
                    _ => new FaultPlan(hot ? HotReconnectLadder : NoRetries,
                        FaultTerminalAction.None, NotificationSeverity.Warning, PauseWhileRecovering: false),
                };
            case EquipmentFaultKind.TrackingLost:
                // Recovery attempt = re-enable tracking (the mount is still connected).
                return FromPolicy(policies?.OnTrackingLost, "reenable_then_pause",
                    hot, NotificationSeverity.Error, pauseWhileRecovering: true);
            default:
                // §42.2 escalation rows — PERSISTENT op failures stop being instruction-level
                // news: a mount that keeps failing slews may be against an obstruction
                // ("Retry → Abort + park", physical safety), a camera that keeps failing
                // captures is burning sky time for nothing ("Pause if persistent"). No retry
                // schedule: the device is still connected, there is nothing to reconnect.
                if (persistentOpFault) {
                    switch (deviceType) {
                        case DeviceType.Telescope:
                            return new FaultPlan(NoRetries, FaultTerminalAction.AbortAndPark,
                                NotificationSeverity.Error, PauseWhileRecovering: false, Escalated: true);
                        case DeviceType.Camera:
                            return new FaultPlan(NoRetries, FaultTerminalAction.PauseSequence,
                                NotificationSeverity.Error, PauseWhileRecovering: false, Escalated: true);
                        default:
                            break; // peripherals stay notify-only however often they fail —
                                   // their matrix rows all end in "Notify", and §42.4
                                   // instruction_failed already surfaces each failure
                    }
                }
                // Op/state-channel faults (§42.4 stall_timeout/op_error/value_mismatch/
                // cooling_drift): the device is still connected — nothing to reconnect;
                // surface the fault and let sequence-level handling own the consequence.
                return new FaultPlan(NoRetries, FaultTerminalAction.None,
                    NotificationSeverity.Warning, PauseWhileRecovering: false);
        }
    }

    private static FaultPlan FromPolicy(string? policy, string defaultPolicy, bool hotReconnectEnabled,
            NotificationSeverity giveUpSeverity, bool pauseWhileRecovering) {
        // An unknown token (a newer client wrote a value this daemon doesn't know) falls back
        // to the device's default policy rather than silently doing nothing.
        var parsed = ParseToken(policy) ?? ParseToken(defaultPolicy)!.Value;
        var schedule = parsed.Retry && hotReconnectEnabled ? HotReconnectLadder : NoRetries;
        return new FaultPlan(schedule, parsed.Terminal, giveUpSeverity,
            pauseWhileRecovering && parsed.Terminal != FaultTerminalAction.None);
    }

    private static (bool Retry, FaultTerminalAction Terminal)? ParseToken(string? token) =>
        token?.Trim() switch {
            "reconnect_then_pause" or "reenable_then_pause" => (true, FaultTerminalAction.PauseSequence),
            "reconnect_then_abort_park" or "reenable_then_abort_park" => (true, FaultTerminalAction.AbortAndPark),
            "pause" => (false, FaultTerminalAction.PauseSequence),
            "abort_park" => (false, FaultTerminalAction.AbortAndPark),
            "notify_only" => (false, FaultTerminalAction.None),
            _ => null,
        };
}
