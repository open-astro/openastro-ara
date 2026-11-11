#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Model.Equipment;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Sequencer.SequenceItem.Autofocus;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// #1675 — measures each filter's focus offset: one autofocus on the reference filter, one on each picked
/// filter in slot order, a second on the reference. A filter's offset is its best position minus the mean of
/// the two reference best positions. Runs as a §65.5 background job, so the daemon owns it; the results stay
/// here until Save, Discard or the next calibration. One focus routine at a time: it refuses to start while
/// autofocus, the Bahtinov readout, guide-camera focus or a plan runs, and the autofocus endpoint refuses
/// while it runs.
/// </summary>
public sealed class FilterOffsetCalibrationService {

    /// <summary>The trigger each measurement's autofocus run record carries.</summary>
    internal const string Trigger = "offset calibration";

    private readonly IAutofocusExecutor _autofocus;
    private readonly AutofocusRunTracker _tracker;
    private readonly IBatchJobService _jobs;
    private readonly ICameraService _camera;
    private readonly IFocuserService _focuser;
    private readonly IFocuserMediator _focuserMediator;
    private readonly IFilterWheelService _wheel;
    private readonly IFilterWheelMediator _wheelMediator;
    private readonly IProfileStore _profiles;
    private readonly IProfileService _profile;
    private readonly ActiveRunSessionRegistry _runs;
    private readonly IBahtinovFocusService _bahtinov;
    private readonly IGuideFocusService _guideFocus;

    private readonly object _gate = new();
    private Run? _run;

    public FilterOffsetCalibrationService(
            IAutofocusExecutor autofocus,
            AutofocusRunTracker tracker,
            IBatchJobService jobs,
            ICameraService camera,
            IFocuserService focuser,
            IFocuserMediator focuserMediator,
            IFilterWheelService wheel,
            IFilterWheelMediator wheelMediator,
            IProfileStore profiles,
            IProfileService profile,
            ActiveRunSessionRegistry runs,
            IBahtinovFocusService bahtinov,
            IGuideFocusService guideFocus) {
        _autofocus = autofocus;
        _tracker = tracker;
        _jobs = jobs;
        _camera = camera;
        _focuser = focuser;
        _focuserMediator = focuserMediator;
        _wheel = wheel;
        _wheelMediator = wheelMediator;
        _profiles = profiles;
        _profile = profile;
        _runs = runs;
        _bahtinov = bahtinov;
        _guideFocus = guideFocus;
    }

    public bool IsRunning {
        get {
            lock (_gate) {
                return _run?.State == "running";
            }
        }
    }

    /// <summary>Start a calibration of <paramref name="positions"/> (null = the default picks). Throws
    /// <see cref="InvalidOperationException"/> with the reason when a precondition fails (409) and
    /// <see cref="ArgumentOutOfRangeException"/> for a slot the wheel does not have (400).</summary>
    public async Task<BatchJobDto> StartAsync(IReadOnlyList<int>? positions, CancellationToken ct) {
        var busy = IsRunning ? "an offset calibration is already running"
            : _tracker.IsRunning ? "an autofocus run is in progress"
            : _bahtinov.IsActive ? "the Bahtinov focus readout is running"
            : _guideFocus.IsActive ? "guide-camera focus is running"
            : _runs.HasAny ? "a plan is running"
            : null;
        if (busy is not null) {
            throw new InvalidOperationException($"Focus offsets can't be calibrated while {busy}.");
        }
        var camera = await _camera.GetAsync(ct).ConfigureAwait(false);
        var focuser = await _focuser.GetAsync(ct).ConfigureAwait(false);
        var wheel = await _wheel.GetAsync(ct).ConfigureAwait(false);
        var missing = new List<string>();
        if (camera?.State != EquipmentConnectionState.Connected) missing.Add("camera");
        if (focuser?.State != EquipmentConnectionState.Connected) missing.Add("focuser");
        if (wheel?.State != EquipmentConnectionState.Connected) missing.Add("filter wheel");
        if (missing.Count > 0) {
            throw new InvalidOperationException($"Connect the {string.Join(" and ", missing)} to calibrate focus offsets.");
        }
        if (wheel!.Manual) {
            throw new InvalidOperationException("Calibrating focus offsets needs a motorised filter wheel; the manual filter wheel can't change filters by itself.");
        }
        if (focuser!.Capabilities is not { AbsoluteFocuser: true } caps) {
            throw new InvalidOperationException("Calibrating focus offsets needs an absolute focuser; a relative focuser has no positions to compare.");
        }

        var slots = wheel.Slots;
        var picks = positions?.Distinct().ToList() ?? DefaultPicks(slots);
        foreach (var p in picks) {
            if (!slots.Any(s => s.Position == p)) {
                throw new ArgumentOutOfRangeException(nameof(positions), p, $"The filter wheel has no slot {p}.");
            }
        }
        var reference = ChooseReference(slots, picks, _profiles.GetAutofocusSettings().AfFilter);
        var measured = picks.Where(p => p != reference).OrderBy(p => p).ToList();
        if (reference is null || measured.Count == 0) {
            throw new InvalidOperationException("Pick at least one filter besides the reference filter.");
        }
        // The full CFZ: twice the half-CFZ step the Classic sweep seeds from. Null without µm/step or optics.
        var cfz = AutofocusSweepService.CfzStepSize(caps.StepSizeUm > 0 ? caps.StepSizeUm : null, _profiles.GetOpticsSettings()) * 2;

        var previous = PreviousOffsets(slots);
        var rows = slots.Where(s => s.Position == reference || measured.Contains(s.Position)).OrderBy(s => s.Position)
            .Select(s => new Row(s.Position, s.Name, previous[s.Position])).ToList();
        var run = new Run(reference.Value, rows, cfz);
        lock (_gate) {
            if (_run?.State == "running") {
                throw new InvalidOperationException("Focus offsets can't be calibrated while an offset calibration is already running.");
            }
            _run = run;
        }
        // A per-run identity: a finishing job of the previous run is a 409, never a silent join.
        var job = _jobs.Enqueue("filter_offset_calibration", run.Order.Count, Work(run), run.Id.ToString());
        lock (_gate) {
            run.JobId = job.JobId;
        }
        return job;
    }

