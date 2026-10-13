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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenAstroAra.Astrometry;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §58.14 — the pre-sleep self-test behind <c>GET /api/v1/preflight/today</c>. Every fact comes from
/// a reader the daemon already has (device services, the profile store, the fault log, the sequencer,
/// the client session, the notification preferences); this class only gathers and grades them. The
/// grading is in pure static evaluators so each rule is unit-tested without a rig. Any reader that is
/// missing (a slim test host) or throws yields an <c>unknown</c> item, never a silent "ok": the user is
/// about to go to bed on this answer.
/// </summary>
public sealed class PreflightChecklistService {
    public const string Ok = "ok";
    public const string Warn = "warn";
    public const string Fail = "fail";
    public const string Unknown = "unknown";

    /// <summary>The cooler counts as "at target" within this many degrees of the setpoint.</summary>
    public const double CoolerBandC = 1.0;

    /// <summary>A meridian crossing further ahead than this is tomorrow's problem, not tonight's.</summary>
    public static readonly TimeSpan ForecastHorizon = TimeSpan.FromHours(14);

    private readonly IProfileStore _profiles;
    private readonly ICameraService? _camera;
    private readonly ITelescopeService? _telescope;
    private readonly IFocuserService? _focuser;
    private readonly IFilterWheelService? _filterWheel;
    private readonly IGuiderService? _guider;
    private readonly IFaultLogService? _faults;
    private readonly ISequenceService? _sequences;
    private readonly ISequencerService? _sequencer;
    private readonly ClientSessionService? _sessions;
    private readonly INotificationService? _notifications;
    private readonly OpenAstroAra.Profile.Interfaces.IProfileService? _profileService;
    private readonly Func<string, bool> _fileExists;

    public PreflightChecklistService(
            IProfileStore profiles,
            ICameraService? camera = null,
            ITelescopeService? telescope = null,
            IFocuserService? focuser = null,
            IFilterWheelService? filterWheel = null,
            IGuiderService? guider = null,
            IFaultLogService? faults = null,
            ISequenceService? sequences = null,
            ISequencerService? sequencer = null,
            ClientSessionService? sessions = null,
            INotificationService? notifications = null,
            OpenAstroAra.Profile.Interfaces.IProfileService? profileService = null,
            Func<string, bool>? fileExists = null) {
        _profiles = profiles;
        _camera = camera;
        _telescope = telescope;
        _focuser = focuser;
        _filterWheel = filterWheel;
        _guider = guider;
        _faults = faults;
        _sequences = sequences;
        _sequencer = sequencer;
        _sessions = sessions;
        _notifications = notifications;
        _profileService = profileService;
        _fileExists = fileExists ?? File.Exists;
    }

    /// <summary>Injectable clock (tests pin the instant for the flip forecast).</summary>
    internal Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;

