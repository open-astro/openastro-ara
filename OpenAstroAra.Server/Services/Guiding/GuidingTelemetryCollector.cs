#region "copyright"

/* Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors. */

#endregion

using OpenAstroAra.Core.Interfaces;
using OpenAstroAra.Core.Guiding;
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2.PhdEvents;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services.Guiding;

/// <summary>Bounded, replayable guide-step collector. It never changes equipment.</summary>
public sealed class GuidingTelemetryCollector : IDisposable {
    private const int MaximumSamples = 12000;
    private readonly object _gate = new();
    private readonly List<GuidingTelemetrySample> _samples = new();
    private readonly IGuiderMediator _guider;
    private readonly long _startedTimestamp = Stopwatch.GetTimestamp();
    private long _sequence;
    private long _lastMonotonicTimestampTicks;
    private DateTimeOffset _lastTimestampUtc;
    private double? _exposureMilliseconds;
    private double? _guidePixelScaleArcsecPerPixel;
    private bool _isDither;
    private bool _isSettling;
    private bool _isCalibration;
    private bool _guideOutputEnabled = true;
    private double? _mountRightAscensionHours;
    private double? _mountDeclinationDegrees;
    private double? _mountAzimuthDegrees;
    private string? _parameterSnapshotHash;
    private bool _disposed;

    public GuidingTelemetryCollector(IGuiderMediator guider) {
        _guider = guider ?? throw new ArgumentNullException(nameof(guider));
        _guider.GuideEvent += OnGuideStep;
    }

    public int Count { get { lock (_gate) return _samples.Count; } }

    public void SetContext(double? exposureMilliseconds = null,
        double? guidePixelScaleArcsecPerPixel = null, bool? isDither = null,
        bool? isSettling = null, bool? isCalibration = null,
        bool? guideOutputEnabled = null, double? mountRightAscensionHours = null,
        double? mountDeclinationDegrees = null, double? mountAzimuthDegrees = null,
        string? parameterSnapshotHash = null) {
        lock (_gate) {
            if (exposureMilliseconds is not null) _exposureMilliseconds = exposureMilliseconds;
            if (guidePixelScaleArcsecPerPixel is not null) _guidePixelScaleArcsecPerPixel = guidePixelScaleArcsecPerPixel;
            if (isDither is not null) _isDither = isDither.Value;
            if (isSettling is not null) _isSettling = isSettling.Value;
            if (isCalibration is not null) _isCalibration = isCalibration.Value;
            if (guideOutputEnabled is not null) _guideOutputEnabled = guideOutputEnabled.Value;
            if (mountRightAscensionHours is not null) _mountRightAscensionHours = mountRightAscensionHours;
            if (mountDeclinationDegrees is not null) _mountDeclinationDegrees = mountDeclinationDegrees;
            if (mountAzimuthDegrees is not null) _mountAzimuthDegrees = mountAzimuthDegrees;
            if (parameterSnapshotHash is not null) _parameterSnapshotHash = parameterSnapshotHash;
        }
    }

    public async Task<bool> WaitForSamplesAsync(int minimumSamples, TimeSpan timeout, CancellationToken ct) {
        if (minimumSamples <= 0) return true;
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Count < minimumSamples) {
            ct.ThrowIfCancellationRequested();
            if (Stopwatch.GetTimestamp() >= deadline) return false;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        return true;
    }

    public GuidingTelemetryWindow GetWindow(int maximumSamples = MaximumSamples) {
        lock (_gate) {
            var selected = _samples.TakeLast(Math.Clamp(maximumSamples, 1, MaximumSamples)).ToArray();
            var start = selected.Length == 0 ? DateTimeOffset.UtcNow : selected[0].TimestampUtc;
            var end = selected.Length == 0 ? start : selected[^1].TimestampUtc;
            return new GuidingTelemetryWindow(selected, "live-guider-events", start, end);
        }
    }

    public void Clear() {
        lock (_gate) {
            _samples.Clear();
            _lastTimestampUtc = default;
            _lastMonotonicTimestampTicks = 0;
        }
    }

    private void OnGuideStep(object? sender, IGuideStep step) {
        if (step is null) return;
        var monotonicTimestampTicks = Stopwatch.GetTimestamp();
        DateTimeOffset timestamp;
        lock (_gate) timestamp = ToTimestamp(step.Time, _lastTimestampUtc);
        GuidingTelemetrySample? previous;
        lock (_gate) previous = _samples.Count == 0 ? null : _samples[^1];
        double? interval = previous is null ? null
            : previous.MonotonicTimestampTicks > 0
                ? (monotonicTimestampTicks - previous.MonotonicTimestampTicks) * 1000d / Stopwatch.Frequency
                : (timestamp - previous.TimestampUtc).TotalMilliseconds;
        var phd = step as PhdEventGuideStep;
        bool isDither, isSettling, isCalibration, guideOutputEnabled;
        double? exposure, scale, mountRa, mountDec, mountAz;
        string? parameterHash;
        lock (_gate) {
            isDither = _isDither;
            isSettling = _isSettling;
            isCalibration = _isCalibration;
            guideOutputEnabled = _guideOutputEnabled;
            exposure = _exposureMilliseconds;
            scale = _guidePixelScaleArcsecPerPixel;
            mountRa = _mountRightAscensionHours;
            mountDec = _mountDeclinationDegrees;
            mountAz = _mountAzimuthDegrees;
            parameterHash = _parameterSnapshotHash;
        }
        var sample = new GuidingTelemetrySample(
            Interlocked.Increment(ref _sequence), timestamp,
            step.RADistanceRaw, step.DECDistanceRaw,
            phd?.RADistanceGuide ?? step.RADistanceRaw,
            phd?.DECDistanceGuide ?? step.DECDistanceRaw,
            Math.Abs(step.RADuration), Math.Abs(step.DECDuration),
            phd?.RADirection, phd?.DECDirection,
            exposure, interval, phd?.SNR, phd?.HFD, phd?.StarMass,
            phd?.MultiStarCount, phd?.RejectedStarCount, phd?.RALimited ?? false, phd?.DecLimited ?? false,
            phd?.ErrorCode is > 0, isDither, isSettling, isCalibration, guideOutputEnabled, scale,
            monotonicTimestampTicks, null, null, mountRa, mountDec, mountAz, parameterHash);
        lock (_gate) {
            if (_disposed || (_samples.Count > 0 && timestamp <= _samples[^1].TimestampUtc)) return;
            _samples.Add(sample);
            _lastTimestampUtc = timestamp;
            _lastMonotonicTimestampTicks = monotonicTimestampTicks;
            if (_samples.Count > MaximumSamples) _samples.RemoveRange(0, _samples.Count - MaximumSamples);
        }
    }

    private DateTimeOffset ToTimestamp(double unixSeconds, DateTimeOffset previous) {
        if (double.IsFinite(unixSeconds) && unixSeconds > 1) {
            try { return DateTimeOffset.FromUnixTimeMilliseconds((long)(unixSeconds * 1000)); }
            catch (ArgumentOutOfRangeException) { }
        }
        var elapsed = (Stopwatch.GetTimestamp() - _startedTimestamp) / (double)Stopwatch.Frequency;
        var fallback = DateTimeOffset.UtcNow;
        if (previous != default && fallback <= previous)
            fallback = previous.AddTicks(Math.Max(1, (long)(elapsed * TimeSpan.TicksPerSecond)));
        return fallback;
    }

    public void Dispose() {
        lock (_gate) {
            if (_disposed) return;
            _disposed = true;
            _guider.GuideEvent -= OnGuideStep;
        }
    }
}