    /// <summary>Stop the running calibration. False when none is running.</summary>
    public bool TryCancel() {
        Guid jobId;
        lock (_gate) {
            if (_run?.State != "running") {
                return false;
            }
            jobId = _run.JobId;
        }
        return _jobs.TryCancel(jobId);
    }

    /// <summary>Write each measured offset into the profile's filter list (the one a filter change applies).
    /// Failed and skipped filters keep theirs. False when there are no finished results.</summary>
    public bool TrySave() {
        FilterOffsetCalibrationDto results;
        lock (_gate) {
            if (_run is null || _run.State == "running") {
                return false;
            }
            results = Build(_run);
        }
        lock (FilterWheelService.ProfileFiltersLock) {
            var filters = _profile.ActiveProfile?.FilterWheelSettings?.FilterWheelFilters;
            if (filters is not null) {
                foreach (var row in results.Filters) {
                    if (row is not { Status: "measured", Offset: int offset }) {
                        continue;
                    }
                    if (filters.FirstOrDefault(f => f.Position == row.Position) is { } filter) {
                        filter.FocusOffset = offset;
                    } else {
                        filters.Add(new FilterInfo(row.Name, offset, (short)row.Position));
                    }
                }
            }
        }
        return TryDiscard();
    }

    /// <summary>Drop the finished results. False when there are none.</summary>
    public bool TryDiscard() {
        lock (_gate) {
            if (_run is null || _run.State == "running") {
                return false;
            }
            _run = null;
            return true;
        }
    }

    public FilterOffsetCalibrationDto? Snapshot() {
        lock (_gate) {
            return _run is null ? null : Build(_run);
        }
    }