    public async Task<PreflightChecklistDto> BuildAsync(CancellationToken ct) {
        var now = UtcNow();
        var site = _profiles.GetSiteSettings();
        var safety = _profiles.GetSafetyPolicies();
        var storage = _profiles.GetStorageSettings();

        var camera = await ReadAsync<CameraDto>(_camera is null ? null : () => _camera.GetAsync(ct)).ConfigureAwait(false);
        var telescope = await ReadAsync<TelescopeDto>(_telescope is null ? null : () => _telescope.GetAsync(ct)).ConfigureAwait(false);
        var focuser = await ReadAsync<FocuserDto>(_focuser is null ? null : () => _focuser.GetAsync(ct)).ConfigureAwait(false);
        var filterWheel = await ReadAsync<FilterWheelDto>(_filterWheel is null ? null : () => _filterWheel.GetAsync(ct)).ConfigureAwait(false);
        var guider = await ReadAsync<GuiderDto>(_guider is null ? null : () => _guider.GetAsync(ct)).ConfigureAwait(false);
        var unresolvedFaults = await CountUnresolvedFaultsAsync(ct).ConfigureAwait(false);

        var items = new List<PreflightItemDto> {
            EquipmentItem(camera.Value, telescope.Value, focuser.Value, filterWheel.Value, guider.Value,
                readFailed: camera.Failed || telescope.Failed, unresolvedFaults: unresolvedFaults),
            StorageItem(ReadStorage(), storage.MinFreeDiskWarnGb, storage.MinFreeDiskCriticalGb),
            SolverItem(PlateSolveEndpoints.BuildDatabaseStatus(_profiles.GetPlateSolveSettings(), _fileExists)),
            CoolerItem(camera.Value, camera.Failed),
            MountItem(telescope.Value, telescope.Failed),
        };

        var running = await RunningSequencesAsync(ct).ConfigureAwait(false);
        items.Add(SequenceItem(running.Select(r => r.Name).ToList(), running.Failed));

        var flips = new List<PreflightFlipDto>();
        var unforecast = 0;
        var minutesAfter = MinutesAfterMeridian();
        foreach (var run in running) {
            unforecast += Math.Max(0, run.TargetCount - 1);
            var coords = await ReadAsync<Coordinates>(_sequencer is null ? null : () => _sequencer.GetActiveTargetCoordinatesAsync(run.Id, ct)).ConfigureAwait(false);
            if (coords.Value is { } c
                    && ForecastFlip(run.TargetName ?? run.Name, c, site.LatitudeDeg, site.LongitudeDeg, minutesAfter, now,
                        site.SoftWarningAltitudeDeg, site.DefaultHorizonAltitudeDeg) is { } flip) {
                flips.Add(flip);
            }
        }

        var window = UnattendedWindow(safety);
        var session = ReadQuiet(() => _sessions?.GetSession());
        var prefs = await ReadAsync<NotificationPreferenceDto>(_notifications is null ? null : async () => (NotificationPreferenceDto?)await _notifications.GetPreferencesAsync(ct).ConfigureAwait(false)).ConfigureAwait(false);

        return new PreflightChecklistDto(
            GeneratedUtc: now,
            Items: items,
            Flips: flips,
            UnforecastTargets: unforecast,
            UnattendedWindowStart: window.Start,
            UnattendedWindowEnd: window.End,
            UnattendedWindowSource: window.Source,
            AlarmDevice: session?.Connected == true ? session.Hostname : null,
            AlarmSoundEnabled: prefs.Value?.AlarmSoundEnabled ?? false,
            AllGood: IsAllGood(items, flips));
    }

    // ─── pure evaluators ─────────────────────────────────────────────────────────────────────────

    /// <summary>All good = nothing failed and no forecast flip lands below the hard floor. Warnings
    /// and unknowns are shown but do not veto: they are the user's call at the "Adjust" button.</summary>
    internal static bool IsAllGood(IReadOnlyList<PreflightItemDto> items, IReadOnlyList<PreflightFlipDto> flips) =>
        items.All(i => i.Status != Fail) && flips.All(f => f.Standing != "below_floor");

    /// <summary>Equipment: the mount and the main camera must be selected AND connected; any other
    /// selected device that is not connected is a warning; unresolved faults in the log are a warning.</summary>
    internal static PreflightItemDto EquipmentItem(CameraDto? camera, TelescopeDto? telescope, FocuserDto? focuser,
            FilterWheelDto? filterWheel, GuiderDto? guider, bool readFailed, int? unresolvedFaults) {
        if (readFailed) {
            return new PreflightItemDto("equipment", Unknown, "Equipment", "Could not read the equipment state.");
        }
        var missing = new List<string>();
        if (telescope is null) missing.Add("no mount selected");
        else if (telescope.State != EquipmentConnectionState.Connected) missing.Add($"mount {Describe(telescope.State)}");
        if (camera is null) missing.Add("no camera selected");
        else if (camera.State != EquipmentConnectionState.Connected) missing.Add($"camera {Describe(camera.State)}");
        if (missing.Count > 0) {
            return new PreflightItemDto("equipment", Fail, "Equipment", Capitalise(string.Join("; ", missing)) + ".");
        }
        var optional = new List<string>();
        if (focuser is { State: not EquipmentConnectionState.Connected } f) optional.Add($"focuser {Describe(f.State)}");
        if (filterWheel is { State: not EquipmentConnectionState.Connected, Manual: false } w) optional.Add($"filter wheel {Describe(w.State)}");
        if (guider is { State: not EquipmentConnectionState.Connected } g) optional.Add($"guider {Describe(g.State)}");
        if (unresolvedFaults is > 0) {
            optional.Add(unresolvedFaults == 1 ? "1 unresolved fault in the log" : $"{unresolvedFaults} unresolved faults in the log");
        }
        var connected = 2 + (focuser is null ? 0 : 1) + (filterWheel is null ? 0 : 1) + (guider is null ? 0 : 1)
            - optional.Count(o => !o.EndsWith("in the log", StringComparison.Ordinal));
        return optional.Count > 0
            ? new PreflightItemDto("equipment", Warn, "Equipment", Capitalise(string.Join("; ", optional)) + ".")
            : new PreflightItemDto("equipment", Ok, "Equipment", $"All {connected} devices connected and healthy.");
    }

