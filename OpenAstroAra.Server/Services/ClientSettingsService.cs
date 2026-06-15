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
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>§55.1 WILMA settings sync: read/replace the profile's opaque UI-preferences blob.</summary>
public interface IClientSettingsService {
    /// <summary>The stored preferences, or an empty object (and null timestamp) when nothing's been saved yet.</summary>
    Task<ClientSettingsDto> GetAsync(CancellationToken ct);

    /// <summary>Replace the stored preferences wholesale (last-write-wins). Throws <see cref="ArgumentException"/> when
    /// <paramref name="settings"/> isn't a JSON object or exceeds the size cap.</summary>
    Task<ClientSettingsDto> ReplaceAsync(JsonElement settings, CancellationToken ct);
}

/// <summary>
/// File-backed <see cref="IClientSettingsService"/>: one <c>client-settings.json</c> under the profile dir holding the
/// opaque preferences object. Writes are atomic (temp file + rename) and serialized by a semaphore so two devices
/// saving at once can't tear the file; the later write simply wins. The blob is capped so a client can't park an
/// unbounded payload on the daemon.
/// </summary>
public sealed partial class ClientSettingsService : IClientSettingsService, IDisposable {

    internal const string FileName = "client-settings.json";

    // 256 KiB is far above any realistic UI-preferences blob (theme, layout, collapsed panels) but small enough that a
    // misbehaving client can't fill the disk through this path.
    internal const int MaxBytes = 256 * 1024;

    // Coarse HTTP-layer body cap, applied before the request is even buffered/deserialized so a misbehaving client
    // can't force a large allocation ahead of the precise per-object check below. Just above MaxBytes — enough for the
    // {"settings":…} envelope around a near-cap object, without false-rejecting it at the transport layer.
    internal const long MaxRequestBytes = MaxBytes + 1024;