    /// <summary>Every named slot except a dark.</summary>
    internal static List<int> DefaultPicks(IReadOnlyList<FilterSlotDto> slots) =>
        slots.Where(s => s.Name.Trim().Length > 0 && !s.Name.Contains("dark", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Position).ToList();

    /// <summary>The profile's autofocus filter, else a slot named L or Lum, else the lowest pick.</summary>
    internal static int? ChooseReference(IReadOnlyList<FilterSlotDto> slots, IReadOnlyCollection<int> picks, string? afFilter) {
        FilterSlotDto? Named(Func<string, bool> match) => slots.FirstOrDefault(s => s.Name.Trim().Length > 0 && match(s.Name.Trim()));
        if (!string.IsNullOrWhiteSpace(afFilter) && Named(n => n.Equals(afFilter.Trim(), StringComparison.OrdinalIgnoreCase)) is { } af) {
            return af.Position;
        }
        if (Named(n => n.Equals("L", StringComparison.OrdinalIgnoreCase) || n.Equals("Lum", StringComparison.OrdinalIgnoreCase)) is { } l) {
            return l.Position;
        }
        return picks.Count > 0 ? picks.Min() : null;
    }

    // The offset a filter change applies today: the profile's entry wins over the device slot, as in
    // FilterWheelService.ResolveFilter.
    private Dictionary<int, int> PreviousOffsets(IReadOnlyList<FilterSlotDto> slots) {
        var offsets = slots.ToDictionary(s => s.Position, s => s.FocusOffset);
        lock (FilterWheelService.ProfileFiltersLock) {
            var filters = _profile.ActiveProfile?.FilterWheelSettings?.FilterWheelFilters;
            foreach (var f in filters ?? Enumerable.Empty<FilterInfo>()) {
                if (offsets.ContainsKey(f.Position)) {
                    offsets[f.Position] = f.FocusOffset;
                }
            }
        }
        return offsets;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Job boundary: a filter-change fault of any kind ends the calibration as failed with its reason, after the end state is restored.")]
    private Func<Action<int>, CancellationToken, Task> Work(Run run) =>
        async (tick, ct) => {
            try {
                for (var i = 0; i < run.Order.Count; i++) {
                    var row = run.Rows.First(r => r.Position == run.Order[i]);
                    lock (_gate) {
                        run.CurrentPosition = row.Position;
                        run.RunIndex = i + 1;
                    }
                    var (best, rSquared, failure) = await MeasureAsync(row, ct).ConfigureAwait(false);
                    lock (_gate) {
                        if (row.Position != run.Reference) {
                            row.BestPosition = best;
                            row.RSquared = rSquared;
                            row.FailureReason = failure;
                            row.Status = best is null ? "failed" : "measured";
                        } else if (i == 0) {
                            run.ReferenceFirst = best;
                            run.ReferenceFirstRSquared = rSquared;
                            row.FailureReason = failure;
                        } else {
                            run.ReferenceSecond = best;
                            run.ReferenceSecondRSquared = rSquared;
                            row.FailureReason = run.ReferenceFirst is null ? failure : null;
                        }
                        if (row.Position == run.Reference) {
                            row.Status = (run.ReferenceFirst ?? run.ReferenceSecond) is not null ? "measured" : i == 0 ? "pending" : "failed";
                        }
                    }
                    tick(i + 1);
                }
                lock (_gate) {
                    run.State = run.ReferenceFirst is null && run.ReferenceSecond is null ? "failed" : "complete";
                    if (run.State == "failed") {
                        var name = run.Rows.First(r => r.Position == run.Reference).Name;
                        run.Reason = $"The reference filter {name} failed both autofocus runs: {run.Rows.First(r => r.Position == run.Reference).FailureReason}";
                    }
                }
            } catch (OperationCanceledException) {
                lock (_gate) {
                    run.State = "cancelled";
                }
            } catch (Exception ex) {
                lock (_gate) {
                    run.State = "failed";
                    run.Reason = ex.Message;
                }
            } finally {
                await ReturnToReferenceAsync(run).ConfigureAwait(false);
                lock (_gate) {
                    run.CurrentPosition = null;
                    foreach (var row in run.Rows.Where(r => r.Status == "pending")) {
                        row.Status = row.Position == run.Reference && row.FailureReason is not null ? "failed" : "skipped";
                    }
                }
            }
            // The job lands as the calibration did; a calibration with failed filters is still complete.
            if (run.State == "cancelled") {
                throw new OperationCanceledException("Offset calibration was cancelled.");
            }
            if (run.State == "failed") {
                throw new InvalidOperationException(run.Reason);
            }
        };

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "One filter's autofocus may throw any device or math exception; it fails that filter, not the run. Cancellation still propagates.")]
    private async Task<(int? Best, double? RSquared, string? Failure)> MeasureAsync(Row row, CancellationToken ct) {
        await _wheelMediator.ChangeFilter(new FilterInfo(row.Name, row.PreviousOffset, (short)row.Position), null, ct).ConfigureAwait(false);
        _tracker.StampNextTrigger(Trigger);
        bool ok;
        try {
            ok = await _autofocus.RunAutofocusAsync(new Progress<ApplicationStatus>(), ct).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            return (null, null, ex.Message);
        }
        var record = _tracker.Snapshot();
        if (ok) {
            return (record.FinalPosition ?? _focuserMediator.GetInfo().Position, record.Fit?.RSquared, null);
        }
        // A Cancel from the Smart Focus pane stops the autofocus in progress: the user wants the routine stopped.
        if (record.State == "cancelled") {
            throw new OperationCanceledException("Autofocus was cancelled.");
        }
        return (null, null, record.Reason is { Length: > 0 } reason ? reason : "autofocus failed");
    }

