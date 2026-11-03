#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Image.ImageAnalysis;
using OpenAstroAra.Stretch;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services.LiveStack;

/// <summary>What one <see cref="LiveStackEngine.Add"/> did with a frame.</summary>
public enum StackAddOutcome { Reference, Stacked, Unregistered, WrongSize, TooLarge, Dropped }

/// <summary>Result of one Add. Offsets follow the engine convention: a frame's stars sit at
/// <c>R(rot)·(p − centre) + centre + (dx, dy)</c> relative to the reference (centre = width/2, height/2).</summary>
public sealed record StackAddResult(StackAddOutcome Outcome, double OffsetX = 0, double OffsetY = 0, double RotationDeg = 0);

/// <summary>One immutable published view of the stack (status + preview), swapped as a single reference so a reader
/// never sees a torn pair. Readonly fields, not properties: CA1819 on the JPEG array.</summary>
internal sealed class StackSnapshot {
    public readonly bool Enabled;
    public readonly long SessionId;
    public readonly long Seq;
    public readonly int FramesStacked;
    public readonly int FramesUnregistered;
    public readonly long RejectedSamples;
    public readonly int? ReferenceStars;
    public readonly int? Width;
    public readonly int? Height;
    public readonly double? LastOffsetX;
    public readonly double? LastOffsetY;
    public readonly double? LastRotationDeg;
    public readonly StackAddOutcome? LastOutcome;
    public readonly DateTimeOffset? LastFrameAt;
    public readonly byte[]? Jpeg;

    public StackSnapshot(bool enabled, long sessionId, long seq, int framesStacked, int framesUnregistered, long rejected,
            int? referenceStars, int? width, int? height, double? offX, double? offY, double? rot,
            StackAddOutcome? lastOutcome, DateTimeOffset? lastFrameAt, byte[]? jpeg) {
        Enabled = enabled; SessionId = sessionId; Seq = seq; FramesStacked = framesStacked;
        FramesUnregistered = framesUnregistered; RejectedSamples = rejected; ReferenceStars = referenceStars;
        Width = width; Height = height; LastOffsetX = offX; LastOffsetY = offY; LastRotationDeg = rot;
        LastOutcome = lastOutcome; LastFrameAt = lastFrameAt; Jpeg = jpeg;
    }
}

/// <summary>
/// ↑ara#1169 live-stack engine (PORT_PLAYBOOK §64.17): registers each raw frame to the first one by its stars
/// (rotation + translation, no scale), resamples it into the reference grid and keeps a per-pixel Welford running
/// mean/variance with one-pass sigma clipping. Pure CPU, no camera knowledge. One leaf lock <c>_sync</c> guards every
/// mutation and never calls out while held; readers use the volatile <see cref="Snapshot"/> and never wait for it.
/// Constants are fixed in slice 1 (k = 3 sigma, warm-up 5 samples per pixel, restart after 10 unregistered frames).
/// Known limit: slow real drift (clouds, sky glow) is tracked only slowly by the mean, so clipped samples rise during
/// a brightness ramp; Reset recovers.
/// </summary>
public sealed class LiveStackEngine {

    /// <summary>The pixel cap (width x height) above which a frame is refused: 12 MP.</summary>
    public const long DefaultMaxPixels = 12_000_000;

    private const int MinReferenceStars = 8;
    private const int MatchStars = 30;
    private const int DetectedStarCap = 50;
    private const int RestartAfterUnregistered = 10;
    private const int MinMatches = 6;
    private const double MinMatchFraction = 0.4;
    private const double MaxRmsPx = 0.7;
    private const double MatchRadiusPx = 2.0;
    private const double MaxRotationDeg = 10.0;
    private const double RotationBinDeg = 0.1;
    private const double VoteLengthTolPx = 1.5;
    private const double MinVoteLengthPx = 40.0;
    private const int FloorSampleCount = 4096;
    private const int MinFloorSamples = 100;
    private const int PreviewMaxDim = 1024;

    private readonly object _sync = new();
    private readonly double _sigma;
    private readonly int _warmUp;
    private readonly long _maxPixels;

