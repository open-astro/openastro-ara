#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging.Abstractions;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenAstroAra.Server.Services;

/// <summary>One object the offline index knows: its main designation, every other name it answers to,
/// and its J2000 position.</summary>
public sealed record TargetIndexEntry(string MainId, IReadOnlyList<string> Aliases, double RaDeg, double DecDeg,
    string? ObjectType, double? Magnitude);

/// <summary>
/// #1267 — the offline name resolver (playbook §36.8): a name index over the sky-data catalogs the Data
/// Manager has installed, so a target can be found by any of the identifiers those files already carry
/// but nothing indexed: HYG's HIP / HD / HR / Gliese / Bayer / Flamsteed / proper / variable-star columns
/// ("AB Cas" is HYG's <c>var=AB con=Cas</c>) and OpenNGC's Messier / NGC / IC / Identifiers / Common names
/// columns. Built lazily on the first lookup from the files on disk and rebuilt when they change (the same
/// write-time signature <see cref="SkyCatalogService"/> uses), so a catalog installed mid-session is
/// searchable without a restart. No internet: this is what answers at the dark site.
/// </summary>
public sealed partial class TargetNameIndex {

    /// <summary>The OpenNGC-layout packages the index reads, besides <c>hyg-stars</c>.</summary>
    private static readonly string[] OpenNgcPackages = SkyCatalogReader.OpenNgcLayoutPackages;

    private readonly string _skyDataRoot;
    private readonly ILogger<TargetNameIndex> _logger;
    private readonly object _buildLock = new();
    private Built? _built;

    private sealed record Built(long Signature, Dictionary<string, TargetIndexEntry> Tight, Dictionary<string, TargetIndexEntry> Loose, int Entries);

    public TargetNameIndex(string skyDataRoot, ILogger<TargetNameIndex>? logger = null) {
        _skyDataRoot = skyDataRoot ?? throw new ArgumentNullException(nameof(skyDataRoot));
        _logger = logger ?? NullLogger<TargetNameIndex>.Instance;
    }

    /// <summary>How many objects the current index holds (0 before the first lookup or with nothing installed).</summary>
    public int Count => _built?.Entries ?? 0;

    /// <summary>The entry a name resolves to, or null. Case, spacing, dashes and leading zeros do not matter
    /// ("ngc0224", "NGC 224", "M031", "m 31", "V* AB Cas", "ab cas", "α Cas", "alpha Cas" all hit).</summary>
    public TargetIndexEntry? Find(string name, CancellationToken ct) {
        var built = EnsureBuilt(ct);
        if (built is null) {
            return null;
        }
        var tight = TightKey(name);
        if (tight.Length == 0) {
            return null;
        }
        if (built.Tight.TryGetValue(tight, out var hit)) {
            return hit;
        }
        var loose = LooseKey(tight);
        return built.Loose.TryGetValue(loose, out hit) ? hit : null;
    }

    // ---- keys ----

