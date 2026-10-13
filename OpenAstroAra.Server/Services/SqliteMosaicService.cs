#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Data.Sqlite;
using OpenAstroAra.Server.Contracts;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §47 / #1146 — mosaic projects in the catalog (replaces <c>PlaceholderMosaicService</c>). The plan
/// (centre, grid, overlap, PA, frames owed per panel) and the per-panel J2000 centres are stored;
/// <b>completion is derived</b>, never stored: a panel's light frames are the catalog rows whose
/// <c>target_name</c> is the panel's target block, which the client names
/// <c>"&lt;name&gt; i/N"</c> (row-major, 1-based — <c>create_imaging_run.dart</c>). So "panels 1–3 done,
/// 4–6 owed" is true after a restart, after a second night on the same project, and after frames are
/// deleted from the Library, with nothing to keep in sync.
/// </summary>
public sealed class SqliteMosaicService : IMosaicService {

    /// <summary>Default light frames a panel owes when the client does not say (the old placeholder's figure).</summary>
    public const int DefaultFramesPerPanel = 20;

    private readonly IAraDatabase _db;
    private readonly IProfileStore? _profile;
    private readonly Func<DateTimeOffset> _clock;

    public SqliteMosaicService(IAraDatabase db, IProfileStore? profile = null, Func<DateTimeOffset>? clock = null) {
        _db = db;
        _profile = profile;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    private sealed record Row(Guid Id, string Name, double Ra, double Dec, int X, int Y, double Overlap, double Pa,
        int FramesPerPanel, DateTimeOffset Created, Guid? SequenceId) {
        public MosaicDto ToDto() => new(Id, Name, Ra, Dec, X, Y, Overlap, Pa, X * Y, Created, SequenceId);
    }

    public async Task<CursorPage<MosaicDto>> ListAsync(int limit, string? cursor, CancellationToken ct) {
        var offset = 0;
        if (!string.IsNullOrEmpty(cursor) && int.TryParse(cursor, out var parsed) && parsed >= 0) {
            offset = parsed;
        }
        var pageSize = Math.Clamp(limit, 1, 200);
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectColumns + " FROM mosaics ORDER BY created_at DESC, id LIMIT $limit OFFSET $offset;";
        cmd.Parameters.AddWithValue("$limit", pageSize + 1);
        cmd.Parameters.AddWithValue("$offset", offset);
        var items = new List<MosaicDto>(pageSize);
        var hasMore = false;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) {
            if (items.Count == pageSize) {
                hasMore = true;
                break;
            }
            items.Add(ReadRow(reader).ToDto());
        }
        return new CursorPage<MosaicDto>(items, NextCursor: hasMore ? (offset + pageSize).ToString() : null, HasMore: hasMore);
    }

    public async Task<MosaicDto?> GetAsync(Guid id, CancellationToken ct) {
        await using var conn = _db.OpenConnection();
        return (await GetRowAsync(conn, id, ct))?.ToDto();
    }