    /// <summary>Storage: graded by the profile's own warn/critical thresholds (the §29 disk monitor's rule).</summary>
    internal static PreflightItemDto StorageItem(StorageSpaceDto space, int warnGb, int criticalGb) {
        if (space.FreeBytes is not long free) {
            return new PreflightItemDto("storage", Unknown, "Storage", $"Could not read the free space under {space.SaveDirectory}.");
        }
        var (low, critical) = DiskSpaceMonitor.ResolveThresholdBytes(warnGb, criticalGb);
        var level = DiskSpaceMonitor.Evaluate(free, low, critical);
        var text = $"{FormatGb(free)} free" + (space.IsFallback ? " (default save directory)" : "");
        return level switch {
            DiskSpaceLevel.Critical => new PreflightItemDto("storage", Fail, "Storage", $"{text}: below the {FormatGb(critical)} critical threshold."),
            DiskSpaceLevel.Low => new PreflightItemDto("storage", Warn, "Storage", $"{text}: below the {FormatGb(low)} warning threshold."),
            _ => new PreflightItemDto("storage", Ok, "Storage", text + "."),
        };
    }

    /// <summary>Plate solver: the binary must exist and a star database must be where ASTAP will look.
    /// This is a presence check, not a solve: the detail says so (an honest card beats a green lie).</summary>
    internal static PreflightItemDto SolverItem(PlateSolveDatabaseStatusDto status) {
        if (!status.SolverFound) {
            return new PreflightItemDto("plate_solver", Fail, "Plate solver",
                status.SolverPath.Length == 0 ? "No solver configured." : $"Solver binary not found at {status.SolverPath}.");
        }
        if (status.EffectivePath is null || status.FileCount == 0) {
            return new PreflightItemDto("plate_solver", Fail, "Plate solver", "Solver found, but no star database in its index directory.");
        }
        var dbs = status.Databases.Count > 0 ? string.Join(", ", status.Databases) : $"{status.FileCount} files";
        return new PreflightItemDto("plate_solver", Ok, "Plate solver", $"Solver and star database ({dbs}) present; not test-solved.");
    }

    /// <summary>Cooler: at target within <see cref="CoolerBandC"/>; a camera without a cooler is ok.</summary>
    internal static PreflightItemDto CoolerItem(CameraDto? camera, bool readFailed) {
        if (readFailed) {
            return new PreflightItemDto("cooler", Unknown, "Cooler", "Could not read the camera.");
        }
        if (camera is null || camera.State != EquipmentConnectionState.Connected) {
            return new PreflightItemDto("cooler", Unknown, "Cooler", "Camera not connected.");
        }
        if (camera.Capabilities?.HasCooler != true) {
            return new PreflightItemDto("cooler", Ok, "Cooler", "This camera has no cooler.");
        }
        var rt = camera.Runtime;
        if (!rt.CoolerStateKnown || rt.CcdTemperature is not double temp) {
            return new PreflightItemDto("cooler", Unknown, "Cooler", "The cooler state could not be read.");
        }
        if (!rt.CoolerOn) {
            return new PreflightItemDto("cooler", Warn, "Cooler", $"Cooler off; sensor at {FormatC(temp)}.");
        }
        if (rt.CoolerSetpointC is not double setpoint) {
            return new PreflightItemDto("cooler", Warn, "Cooler", $"Cooler on, no setpoint reported; sensor at {FormatC(temp)}.");
        }
        return Math.Abs(temp - setpoint) <= CoolerBandC
            ? new PreflightItemDto("cooler", Ok, "Cooler", $"At target ({FormatC(setpoint)}).")
            : new PreflightItemDto("cooler", Warn, "Cooler", $"Sensor at {FormatC(temp)}, target {FormatC(setpoint)}: still settling.");
    }