    // All below guarded by _sync.
    private bool _enabled = true;
    private long _session;
    private float[]? _mean;
    private float[]? _m2;
    private ushort[]? _n;
    private int _w, _h;
    private (double X, double Y)[]? _refStars;
    private int _refStarCount;
    private int _framesStacked, _framesUnregistered, _consecutiveUnregistered;
    private long _rejected, _seq;
    private byte[]? _jpeg;

    private volatile StackSnapshot _snapshot;

    public LiveStackEngine(double sigma = 3.0, int warmUp = 5, long maxPixels = DefaultMaxPixels) {
        _sigma = sigma; _warmUp = warmUp; _maxPixels = maxPixels;
        _snapshot = new StackSnapshot(true, 0, 0, 0, 0, 0, null, null, null, null, null, null, null, null, null);
    }

    internal StackSnapshot Snapshot => _snapshot;
    public int FramesStacked => _snapshot.FramesStacked;
    public int FramesUnregistered => _snapshot.FramesUnregistered;
    public long RejectedSamples => _snapshot.RejectedSamples;

    /// <summary>Resets, binds the engine to a Live View session and enables it.</summary>
    public void Enable(long session) {
        lock (_sync) {
            ClearStack();
            _enabled = true;
            _session = session;
            Publish(null, 0, 0, 0, null);
        }
    }

    /// <summary>Resets and disables: every later Add is dropped until the next Enable.</summary>
    public void Disable() {
        lock (_sync) {
            ClearStack();
            _enabled = false;
            Publish(null, 0, 0, 0, null);
        }
    }

    /// <summary>Empties the stack; the next frame becomes the reference. Stays enabled.</summary>
    public void Reset() {
        lock (_sync) {
            ClearStack();
            Publish(null, 0, 0, 0, null);
        }
    }

    /// <summary>Test seam: sets one pixel's sample counter, to reach the 65535 cap without 65k frames.</summary>
    internal void SetPixelCountForTest(int index, ushort count) {
        lock (_sync) { _n![index] = count; }
    }

    /// <summary>The stacked mean as a copy (row-major, reference grid), or null when nothing is stacked.</summary>
    public float[]? SnapshotMean(out int width, out int height) {
        lock (_sync) {
            width = _w; height = _h;
            return _mean is null ? null : (float[])_mean.Clone();
        }
    }

    /// <summary>The stretched stack as a JPEG (max dimension 1024), rendered when the last frame was added.</summary>
    public byte[]? RenderPreview() => _snapshot.Jpeg;

