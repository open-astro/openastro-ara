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

namespace OpenAstroAra.Server.Services;

/// <summary>#1267 item 5 — canonicalises a saved-targets list: names trimmed and bounded, one entry per
/// name (case-insensitive, the newest kept), finite J2000 positions in range, a known source token,
/// aliases trimmed and bounded, the list sorted newest first and capped. Pure.</summary>
public static class SavedTargetsValidator {

    public const int MaxTargets = 200;
    public const int MaxNameLength = 128;
    public const int MaxAliases = 32;
    private static readonly string[] Sources = ["typed", "index", "cache", "simbad"];

    /// <summary>The canonical list, or an error for a body the client should not have sent. A null body
    /// or list normalises to empty; a malformed entry (blank name, bad position) is an error on a PUT and
    /// is dropped when a stored file is read back (<see cref="NormalizeLenient"/>).</summary>
    public static (SavedTargetsDto? Normalized, string? Error) Normalize(SavedTargetsDto? body) {
        var list = (IReadOnlyList<SavedTargetDto>?)body?.Targets;
        if (list is null || list.Count == 0) {
            return (SavedTargetsDto.Empty, null);
        }
        if (list.Count > MaxTargets) {
            return (null, $"at most {MaxTargets} saved targets");
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clean = new List<SavedTargetDto>(list.Count);
        // Newest first, then by name: a deterministic order the client reproduces, so equal-time
        // entries never read as a difference to push again.
        foreach (var t in list.OrderByDescending(t => t?.SavedUtc ?? DateTimeOffset.MinValue)
                .ThenBy(t => (t?.Name ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase)) {
            if (t is null) {
                return (null, "a saved target is null");
            }
            var name = (t.Name ?? string.Empty).Trim();
            if (name.Length == 0) {
                return (null, "a saved target has no name");
            }
            if (name.Length > MaxNameLength || name.Any(char.IsControl)) {
                return (null, $"a saved target name must be at most {MaxNameLength} characters with no control characters");
            }
            if (!double.IsFinite(t.RaDeg) || !double.IsFinite(t.DecDeg) || t.DecDeg is < -90 or > 90) {
                return (null, $"'{name}' has no finite J2000 position in range");
            }
            var source = (t.Source ?? string.Empty).Trim().ToLowerInvariant();
            if (!Sources.Contains(source)) {
                return (null, $"'{name}' has an unknown source '{source}' (typed, index, cache or simbad)");
            }
            if (!seen.Add(name)) {
                continue; // an older entry under the same name: the newest wins
            }
            var aliases = ((IReadOnlyList<string>?)t.Aliases ?? [])
                .Where(a => a is not null)
                .Select(a => a.Trim())
                .Where(a => a.Length > 0 && a.Length <= MaxNameLength && !a.Any(char.IsControl))
                .Distinct(StringComparer.Ordinal)
                .Take(MaxAliases)
                .ToArray();
            clean.Add(new SavedTargetDto(name, aliases, ((t.RaDeg % 360.0) + 360.0) % 360.0, t.DecDeg, source, t.SavedUtc, t.TypedAsJNow));
        }
        return (new SavedTargetsDto(clean), null);
    }

    /// <summary>For a stored file: malformed entries are dropped instead of failing the whole profile.</summary>
    public static SavedTargetsDto NormalizeLenient(SavedTargetsDto? body) {
        var list = (IReadOnlyList<SavedTargetDto>?)body?.Targets;
        if (list is null) {
            return SavedTargetsDto.Empty;
        }
        var kept = new List<SavedTargetDto>();
        foreach (var t in list) {
            if (Normalize(new SavedTargetsDto([t])).Normalized is { Targets.Count: 1 } one) {
                kept.Add(one.Targets[0]);
            }
        }
        return Normalize(new SavedTargetsDto(kept)).Normalized ?? SavedTargetsDto.Empty;
    }
}