    /// <summary>Mount safety: connected, not parked, tracking. The slew rate is not verified (Alpaca
    /// has no slew-duration estimate: the §58.9 watchdog uses the profile's expected flip seconds).</summary>
    internal static PreflightItemDto MountItem(TelescopeDto? telescope, bool readFailed) {
        if (readFailed) {
            return new PreflightItemDto("mount", Unknown, "Mount safety", "Could not read the mount.");
        }
        if (telescope is null || telescope.State != EquipmentConnectionState.Connected) {
            return new PreflightItemDto("mount", Fail, "Mount safety", "Mount not connected.");
        }
        var rt = telescope.Runtime;
        if (rt.Parked) {
            return new PreflightItemDto("mount", Fail, "Mount safety", "Mount is parked.");
        }
        if (!rt.Tracking) {
            return new PreflightItemDto("mount", Warn, "Mount safety", "Mount is not tracking.");
        }
        return new PreflightItemDto("mount", Ok, "Mount safety", "Tracking; the flip watchdog uses the profile's expected slew time.");
    }

    /// <summary>The running sequence(s): none running is a warning (nothing to sleep through), not a failure.</summary>
    internal static PreflightItemDto SequenceItem(IReadOnlyList<string> runningNames, bool readFailed) {
        if (readFailed) {
            return new PreflightItemDto("sequence", Unknown, "Tonight's sequence", "Could not read the sequence list.");
        }
        return runningNames.Count switch {
            0 => new PreflightItemDto("sequence", Warn, "Tonight's sequence", "No sequence is running."),
            1 => new PreflightItemDto("sequence", Ok, "Tonight's sequence", $"{runningNames[0]} is running."),
            _ => new PreflightItemDto("sequence", Ok, "Tonight's sequence", $"{runningNames.Count} sequences running: {string.Join(", ", runningNames)}."),
        };
    }

    /// <summary>When the target next crosses the flip point (meridian + <paramref name="minutesAfterMeridian"/>)
    /// and its altitude then, graded against the site's soft and hard floors. Null when that crossing is
    /// past <see cref="ForecastHorizon"/> (not tonight). Same LST/altitude maths as the flight check.</summary>
    internal static PreflightFlipDto? ForecastFlip(string target, Coordinates coords, double latDeg, double lonDeg,
            double minutesAfterMeridian, DateTimeOffset now, double softFloorDeg, double hardFloorDeg) {
        var lst = SiteAstrometry.LocalSiderealTimeDeg(now, lonDeg);
        var flipHourAngleDeg = minutesAfterMeridian / 60.0 * 15.0;
        var hourAngleDeg = SiteAstrometry.Mod360(lst - coords.RADegrees);
        var aheadDeg = SiteAstrometry.Mod360(flipHourAngleDeg - hourAngleDeg);
        // Sidereal degrees to solar seconds: 360° of hour angle per 23 h 56 m 04 s.
        var ahead = TimeSpan.FromSeconds(aheadDeg / 360.0 * 86164.0905);
        if (ahead > ForecastHorizon) {
            return null;
        }
        var at = now + ahead;
        var alt = SiteAstrometry.AltitudeFromHourAngleDeg(coords.Dec, latDeg, flipHourAngleDeg);
        var standing = alt < hardFloorDeg ? "below_floor" : softFloorDeg > 0 && alt < softFloorDeg ? "soft_warning" : Ok;
        return new PreflightFlipDto(target, at, Math.Round(alt, 1), standing);
    }