    // Once the reference has measured, every ending puts it back in place with the focuser at its latest best.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "End-state boundary: a wheel or focuser fault here must not hide the calibration's results; it is reported in the reason.")]
    private async Task ReturnToReferenceAsync(Run run) {
        int? best;
        Row reference;
        lock (_gate) {
            best = run.ReferenceSecond ?? run.ReferenceFirst;
            reference = run.Rows.First(r => r.Position == run.Reference);
        }
        if (best is not int position) {
            return;
        }
        try {
            await _wheelMediator.ChangeFilter(new FilterInfo(reference.Name, reference.PreviousOffset, (short)reference.Position), null, CancellationToken.None).ConfigureAwait(false);
            await _focuserMediator.MoveFocuser(position, CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            lock (_gate) {
                run.Reason ??= $"Could not return to the reference filter {reference.Name}: {ex.Message}";
            }
        }
    }

    private static FilterOffsetCalibrationDto Build(Run run) {
        double? baseline = (run.ReferenceFirst, run.ReferenceSecond) switch {
            (int a, int b) => (a + b) / 2.0,
            (int a, null) => a,
            (null, int b) => b,
            _ => null,
        };
        var driftChecked = run.ReferenceFirst is not null && run.ReferenceSecond is not null && run.Cfz is not null;
        var filters = run.Rows.Select(r => r.Position == run.Reference
            ? new FilterOffsetResultDto(r.Position, r.Name, r.Status,
                baseline is double b ? (int)Math.Round(b, MidpointRounding.AwayFromZero) : null,
                baseline is null ? null : 0,
                run.ReferenceFirstRSquared ?? run.ReferenceSecondRSquared, r.PreviousOffset, r.FailureReason)
            : new FilterOffsetResultDto(r.Position, r.Name, r.Status, r.BestPosition,
                r.BestPosition is int best && baseline is double bl ? (int)Math.Round(best - bl, MidpointRounding.AwayFromZero) : null,
                r.RSquared, r.PreviousOffset, r.FailureReason)).ToList();
        return new FilterOffsetCalibrationDto(
            State: run.State,
            JobId: run.JobId,
            ReferencePosition: run.Reference,
            ReferenceName: run.Rows.First(r => r.Position == run.Reference).Name,
            ReferenceFirstPosition: run.ReferenceFirst,
            ReferenceSecondPosition: run.ReferenceSecond,
            CfzSteps: run.Cfz,
            DriftChecked: driftChecked,
            DriftWarning: driftChecked && Math.Abs(run.ReferenceFirst!.Value - run.ReferenceSecond!.Value) > run.Cfz!.Value,
            CurrentPosition: run.CurrentPosition,
            RunIndex: run.RunIndex,
            RunCount: run.Order.Count,
            Reason: run.Reason,
            Filters: filters);
    }

    // Mutable run state, guarded by _gate.
    private sealed class Run {
        public Run(int reference, List<Row> rows, int? cfz) {
            Reference = reference;
            Rows = rows;
            Cfz = cfz;
            Order = [reference, .. rows.Where(r => r.Position != reference).Select(r => r.Position), reference];
        }

        public Guid Id { get; } = Guid.NewGuid();
        public int Reference { get; }
        public List<Row> Rows { get; }
        public List<int> Order { get; }
        public int? Cfz { get; }
        public Guid JobId { get; set; }
        public string State { get; set; } = "running";
        public string? Reason { get; set; }
        public int? CurrentPosition { get; set; }
        public int RunIndex { get; set; }
        public int? ReferenceFirst { get; set; }
        public int? ReferenceSecond { get; set; }
        public double? ReferenceFirstRSquared { get; set; }
        public double? ReferenceSecondRSquared { get; set; }
    }

    private sealed class Row {
        public Row(int position, string name, int previousOffset) {
            Position = position;
            Name = name;
            PreviousOffset = previousOffset;
        }

        public int Position { get; }
        public string Name { get; }
        public int PreviousOffset { get; }
        public string Status { get; set; } = "pending";
        public int? BestPosition { get; set; }
        public double? RSquared { get; set; }
        public string? FailureReason { get; set; }
    }
}