    private readonly string _path;
    private readonly ILogger<ClientSettingsService> _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public ClientSettingsService(string profileDir, ILogger<ClientSettingsService> logger) {
        ArgumentException.ThrowIfNullOrEmpty(profileDir);
        _path = Path.Combine(profileDir, FileName);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ClientSettingsDto> GetAsync(CancellationToken ct) {
        string text;
        DateTimeOffset updated;
        // Read content + timestamp under the same gate that writes hold, so the pair is a consistent snapshot — a
        // concurrent ReplaceAsync can't slip a new timestamp onto the old content (which would mislead a client's
        // "is my state current?" check). Conscious tradeoff: this also serializes concurrent reads, which is fine for
        // the read-once-on-connect access pattern this store targets (not a hot polling path).
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try {
            text = await File.ReadAllTextAsync(_path, ct).ConfigureAwait(false);
            updated = new DateTimeOffset(File.GetLastWriteTimeUtc(_path), TimeSpan.Zero);
        } catch (FileNotFoundException) {
            return new ClientSettingsDto(EmptyObject(), null);
        } catch (DirectoryNotFoundException) {
            return new ClientSettingsDto(EmptyObject(), null);
        } finally {
            _writeGate.Release();
        }

        try {
            using var doc = JsonDocument.Parse(text);
            // A hand-edited file can be valid JSON yet not an object (an array, a bare string). The store's contract is
            // an object — and the PUT path enforces it — so degrade a non-object file to empty rather than handing the
            // client something its `Settings.ValueKind == Object` assumption doesn't expect.
            if (doc.RootElement.ValueKind != JsonValueKind.Object) {
                LogCorruptSettings(new JsonException("Root element is not a JSON object."));
                return new ClientSettingsDto(EmptyObject(), null);
            }
            // Clone so the element outlives the JsonDocument we dispose here.
            return new ClientSettingsDto(doc.RootElement.Clone(), updated);
        } catch (JsonException ex) {
            // A corrupt/hand-edited file shouldn't wedge the client — log it and serve an empty object so a fresh
            // PUT can overwrite it.
            LogCorruptSettings(ex);
            return new ClientSettingsDto(EmptyObject(), null);
        }
    }

    public async Task<ClientSettingsDto> ReplaceAsync(JsonElement settings, CancellationToken ct) {
        if (settings.ValueKind != JsonValueKind.Object) {
            throw new ArgumentException("Client settings must be a JSON object.", nameof(settings));
        }
        // Measure the exact bytes we'll persist (GetRawText preserves the client's formatting) — the cap is on what
        // lands on disk, so a pretty-printed body hits it sooner than a minified one with identical content. That's
        // intentional; the cap is generous enough that real preferences blobs never approach it either way.
        var json = settings.GetRawText();
        if (Encoding.UTF8.GetByteCount(json) > MaxBytes) {
            throw new ArgumentException($"Client settings exceed the {MaxBytes}-byte limit.", nameof(settings));
        }
        // Clone eagerly, before the first await: detaches an independent copy of the element so the returned DTO never
        // depends on the caller's request JsonDocument still being alive past the await boundary.
        var stored = settings.Clone();

        DateTimeOffset updated;
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try {
            // Atomic publish: write a sibling temp file then rename over the target, so a reader never sees a
            // half-written file and a crash mid-write leaves the prior settings intact.
            var tmp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
            try {
                await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
                File.Move(tmp, _path, overwrite: true);
            } catch {
                // A cancelled/failed write would otherwise leave the temp file behind — best-effort cleanup.
                try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
            // Stamp the mtime explicitly INSIDE the gate: rename() doesn't reliably bump the destination mtime on
            // Linux (it reflects the temp-file write, not the swap), and reading it inside the gate keeps a concurrent
            // write from stamping this caller's response with the other write's time. The returned timestamp then
            // matches exactly what GetAsync reads back.
            var now = DateTime.UtcNow;
            File.SetLastWriteTimeUtc(_path, now);
            updated = new DateTimeOffset(now, TimeSpan.Zero);
        } finally {
            _writeGate.Release();
        }

        return new ClientSettingsDto(stored, updated);
    }

    /// <summary>
    /// Reclaim crash-only orphan temp files under <paramref name="profileDir"/> — <c>client-settings.json.tmp-*</c>
    /// siblings left when a <see cref="ReplaceAsync"/> was hard-killed in the window between the temp write and the
    /// <c>File.Move</c> rename. The in-process catch only cleans up on a caught exception, not a SIGKILL, so a startup
    /// sweep reclaims them. Returns the count removed; best-effort per file (a locked/denied one is skipped, the next
    /// boot retries). Logs a Warning when it reclaims any — a non-empty sweep means the daemon died mid-write. Mirrors
    /// §43-2 <see cref="BackupService.SweepOrphans"/> / §36-2c <see cref="SkyDataInstaller.SweepStaleScratch"/>. Called
    /// at startup before request acceptance, so no concurrent write can race a temp into the sweep.
    /// </summary>
    internal static int SweepOrphans(string profileDir, ILogger? logger = null) {
        ArgumentException.ThrowIfNullOrEmpty(profileDir);
        string[] temps;
        try {
            if (!Directory.Exists(profileDir)) {
                return 0;
            }
            temps = Directory.GetFiles(profileDir, FileName + ".tmp-*", SearchOption.TopDirectoryOnly);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return 0;
        }

        var removedNames = new List<string>();
        foreach (var path in temps) {
            try {
                File.Delete(path);
                removedNames.Add(Path.GetFileName(path));
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // best-effort — leave it; the next boot retries.
            }
        }
        if (removedNames.Count > 0 && logger is not null) {
            LogOrphansSwept(logger, removedNames.Count, string.Join(", ", removedNames));
        }
        return removedNames.Count;
    }

    private static JsonElement EmptyObject() {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }

    public void Dispose() => _writeGate.Dispose();

    [LoggerMessage(Level = LogLevel.Warning, Message = "client-settings.json was unreadable JSON; serving an empty object")]
    partial void LogCorruptSettings(Exception ex);

    // Static (the sweep runs at startup before the service instance exists), so it takes the logger explicitly.
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Reclaimed {Count} crash-orphaned client-settings temp file(s) at startup (daemon died mid-write): {Files}")]
    private static partial void LogOrphansSwept(ILogger logger, int count, string files);
}
