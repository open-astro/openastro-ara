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
using System.Globalization;
using System.IO;
using System.Linq;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// #1765 — the drives erased through ARA, by filesystem UUID and time, one line each in
/// <c>{profileDir}/erased-drives.log</c>. A copy marker that names a drive erased after the
/// copy vouches for nothing. The log lives on the rig, not on a drive, so it also covers
/// stores that were unplugged when the erase ran.
/// </summary>
public sealed class StorageEraseLog(string profileDir) {
    internal const string FileName = "erased-drives.log";

    private readonly string path = Path.Combine(profileDir, FileName);
    private readonly object gate = new();

    /// <summary>Throws on an I/O failure: an erase that cannot be recorded must not run.</summary>
    public void Record(string uuid, DateTime utc) {
        lock (gate) {
            Directory.CreateDirectory(profileDir);
            File.AppendAllText(path, $"{uuid.ToUpperInvariant()} {utc.ToUniversalTime():O}\n");
        }
    }

    /// <summary>True when <paramref name="uuid"/> was erased at or after <paramref name="utc"/>,
    /// or when the log cannot be read (the count errs toward "not copied").</summary>
    public bool ErasedSince(string uuid, DateTime utc) {
        string[] lines;
        lock (gate) {
            if (!File.Exists(path)) {
                return false;
            }
            try {
                lines = File.ReadAllLines(path);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                return true;
            }
        }
        return lines.Select(l => l.Split(' ', 2)).Any(p => p.Length == 2
            && string.Equals(p[0], uuid, StringComparison.OrdinalIgnoreCase)
            && DateTime.TryParse(p[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            && at.ToUniversalTime() >= utc);
    }
}