    public StackAddResult Add(ushort[] pixels, int width, int height, long session = 0) {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width <= 0 || height <= 0 || pixels.Length != (long)width * height) {
            throw new ArgumentException("pixels length must equal width * height.", nameof(pixels));
        }
        lock (_sync) {
            // Check and store in one section: a Disable/session change that lands first wins, one that lands
            // after clears whatever this Add stored.
            if (!_enabled || session != _session) {
                return new StackAddResult(StackAddOutcome.Dropped);
            }
            if ((long)width * height > _maxPixels) {
                return Finish(new StackAddResult(StackAddOutcome.TooLarge));
            }
            if (_mean is not null && (width != _w || height != _h)) {
                return Finish(new StackAddResult(StackAddOutcome.WrongSize));
            }

            var stars = DetectStars(pixels, width, height, out var detected);
            if (_mean is null) {
                if (detected < MinReferenceStars) {
                    _framesUnregistered++;
                    return Finish(new StackAddResult(StackAddOutcome.Unregistered));
                }
                return Finish(StartReference(pixels, width, height, stars, detected));
            }

            var reg = Register(_refStars!, stars, width / 2.0, height / 2.0);
            if (reg is null) {
                _framesUnregistered++;
                if (++_consecutiveUnregistered >= RestartAfterUnregistered) {
                    // The user slewed or re-framed: start over on the new field if it has enough stars.
                    ClearStack();
                    if (detected >= MinReferenceStars) {
                        return Finish(StartReference(pixels, width, height, stars, detected));
                    }
                }
                return Finish(new StackAddResult(StackAddOutcome.Unregistered));
            }

            _consecutiveUnregistered = 0;
            var (rot, dx, dy) = reg.Value;
            Accumulate(pixels, width, height, rot, dx, dy);
            _framesStacked++;
            return Finish(new StackAddResult(StackAddOutcome.Stacked, dx, dy, rot * 180 / Math.PI));
        }
    }

    private void ClearStack() {
        _mean = null; _m2 = null; _n = null; _w = _h = 0;
        _refStars = null; _refStarCount = 0;
        _framesStacked = _framesUnregistered = _consecutiveUnregistered = 0;
        _rejected = 0;
        _jpeg = null;
    }

    private StackAddResult StartReference(ushort[] pixels, int width, int height, (double X, double Y)[] stars, int detected) {
        int count = pixels.Length;
        _w = width; _h = height;
        _mean = new float[count];
        _m2 = new float[count];
        _n = new ushort[count];
        for (int i = 0; i < count; i++) { _mean[i] = pixels[i]; }
        Array.Fill(_n, (ushort)1);
        _refStars = stars;
        _refStarCount = detected;
        _framesStacked = 1;
        _consecutiveUnregistered = 0;
        return new StackAddResult(StackAddOutcome.Reference);
    }

    // Records the outcome in the published snapshot (and a fresh preview when a frame was added).
    private StackAddResult Finish(StackAddResult r) {
        bool added = r.Outcome is StackAddOutcome.Reference or StackAddOutcome.Stacked;
        if (added) {
            _seq++;
            _jpeg = RenderJpeg();
        }
        Publish(r.Outcome, r.OffsetX, r.OffsetY, r.RotationDeg, added ? DateTimeOffset.UtcNow : null);
        return r;
    }

    // Always called with _sync held. A non-added outcome keeps the previous JPEG; Reset/Disable/Enable cleared it.
    private void Publish(StackAddOutcome? outcome, double offX, double offY, double rotDeg, DateTimeOffset? frameAt) {
        bool registered = outcome is StackAddOutcome.Stacked;
        var prev = _snapshot;
        _snapshot = new StackSnapshot(
            _enabled, _session, _seq, _framesStacked, _framesUnregistered, _rejected,
            _mean is null ? null : _refStarCount, _mean is null ? null : _w, _mean is null ? null : _h,
            registered ? offX : (outcome is null ? null : prev.LastOffsetX),
            registered ? offY : (outcome is null ? null : prev.LastOffsetY),
            registered ? rotDeg : (outcome is null ? null : prev.LastRotationDeg),
            outcome, frameAt ?? (outcome is null ? null : prev.LastFrameAt), _jpeg);
    }

    private byte[] RenderJpeg() {
        var mean = _mean!;
        var u = new ushort[mean.Length];
        for (int i = 0; i < u.Length; i++) { u[i] = (ushort)Math.Clamp(mean[i] + 0.5f, 0f, 65535f); }
        var stretched = Stretcher.Apply(StretchAlgorithm.AutoStf, u);
        return JpegEncoder.EncodeGray(stretched, _w, _h, maxDim: PreviewMaxDim);
    }

    // Brightest MatchStars of the detector's (capped) list, as pixel coordinates.
    private static (double X, double Y)[] DetectStars(ushort[] pixels, int width, int height, out int detected) {
        var result = StarDetector.Detect(pixels, width, height, CameraService.AnalysisDetectionParams(DetectedStarCap), CancellationToken.None);
        detected = result.StarList.Count;
        return result.StarList.OrderByDescending(s => s.MaxBrightness).Take(MatchStars)
            .Select(s => { var (x, y) = s.Unpack(width); return ((double)x, (double)y); }).ToArray();
    }

    // Returns (rotation rad, dx, dy) mapping reference stars onto the frame's, or null when the match is not trusted.
    private static (double Rot, double Dx, double Dy)? Register((double X, double Y)[] a, (double X, double Y)[] b, double cx, double cy) {
        if (a.Length < MinMatches || b.Length < MinMatches) { return null; }

        double rot = EstimateRotation(a, b);
        double c = Math.Cos(rot), s = Math.Sin(rot);
        // Translation: vote B − R·A into a 1 px grid, take the best 3x3 window and average its votes.
        var votes = new Dictionary<(int, int), int>();
        var diffs = new List<(double, double)>(a.Length * b.Length);
        foreach (var p in a) {
            double rx = c * (p.X - cx) - s * (p.Y - cy) + cx, ry = s * (p.X - cx) + c * (p.Y - cy) + cy;
            foreach (var q in b) {
                double ddx = q.X - rx, ddy = q.Y - ry;
                diffs.Add((ddx, ddy));
                var key = ((int)Math.Round(ddx), (int)Math.Round(ddy));
                votes[key] = votes.GetValueOrDefault(key) + 1;
            }
        }
        (int, int) best = default; int bestSum = 0;
        foreach (var k in votes.Keys) {
            int sum = 0;
            for (int ox = -1; ox <= 1; ox++) for (int oy = -1; oy <= 1; oy++) sum += votes.GetValueOrDefault((k.Item1 + ox, k.Item2 + oy));
            if (sum > bestSum) { bestSum = sum; best = k; }
        }
        double sx = 0, sy = 0; int cnt = 0;
        foreach (var (ddx, ddy) in diffs) {
            if (Math.Abs(ddx - best.Item1) <= 1.5 && Math.Abs(ddy - best.Item2) <= 1.5) { sx += ddx; sy += ddy; cnt++; }
        }
        if (cnt == 0) { return null; }
        double dx = sx / cnt, dy = sy / cnt;

        // Refine: nearest-neighbour matches within MatchRadiusPx, then a least-squares similarity (no scale) fit.
        int matches = 0; double rms = double.MaxValue;
        for (int iter = 0; iter < 4; iter++) {
            c = Math.Cos(rot); s = Math.Sin(rot);
            var pa = new List<(double X, double Y)>(); var pb = new List<(double X, double Y)>();
            foreach (var p in a) {
                double px = c * (p.X - cx) - s * (p.Y - cy) + cx + dx, py = s * (p.X - cx) + c * (p.Y - cy) + cy + dy;
                double bd = MatchRadiusPx * MatchRadiusPx; (double X, double Y)? bq = null;
                foreach (var q in b) {
                    double d2 = (q.X - px) * (q.X - px) + (q.Y - py) * (q.Y - py);
                    if (d2 <= bd) { bd = d2; bq = q; }
                }
                if (bq is { } m) { pa.Add((p.X - cx, p.Y - cy)); pb.Add((m.X - cx, m.Y - cy)); }
            }
            matches = pa.Count;
            if (matches < MinMatches) { return null; }
            double mx = pa.Average(t => t.X), my = pa.Average(t => t.Y), nx = pb.Average(t => t.X), ny = pb.Average(t => t.Y);
            double dot = 0, cross = 0;
            for (int i = 0; i < matches; i++) {
                double ux = pa[i].X - mx, uy = pa[i].Y - my, vx = pb[i].X - nx, vy = pb[i].Y - ny;
                dot += ux * vx + uy * vy; cross += ux * vy - uy * vx;
            }
            rot = Math.Atan2(cross, dot);
            c = Math.Cos(rot); s = Math.Sin(rot);
            dx = nx - (c * mx - s * my); dy = ny - (s * mx + c * my);
            double sum2 = 0;
            for (int i = 0; i < matches; i++) {
                double ex = c * pa[i].X - s * pa[i].Y + dx - pb[i].X, ey = s * pa[i].X + c * pa[i].Y + dy - pb[i].Y;
                sum2 += ex * ex + ey * ey;
            }
            rms = Math.Sqrt(sum2 / matches);
        }
        if (matches < MinMatches || matches < MinMatchFraction * Math.Min(a.Length, b.Length) || rms > MaxRmsPx) { return null; }
        return (rot, dx, dy);
    }

    // Votes the angle difference of every equal-length star-pair vector into a 0.1 deg histogram (within ±10 deg);
    // the best 3-bin window's mean angle is the rotation estimate (radians). 0 when nothing votes.
    private static double EstimateRotation((double X, double Y)[] a, (double X, double Y)[] b) {
        int bins = (int)Math.Round(2 * MaxRotationDeg / RotationBinDeg) + 1;
        var count = new int[bins]; var sum = new double[bins];
        var va = PairVectors(a, false); var vb = PairVectors(b, true);
        foreach (var (ax, ay, al, aa) in va) {
            foreach (var (_, _, bl, ba) in vb) {
                if (Math.Abs(al - bl) > VoteLengthTolPx) { continue; }
                double d = (ba - aa) * 180 / Math.PI;
                d -= 360 * Math.Round(d / 360);
                if (Math.Abs(d) > MaxRotationDeg) { continue; }
                int bin = (int)Math.Round((d + MaxRotationDeg) / RotationBinDeg);
                count[bin]++; sum[bin] += d;
            }
        }
        int bestBin = -1, bestCount = 0;
        for (int i = 0; i < bins; i++) {
            int n = count[i] + (i > 0 ? count[i - 1] : 0) + (i < bins - 1 ? count[i + 1] : 0);
            if (n > bestCount) { bestCount = n; bestBin = i; }
        }
        if (bestBin < 0) { return 0; }
        double total = 0; int cnt = 0;
        for (int i = Math.Max(0, bestBin - 1); i <= Math.Min(bins - 1, bestBin + 1); i++) { total += sum[i]; cnt += count[i]; }
        return cnt == 0 ? 0 : total / cnt * Math.PI / 180;
    }

    // Vectors between star pairs longer than MinVoteLengthPx; both directions when asked (the frame side).
    private static List<(double X, double Y, double Len, double Angle)> PairVectors((double X, double Y)[] st, bool both) {
        var l = new List<(double, double, double, double)>();
        for (int i = 0; i < st.Length; i++) {
            for (int j = both ? 0 : i + 1; j < st.Length; j++) {
                if (i == j) { continue; }
                double x = st[j].X - st[i].X, y = st[j].Y - st[i].Y, len = Math.Sqrt(x * x + y * y);
                if (len >= MinVoteLengthPx) { l.Add((x, y, len, Math.Atan2(y, x))); }
            }
        }
        return l;
    }

    // Resamples the frame into the reference grid (bilinear; frame position = R(rot)(q − centre) + centre + (dx, dy))
    // and folds each sample into the per-pixel Welford state with a one-pass sigma clip against the prior estimate.
    private void Accumulate(ushort[] pixels, int w, int h, double rot, double dx, double dy) {
        var mean = _mean!; var m2 = _m2!; var nArr = _n!;
        double floor = SigmaFloor(m2, nArr);
        double c = Math.Cos(rot), s = Math.Sin(rot), cx = w / 2.0, cy = h / 2.0;
        int warm = _warmUp; double k = _sigma;
        long rejected = 0;
        Parallel.For(0, h, y => {
            long rowRejected = 0;
            for (int x = 0; x < w; x++) {
                double sx = c * (x - cx) - s * (y - cy) + cx + dx, sy = s * (x - cx) + c * (y - cy) + cy + dy;
                int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy);
                if (x0 < 0 || y0 < 0 || x0 >= w - 1 || y0 >= h - 1) { continue; }
                double fx = sx - x0, fy = sy - y0;
                int i0 = y0 * w + x0;
                double v = (pixels[i0] * (1 - fx) + pixels[i0 + 1] * fx) * (1 - fy)
                         + (pixels[i0 + w] * (1 - fx) + pixels[i0 + w + 1] * fx) * fy;
                int i = y * w + x;
                int n = nArr[i];
                if (n == ushort.MaxValue) { continue; } // at the cap: mean unchanged, not counted as rejected
                if (n >= warm) {
                    double sigmaPx = Math.Sqrt(m2[i] / (n - 1));
                    if (Math.Abs(v - mean[i]) > k * Math.Max(sigmaPx, floor)) { rowRejected++; continue; }
                }
                n++;
                double delta = v - mean[i];
                double newMean = mean[i] + delta / n;
                m2[i] += (float)(delta * (v - newMean));
                mean[i] = (float)newMean;
                nArr[i] = (ushort)n;
            }
            if (rowRejected != 0) { Interlocked.Add(ref rejected, rowRejected); }
        });
        _rejected += rejected;
    }

    // Median per-pixel sigma over a regular grid of about 4096 pixels with n >= 2 (so a pixel whose few early samples
    // agree by luck cannot clip everything after it). No floor when fewer than MinFloorSamples pixels qualify.
    internal static double SigmaFloor(float[] m2, ushort[] n) {
        int step = Math.Max(1, n.Length / FloorSampleCount);
        var sample = new List<float>(FloorSampleCount + 1);
        for (int i = 0; i < n.Length; i += step) {
            if (n[i] >= 2) { sample.Add((float)Math.Sqrt(m2[i] / (n[i] - 1))); }
        }
        if (sample.Count < MinFloorSamples) { return 0; }
        sample.Sort();
        return sample[sample.Count / 2];
    }
}