    /// <summary>The unattended window as the card prints it: the explicit clock window when the profile
    /// carries a valid one, else "sun" (astronomical darkness decides).</summary>
    internal static (string? Start, string? End, string Source) UnattendedWindow(SafetyPoliciesDto safety) =>
        UnattendedSeverity.TryParseWindow(safety.UnattendedWindowStart, safety.UnattendedWindowEnd, out var s, out var e)
            ? (s.ToString(@"hh\:mm", CultureInfo.InvariantCulture), e.ToString(@"hh\:mm", CultureInfo.InvariantCulture), "clock")
            : (null, null, "sun");

    // ─── readers ─────────────────────────────────────────────────────────────────────────────────

    private readonly record struct Read<T>(T? Value, bool Failed);

    // A reader that is not wired (null) reads as "no device/no data", not as a failure: the slim
    // test host and a daemon without that subsystem both answer the same way. A reader that THROWS
    // is a failure and the item says "could not read" (CA1031: the whole point is to grade the
    // throw, never to let one dead device driver take the checklist down).
    private static async Task<Read<T>> ReadAsync<T>(Func<Task<T?>>? read) where T : class {
        if (read is null) {
            return new Read<T>(default, false);
        }
        try {
            return new Read<T>(await read().ConfigureAwait(false), false);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            return new Read<T>(default, true);
        }
    }

    private static T? ReadQuiet<T>(Func<T?> read) where T : class {
        try {
            return read();
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            return null;
        }
    }

    // Null = no fault log wired or it could not be read (the item then just omits the fault count).
    private async Task<int?> CountUnresolvedFaultsAsync(CancellationToken ct) {
        if (_faults is null) {
            return null;
        }
        try {
            return (await _faults.ListAsync(100, null, null, null, unresolvedOnly: true, null, ct).ConfigureAwait(false)).Items.Count;
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            return null;
        }
    }

    private StorageSpaceDto ReadStorage() {
        var configured = _profiles.GetStorageSettings().SaveDirectory;
        var isFallback = string.IsNullOrWhiteSpace(configured);
        var dir = isFallback ? Path.Combine(AppContext.BaseDirectory, "frames") : configured!;
        var space = Directory.Exists(dir) ? DiskSpaceMonitor.TryGetSpace(dir) : null;
        return new StorageSpaceDto(dir, isFallback, space?.Free, space?.Total);
    }

    private sealed record RunningRun(Guid Id, string Name, string? TargetName, int TargetCount);

    private sealed class RunningList : List<RunningRun> {
        public bool Failed { get; init; }
    }

    private async Task<RunningList> RunningSequencesAsync(CancellationToken ct) {
        if (_sequences is null) {
            return new RunningList();
        }
        try {
            var page = await _sequences.ListAsync(100, null, ct).ConfigureAwait(false);
            var list = new RunningList();
            foreach (var s in page.Items.Where(IsLive)) {
                var run = _sequencer is null ? null : await _sequencer.GetRunStateAsync(s.Id, ct).ConfigureAwait(false);
                list.Add(new RunningRun(s.Id, s.Name, run?.CurrentTargetName, s.TargetCount));
            }
            return list;
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            return new RunningList { Failed = true };
        }
    }

    private static bool IsLive(SequenceListItemDto s) => s.CurrentRunState is SequenceRunState.Running
        or SequenceRunState.Starting or SequenceRunState.Paused or SequenceRunState.PausedAwaitingUser;

    private double MinutesAfterMeridian() =>
        ReadQuiet(() => _profileService?.ActiveProfile?.MeridianFlipSettings)?.MinutesAfterMeridian ?? 0.0;

    // ─── formatting ──────────────────────────────────────────────────────────────────────────────

    private static string Describe(EquipmentConnectionState s) => s switch {
        EquipmentConnectionState.Connecting => "still connecting",
        EquipmentConnectionState.Error => "in error",
        _ => "disconnected",
    };

    private static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string FormatGb(long bytes) => $"{bytes / (1024.0 * 1024 * 1024):0} GB";

    private static string FormatC(double c) => string.Create(CultureInfo.InvariantCulture, $"{c:0.0} °C");
}