    /// <summary>Upper-case, no whitespace or underscores, Simbad's "V*" / "*" prefixes dropped, Greek letters
    /// and their spelled-out / HYG three-letter / Simbad three-letter forms unified, leading zeros after a
    /// letter prefix dropped. Dashes stay ("WR 2-1" and "WR 21" are different stars).</summary>
    public static string TightKey(string? name) {
        var s = (name ?? string.Empty).Trim();
        if (s.StartsWith("V*", StringComparison.OrdinalIgnoreCase)) {
            s = s[2..].Trim();
        } else if (s.StartsWith("* ", StringComparison.Ordinal)) {
            s = s[2..].Trim();
        }
        s = GreekToAbbrev(s.ToUpperInvariant());
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) {
            if (!char.IsWhiteSpace(c) && c != '_') {
                sb.Append(c);
            }
        }
        return LeadingZeros().Replace(sb.ToString(), "$1$2");
    }

    /// <summary>The tight key without dashes, for the second lookup.</summary>
    public static string LooseKey(string tightKey) => tightKey.Replace("-", string.Empty, StringComparison.Ordinal);

    [GeneratedRegex(@"([A-Z])0+(\d)")]
    private static partial Regex LeadingZeros();

    // HYG's three-letter Bayer abbreviations, the full Greek names and the Unicode letters all map to the
    // abbreviation, so "alp Cas", "alpha Cas", "α Cas" and "Alp Cas" share one key. Simbad spells a few
    // differently ("alf", "tet", "ksi"); those map to HYG's form.
    private static readonly (string Simbad, string Abbrev)[] SimbadGreek = { ("ALF", "ALP"), ("TET", "THE"), ("KSI", "XI") };

    private static readonly (string Abbrev, string Full, char Letter)[] Greek = {
        ("ALP", "ALPHA", 'Α'), ("BET", "BETA", 'Β'), ("GAM", "GAMMA", 'Γ'), ("DEL", "DELTA", 'Δ'), ("EPS", "EPSILON", 'Ε'),
        ("ZET", "ZETA", 'Ζ'), ("ETA", "ETA", 'Η'), ("THE", "THETA", 'Θ'), ("IOT", "IOTA", 'Ι'), ("KAP", "KAPPA", 'Κ'),
        ("LAM", "LAMBDA", 'Λ'), ("MU", "MU", 'Μ'), ("NU", "NU", 'Ν'), ("XI", "XI", 'Ξ'), ("OMI", "OMICRON", 'Ο'),
        ("PI", "PI", 'Π'), ("RHO", "RHO", 'Ρ'), ("SIG", "SIGMA", 'Σ'), ("TAU", "TAU", 'Τ'), ("UPS", "UPSILON", 'Υ'),
        ("PHI", "PHI", 'Φ'), ("CHI", "CHI", 'Χ'), ("PSI", "PSI", 'Ψ'), ("OME", "OMEGA", 'Ω'),
    };

    private static string GreekToAbbrev(string upper) {
        // A Greek letter anywhere (upper-cased Unicode) → abbreviation; a leading spelled-out name → abbreviation.
        var sb = new StringBuilder(upper.Length);
        foreach (var c in upper) {
            var replaced = false;
            foreach (var (abbrev, _, letter) in Greek) {
                if (c == letter) {
                    sb.Append(abbrev);
                    replaced = true;
                    break;
                }
            }
            if (!replaced) {
                sb.Append(c);
            }
        }
        var s = sb.ToString();
        foreach (var (abbrev, full, _) in Greek) {
            if (full.Length > abbrev.Length && s.StartsWith(full, StringComparison.Ordinal)
                    && (s.Length == full.Length || !char.IsLetter(s[full.Length]))) {
                return abbrev + s[full.Length..];
            }
        }
        foreach (var (simbad, abbrev) in SimbadGreek) {
            if (s.StartsWith(simbad, StringComparison.Ordinal) && (s.Length == simbad.Length || !char.IsLetter(s[simbad.Length]))) {
                return abbrev + s[simbad.Length..];
            }
        }
        return s;
    }

    // ---- build ----

    private Built? EnsureBuilt(CancellationToken ct) {
        var signature = Signature();
        var current = Volatile.Read(ref _built);
        if (current is not null && current.Signature == signature) {
            return current;
        }
        lock (_buildLock) {
            current = Volatile.Read(ref _built);
            if (current is not null && current.Signature == signature) {
                return current;
            }
            var built = Build(signature, ct);
            Volatile.Write(ref _built, built);
            return built;
        }
    }

    private IEnumerable<(string Package, string Path)> Files() {
        var hyg = Path.Combine(_skyDataRoot, "hyg-stars", "catalog.csv");
        if (File.Exists(hyg)) {
            yield return ("hyg-stars", hyg);
        }
        foreach (var id in OpenNgcPackages) {
            var path = Path.Combine(_skyDataRoot, id, "catalog.csv");
            if (File.Exists(path)) {
                yield return (id, path);
            }
        }
    }

    private long Signature() {
        long sig = 17;
        foreach (var (package, path) in Files()) {
            sig = unchecked(sig * 31 + package.GetHashCode(StringComparison.Ordinal));
            sig = unchecked(sig * 31 + File.GetLastWriteTimeUtc(path).Ticks);
        }
        return sig;
    }

    private Built Build(long signature, CancellationToken ct) {
        var tight = new Dictionary<string, TargetIndexEntry>(StringComparer.Ordinal);
        var loose = new Dictionary<string, TargetIndexEntry>(StringComparer.Ordinal);
        var entries = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var (package, path) in Files()) {
            try {
                using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                entries += package == "hyg-stars"
                    ? ReadHyg(reader, tight, loose, ct)
                    : ReadOpenNgc(reader, tight, loose, ct);
            } catch (Exception ex) when (ex is IOException or FormatException or InvalidDataException) {
                LogFileSkipped(ex, package);
            }
        }
        LogBuilt(entries, tight.Count, sw.ElapsedMilliseconds);
        return new Built(signature, tight, loose, entries);
    }

    private static void Add(Dictionary<string, TargetIndexEntry> tight, Dictionary<string, TargetIndexEntry> loose,
            TargetIndexEntry entry) {
        foreach (var alias in entry.Aliases) {
            var t = TightKey(alias);
            if (t.Length == 0) {
                continue;
            }
            tight.TryAdd(t, entry); // first catalog row wins a contested key (HYG before the DSO files)
            loose.TryAdd(LooseKey(t), entry);
        }
    }

    private static int ReadHyg(TextReader reader, Dictionary<string, TargetIndexEntry> tight,
            Dictionary<string, TargetIndexEntry> loose, CancellationToken ct) {
        var header = reader.ReadLine();
        if (header is null) {
            return 0;
        }
        var sb = new StringBuilder();
        var cols = Columns(SkyCatalogReader.SplitCsv(header, ',', sb));
        int hip = cols.GetValueOrDefault("hip", -1), hd = cols.GetValueOrDefault("hd", -1), hr = cols.GetValueOrDefault("hr", -1);
        int gl = cols.GetValueOrDefault("gl", -1), proper = cols.GetValueOrDefault("proper", -1);
        int ra = cols.GetValueOrDefault("ra", -1), dec = cols.GetValueOrDefault("dec", -1), mag = cols.GetValueOrDefault("mag", -1);
        int bayer = cols.GetValueOrDefault("bayer", -1), flam = cols.GetValueOrDefault("flam", -1), con = cols.GetValueOrDefault("con", -1);
        int variable = cols.GetValueOrDefault("var", -1), dist = cols.GetValueOrDefault("dist", -1);
        if (ra < 0 || dec < 0 || hip < 0) {
            return 0;
        }
        var count = 0;
        string? line;
        var aliases = new List<string>(8);
        while ((line = reader.ReadLine()) is not null) {
            ct.ThrowIfCancellationRequested();
            var f = SkyCatalogReader.SplitCsv(line, ',', sb);
            if (!Num(f, ra, out var raHours) || !Num(f, dec, out var decDeg) || raHours < 0 || raHours >= 24 || decDeg is < -90 or > 90) {
                continue;
            }
            var p = Field(f, proper);
            // HYG's first data row is the Sun (no HIP, proper "Sol", distance 0, RA 0 Dec 0): a moving body, not
            // a fixed star; the planetarium owns it (review r2).
            if (Field(f, hip).Length == 0 && (string.Equals(p, "Sol", StringComparison.OrdinalIgnoreCase) || (Num(f, dist, out var dd) && dd == 0))) {
                continue;
            }
            aliases.Clear();
            var c = Field(f, con);
            if (p.Length > 0) aliases.Add(p);
            var v = Field(f, variable);
            if (v.Length > 0 && c.Length > 0) aliases.Add($"{v} {c}");
            var b = Field(f, bayer);
            if (b.Length > 0 && c.Length > 0) aliases.Add($"{b} {c}");
            var fl = Field(f, flam);
            if (fl.Length > 0 && c.Length > 0) aliases.Add($"{fl} {c}");
            var h = Field(f, hip);
            if (h.Length > 0) aliases.Add($"HIP {h}");
            var d = Field(f, hd);
            if (d.Length > 0) aliases.Add($"HD {d}");
            var r = Field(f, hr);
            if (r.Length > 0) aliases.Add($"HR {r}");
            var g = Field(f, gl);
            if (g.Length > 0) {
                aliases.Add(g);
                if (g.StartsWith("GJ ", StringComparison.Ordinal) || g.StartsWith("Gl ", StringComparison.Ordinal)) {
                    aliases.Add("Gliese " + g[3..]);
                }
            }
            if (aliases.Count == 0) {
                continue;
            }
            var entry = new TargetIndexEntry(aliases[0], aliases.ToArray(), (raHours * 15.0) % 360.0, decDeg,
                v.Length > 0 ? "V*" : "*", Num(f, mag, out var m) ? m : null);
            Add(tight, loose, entry);
            count++;
        }
        return count;
    }

    private static int ReadOpenNgc(TextReader reader, Dictionary<string, TargetIndexEntry> tight,
            Dictionary<string, TargetIndexEntry> loose, CancellationToken ct) {
        var header = reader.ReadLine();
        if (header is null) {
            return 0;
        }
        var cols = Columns(header.Split(';'));
        int name = cols.GetValueOrDefault("Name", -1), type = cols.GetValueOrDefault("Type", -1);
        int ra = cols.GetValueOrDefault("RA", -1), dec = cols.GetValueOrDefault("Dec", -1);
        int vMag = cols.GetValueOrDefault("V-Mag", -1), bMag = cols.GetValueOrDefault("B-Mag", -1);
        int messier = cols.GetValueOrDefault("M", -1), ngc = cols.GetValueOrDefault("NGC", -1), ic = cols.GetValueOrDefault("IC", -1);
        int ids = cols.GetValueOrDefault("Identifiers", -1), common = cols.GetValueOrDefault("Common names", -1);
        if (name < 0 || ra < 0 || dec < 0) {
            return 0;
        }
        var count = 0;
        string? line;
        var aliases = new List<string>(8);
        while ((line = reader.ReadLine()) is not null) {
            ct.ThrowIfCancellationRequested();
            var f = line.Split(';');
            var t = Field(f, type);
            if (t is "Dup" or "NonEx") {
                continue;
            }
            if (!SkyCatalogReader.TryParseSexagesimalHours(Field(f, ra), out var raHours)
                    || !SkyCatalogReader.TryParseSexagesimalDegrees(Field(f, dec), out var decDeg)) {
                continue;
            }
            aliases.Clear();
            var n = Field(f, name);
            if (n.Length == 0) {
                continue;
            }
            aliases.Add(n);
            var m = Field(f, messier);
            if (m.Length > 0) aliases.Add($"M {m.TrimStart('0')}");
            var g = Field(f, ngc);
            if (g.Length > 0 && !n.StartsWith("NGC", StringComparison.Ordinal)) aliases.Add($"NGC {g}");
            var i = Field(f, ic);
            if (i.Length > 0 && !n.StartsWith("IC", StringComparison.Ordinal)) aliases.Add($"IC {i}");
            foreach (var id in Field(f, ids).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
                aliases.Add(id);
            }
            foreach (var cn in Field(f, common).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
                aliases.Add(cn);
            }
            double? mag = Num(f, vMag, out var mv) ? mv : Num(f, bMag, out var mb) ? mb : null;
            var entry = new TargetIndexEntry(n, aliases.ToArray(), (raHours * 15.0) % 360.0, decDeg, t.Length > 0 ? t : null, mag);
            Add(tight, loose, entry);
            count++;
        }
        return count;
    }

    private static Dictionary<string, int> Columns(string[] headers) {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < headers.Length; i++) {
            map.TryAdd(headers[i].Trim().Trim('"'), i);
        }
        return map;
    }

    private static string Field(string[] f, int i) => i >= 0 && i < f.Length ? f[i].Trim().Trim('"').Trim() : string.Empty;

    private static bool Num(string[] f, int i, out double value) =>
        double.TryParse(Field(f, i), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    [LoggerMessage(Level = LogLevel.Information, Message = "Target name index built: {Entries} objects, {Keys} names, {Ms} ms (#1267)")]
    private partial void LogBuilt(int entries, int keys, long ms);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Target name index skipped package {Package}: unreadable catalog (#1267)")]
    private partial void LogFileSkipped(Exception ex, string package);
}