    public async Task<MosaicDto> CreateAsync(MosaicCreateRequestDto request, string? idempotencyKey, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        var name = string.IsNullOrWhiteSpace(request.Name) ? "Mosaic" : request.Name.Trim();
        var x = Math.Max(1, request.PanelCountX);
        var y = Math.Max(1, request.PanelCountY);
        var overlap = Math.Clamp(request.OverlapPercent, 0, 90);
        var pa = ((request.PositionAngleDegrees ?? 0.0) % 360 + 360) % 360;
        var framesPerPanel = request.FramesPerPanel is > 0 ? request.FramesPerPanel.Value : DefaultFramesPerPanel;
        var id = Guid.NewGuid();
        var now = _clock();
        var centres = ResolvePanelCentres(request, x, y, overlap, pa);
        var fovW = FieldWidthDegrees(_profile?.GetOpticsSettings());

        await using var conn = _db.OpenConnection();
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = conn.CreateCommand()) {
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = """
                INSERT INTO mosaics (id, name, center_ra_deg, center_dec_deg, panels_x, panels_y, overlap_percent,
                                     position_angle_deg, frames_per_panel, created_at, generated_sequence_id)
                VALUES ($id, $name, $ra, $dec, $x, $y, $ov, $pa, $fpp, $created, NULL);
                """;
            cmd.Parameters.AddWithValue("$id", id.ToString());
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$ra", request.CenterRaDegrees);
            cmd.Parameters.AddWithValue("$dec", request.CenterDecDegrees);
            cmd.Parameters.AddWithValue("$x", x);
            cmd.Parameters.AddWithValue("$y", y);
            cmd.Parameters.AddWithValue("$ov", overlap);
            cmd.Parameters.AddWithValue("$pa", pa);
            cmd.Parameters.AddWithValue("$fpp", framesPerPanel);
            cmd.Parameters.AddWithValue("$created", now.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        for (var i = 0; i < centres.Count; i++) {
            var (ra, dec) = centres[i];
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = """
                INSERT INTO mosaic_panels (mosaic_id, panel_index, panel_x, panel_y, center_ra_deg, center_dec_deg, crosses_ra_wrap)
                VALUES ($id, $i, $px, $py, $ra, $dec, $wrap);
                """;
            cmd.Parameters.AddWithValue("$id", id.ToString());
            cmd.Parameters.AddWithValue("$i", i);
            cmd.Parameters.AddWithValue("$px", i % x);
            cmd.Parameters.AddWithValue("$py", i / x);
            cmd.Parameters.AddWithValue("$ra", ra);
            cmd.Parameters.AddWithValue("$dec", dec);
            cmd.Parameters.AddWithValue("$wrap", CrossesRaWrap(ra, dec, fovW) ? 1 : 0);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return new Row(id, name, request.CenterRaDegrees, request.CenterDecDegrees, x, y, overlap, pa, framesPerPanel, now, null).ToDto();
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct) {
        await using var conn = _db.OpenConnection();
        // Explicit, not via ON DELETE CASCADE: foreign-key enforcement is a per-connection pragma.
        await using (var panels = conn.CreateCommand()) {
            panels.CommandText = "DELETE FROM mosaic_panels WHERE mosaic_id = $id;";
            panels.Parameters.AddWithValue("$id", id.ToString());
            await panels.ExecuteNonQueryAsync(ct);
        }
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM mosaics WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<IReadOnlyList<MosaicPanelDto>> GetPanelsAsync(Guid mosaicId, CancellationToken ct) {
        await using var conn = _db.OpenConnection();
        var row = await GetRowAsync(conn, mosaicId, ct);
        if (row is null) {
            return Array.Empty<MosaicPanelDto>();
        }
        var captured = await PanelFramesAsync(conn, row, ct);
        var panels = new List<MosaicPanelDto>(row.X * row.Y);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT panel_index, panel_x, panel_y, center_ra_deg, center_dec_deg, crosses_ra_wrap
            FROM mosaic_panels WHERE mosaic_id = $id ORDER BY panel_index;
            """;
        cmd.Parameters.AddWithValue("$id", mosaicId.ToString());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) {
            var index = reader.GetInt32(0);
            captured.TryGetValue(index, out var frames);
            panels.Add(new MosaicPanelDto(
                MosaicId: mosaicId,
                PanelIndex: index,
                PanelX: reader.GetInt32(1),
                PanelY: reader.GetInt32(2),
                CenterRaDegrees: reader.GetDouble(3),
                CenterDecDegrees: reader.GetDouble(4),
                CrossesRaWrap: reader.GetInt32(5) != 0,
                Status: StatusFor(frames.Count, row.FramesPerPanel),
                TargetFrameId: frames.LatestFrameId,
                FramesCaptured: frames.Count));
        }
        return panels;
    }

    public async Task<MosaicProgressDto?> GetProgressAsync(Guid mosaicId, CancellationToken ct) {
        await using var conn = _db.OpenConnection();
        var row = await GetRowAsync(conn, mosaicId, ct);
        if (row is null) {
            return null;
        }
        var captured = await PanelFramesAsync(conn, row, ct);
        var total = row.X * row.Y;
        var completed = 0;
        var framesTotal = 0;
        int? current = null;
        DateTimeOffset? currentStarted = null;
        for (var i = 0; i < total; i++) {
            captured.TryGetValue(i, out var frames);
            framesTotal += frames.Count;
            if (frames.Count >= row.FramesPerPanel) {
                completed++;
            } else if (frames.Count > 0 && current is null) {
                // The lowest-numbered panel that has started but is not done is the one being worked.
                current = i;
                currentStarted = frames.FirstCapturedUtc;
            }
        }
        return new MosaicProgressDto(mosaicId, completed, total, current, currentStarted, row.FramesPerPanel, framesTotal);
    }

    // ── panel ↔ frame derivation ──

    /// <summary>The target block the client names for panel <paramref name="index"/> (0-based) of a
    /// mosaic: <c>"&lt;name&gt; i/N"</c> with i 1-based. One place, so the server and the client agree.</summary>
    public static string PanelTargetName(string mosaicName, int index, int total) => $"{mosaicName} {index + 1}/{total}";

    internal readonly record struct PanelFrames(int Count, Guid? LatestFrameId, DateTimeOffset? FirstCapturedUtc);

    private static async Task<Dictionary<int, PanelFrames>> PanelFramesAsync(SqliteConnection conn, Row row, CancellationToken ct) {
        var total = row.X * row.Y;
        var result = new Dictionary<int, PanelFrames>(total);
        // One query for the whole project: every light frame whose target is "<name> <k>/<N>".
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT target_name, COUNT(*), MIN(captured_utc),
                   (SELECT f2.id FROM frames f2
                     WHERE f2.target_name = f.target_name AND f2.frame_type = 'light'
                     ORDER BY f2.captured_utc DESC LIMIT 1)
            FROM frames f
            WHERE f.frame_type = 'light' AND f.target_name LIKE $pattern ESCAPE '\'
            GROUP BY target_name;
            """;
        cmd.Parameters.AddWithValue("$pattern", EscapeLike(row.Name) + " %/" + total.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) {
            var target = reader.GetString(0);
            if (!TryPanelIndex(target, row.Name, total, out var index)) {
                continue; // "<name> 12/6" or a stray suffix: not one of ours
            }
            DateTimeOffset? first = await reader.IsDBNullAsync(2, ct) ? null : DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture);
            Guid? latest = await reader.IsDBNullAsync(3, ct) ? null : Guid.TryParse(reader.GetString(3), out var g) ? g : null;
            result[index] = new PanelFrames(reader.GetInt32(1), latest, first);
        }
        return result;
    }

    /// <summary>Inverse of <see cref="PanelTargetName"/>: the 0-based panel index of a frame's target
    /// name, or false when the name is not "<paramref name="mosaicName"/> k/<paramref name="total"/>"
    /// with 1 ≤ k ≤ total.</summary>
    internal static bool TryPanelIndex(string targetName, string mosaicName, int total, out int index) {
        index = -1;
        var prefix = mosaicName + " ";
        var suffix = "/" + total.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!targetName.StartsWith(prefix, StringComparison.Ordinal) || !targetName.EndsWith(suffix, StringComparison.Ordinal)) {
            return false;
        }
        var middle = targetName.AsSpan(prefix.Length, targetName.Length - prefix.Length - suffix.Length);
        if (!int.TryParse(middle, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var k) || k < 1 || k > total) {
            return false;
        }
        index = k - 1;
        return true;
    }

    private static string EscapeLike(string s) => s.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    internal static string StatusFor(int captured, int owed) =>
        captured >= Math.Max(1, owed) ? "done" : captured > 0 ? "in_progress" : "pending";

    // ── geometry ──

    /// <summary>The client's drawn centres when it sent the right number, else a grid spread from the
    /// profile optics (FOV × (1 − overlap), rotated by the PA, tangent-plane deprojected), else every
    /// panel at the centre (no optics known — the plan still exists, the geometry is unknown).</summary>
    internal List<(double Ra, double Dec)> ResolvePanelCentres(MosaicCreateRequestDto request, int x, int y, double overlapPercent, double paDeg) {
        if (request.Panels is { } supplied && supplied.Count == x * y) {
            return supplied.Select(p => (Normalize360(p.RaDegrees), Math.Clamp(p.DecDegrees, -90, 90))).ToList();
        }
        var optics = _profile?.GetOpticsSettings();
        var fovW = FieldWidthDegrees(optics);
        var fovH = FieldHeightDegrees(optics);
        return GridCentres(request.CenterRaDegrees, request.CenterDecDegrees, x, y, overlapPercent, paDeg, fovW, fovH);
    }

    /// <summary>Row-major grid of tangent-plane offsets around the centre, the same construction the
    /// planetarium uses to draw the overlay (<c>index.html</c> addFrameToSequence), deprojected to RA/Dec.
    /// A zero field (no optics) puts every panel at the centre.</summary>
    internal static List<(double Ra, double Dec)> GridCentres(double raDeg, double decDeg, int x, int y, double overlapPercent, double paDeg, double fovWDeg, double fovHDeg) {
        var centres = new List<(double, double)>(x * y);
        var ov = Math.Clamp(overlapPercent, 0, 90) / 100.0;
        var stepW = fovWDeg * (1 - ov);
        var stepH = fovHDeg * (1 - ov);
        var th = paDeg * Math.PI / 180.0;
        var (cosT, sinT) = (Math.Cos(th), Math.Sin(th));
        for (var iy = 0; iy < y; iy++) {
            for (var ix = 0; ix < x; ix++) {
                var gx = (ix - (x - 1) / 2.0) * stepW;
                var gy = (iy - (y - 1) / 2.0) * stepH;
                var dx = gx * cosT - gy * sinT;
                var dy = gx * sinT + gy * cosT;
                centres.Add(TanToRaDec(dx, dy, raDeg, decDeg));
            }
        }
        return centres;
    }

    /// <summary>Gnomonic (tangent-plane) deprojection: offsets in degrees (east, north) at the tangent point → RA/Dec.</summary>
    internal static (double Ra, double Dec) TanToRaDec(double dxDeg, double dyDeg, double ra0Deg, double dec0Deg) {
        if (dxDeg == 0 && dyDeg == 0) {
            return (Normalize360(ra0Deg), dec0Deg);
        }
        var d2r = Math.PI / 180.0;
        var xi = dxDeg * d2r;
        var eta = dyDeg * d2r;
        var ra0 = ra0Deg * d2r;
        var dec0 = dec0Deg * d2r;
        var denom = Math.Cos(dec0) - eta * Math.Sin(dec0);
        var ra = ra0 + Math.Atan2(xi, denom);
        var dec = Math.Atan2(Math.Sin(dec0) + eta * Math.Cos(dec0), Math.Sqrt(xi * xi + denom * denom));
        return (Normalize360(ra / d2r), dec / d2r);
    }

    internal static double FieldWidthDegrees(OpticsSettingsDto? optics) => FieldDegrees(optics, optics?.SensorWidthPx ?? 0);
    internal static double FieldHeightDegrees(OpticsSettingsDto? optics) => FieldDegrees(optics, optics?.SensorHeightPx ?? 0);

    private static double FieldDegrees(OpticsSettingsDto? optics, int pixels) {
        if (optics is null || optics.FocalLengthMm <= 0 || optics.PixelSizeUm <= 0 || pixels <= 0) {
            return 0;
        }
        var focal = optics.FocalLengthMm * (optics.ReducerFactor > 0 ? optics.ReducerFactor : 1.0);
        var sensorMm = pixels * optics.PixelSizeUm / 1000.0;
        return 2.0 * Math.Atan(sensorMm / (2.0 * focal)) * 180.0 / Math.PI;
    }

    /// <summary>§47.3 — does the panel's RA extent straddle 0h/24h? Needs the field width; unknown → false.</summary>
    internal static bool CrossesRaWrap(double raDeg, double decDeg, double fovWDeg) {
        if (fovWDeg <= 0) {
            return false;
        }
        var cosDec = Math.Cos(decDeg * Math.PI / 180.0);
        if (cosDec < 1e-6) {
            return true; // at the pole every panel spans all RA
        }
        var halfWidthRa = fovWDeg / 2.0 / cosDec;
        var ra = Normalize360(raDeg);
        return ra - halfWidthRa < 0 || ra + halfWidthRa >= 360;
    }

    private static double Normalize360(double deg) {
        var r = deg % 360.0;
        return r < 0 ? r + 360.0 : r;
    }

    // ── rows ──

    private const string SelectColumns = "SELECT id, name, center_ra_deg, center_dec_deg, panels_x, panels_y, overlap_percent, position_angle_deg, frames_per_panel, created_at, generated_sequence_id";

    private static async Task<Row?> GetRowAsync(SqliteConnection conn, Guid id, CancellationToken ct) {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectColumns + " FROM mosaics WHERE id = $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRow(reader) : null;
    }

    private static Row ReadRow(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.GetDouble(2),
        reader.GetDouble(3),
        reader.GetInt32(4),
        reader.GetInt32(5),
        reader.GetDouble(6),
        reader.GetDouble(7),
        reader.GetInt32(8),
        DateTimeOffset.Parse(reader.GetString(9), System.Globalization.CultureInfo.InvariantCulture),
        reader.IsDBNull(10) ? null : Guid.TryParse(reader.GetString(10), out var seq) ? seq : null);
}
