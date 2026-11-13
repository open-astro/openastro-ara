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
using System.Text.Json.Nodes;
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

    /// <summary>Replace only the top-level keys of <paramref name="patch"/> in the stored object, leaving every other
    /// key alone; a key whose value is JSON <c>null</c> is removed. Throws <see cref="ArgumentException"/> when
    /// <paramref name="patch"/> isn't a JSON object or the merged result exceeds the size cap.</summary>
    Task<ClientSettingsDto> MergeAsync(JsonElement patch, CancellationToken ct);
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
    private readonly IWsBroadcaster? _ws;

    public ClientSettingsService(string profileDir, ILogger<ClientSettingsService> logger, IWsBroadcaster? ws = null) {
        ArgumentException.ThrowIfNullOrEmpty(profileDir);
        _path = Path.Combine(profileDir, FileName);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _ws = ws;
    }

    public async Task<ClientSettingsDto> GetAsync(CancellationToken ct) {
        // Read content + timestamp under the same gate that writes hold, so the pair is a consistent snapshot — a
        // concurrent ReplaceAsync can't slip a new timestamp onto the old content (which would mislead a client's
        // "is my state current?" check). Conscious tradeoff: this also serializes concurrent reads, which is fine for
        // the read-once-on-connect access pattern this store targets (not a hot polling path).
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try {
            return await ReadLockedAsync(ct).ConfigureAwait(false);
        } finally {
            _writeGate.Release();
        }
    }

    // Caller holds _writeGate.
    private async Task<ClientSettingsDto> ReadLockedAsync(CancellationToken ct) {
        string text;
        DateTimeOffset updated;
        try {
            text = await File.ReadAllTextAsync(_path, ct).ConfigureAwait(false);
            updated = new DateTimeOffset(File.GetLastWriteTimeUtc(_path), TimeSpan.Zero);
        } catch (FileNotFoundException) {
            return new ClientSettingsDto(EmptyObject(), null);
        } catch (DirectoryNotFoundException) {
            return new ClientSettingsDto(EmptyObject(), null);
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
            updated = await WriteLockedAsync(json, ct).ConfigureAwait(false);
        } finally {
            _writeGate.Release();
        }

        await PublishUpdatedAsync(KeysOf(stored), updated).ConfigureAwait(false);
        return new ClientSettingsDto(stored, updated);
    }

    public async Task<ClientSettingsDto> MergeAsync(JsonElement patch, CancellationToken ct) {
        if (patch.ValueKind != JsonValueKind.Object) {
            throw new ArgumentException("Client settings must be a JSON object.", nameof(patch));
        }

        ClientSettingsDto result;
        // Read-modify-write under the one gate every write holds, so two devices merging different keys at once keep
        // both; two merging the same key resolve to whichever takes the gate last.
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try {
            var current = await ReadLockedAsync(ct).ConfigureAwait(false);
            var merged = JsonNode.Parse(current.Settings.GetRawText())!.AsObject();
            foreach (var prop in patch.EnumerateObject()) {
                if (prop.Value.ValueKind == JsonValueKind.Null) {
                    merged.Remove(prop.Name);
                } else {
                    merged[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
                }
            }
            var json = merged.ToJsonString();
            if (Encoding.UTF8.GetByteCount(json) > MaxBytes) {
                throw new ArgumentException($"Client settings exceed the {MaxBytes}-byte limit.", nameof(patch));
            }
            var updated = await WriteLockedAsync(json, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            result = new ClientSettingsDto(doc.RootElement.Clone(), updated);
        } finally {
            _writeGate.Release();
        }

        await PublishUpdatedAsync(KeysOf(patch), result.UpdatedUtc!.Value).ConfigureAwait(false);
        return result;
    }

    // Caller holds _writeGate.
    private async Task<DateTimeOffset> WriteLockedAsync(string json, CancellationToken ct) {
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
        return new DateTimeOffset(now, TimeSpan.Zero);
    }

    private static List<string> KeysOf(JsonElement obj) {
        var keys = new List<string>();
        foreach (var prop in obj.EnumerateObject()) {
            keys.Add(prop.Name);
        }
        return keys;
    }

    // client_settings.updated: lets the other devices refetch. Best-effort — a failed publish must not fail a write
    // that already landed on disk. Sent after the gate is released.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "WS publish is best-effort: the settings write already succeeded.")]
    private async Task PublishUpdatedAsync(List<string> keys, DateTimeOffset updated) {
        if (_ws is null) {
            return;
        }
        try {
            var arr = new JsonArray();
            foreach (var k in keys) {
                arr.Add(k);
            }
            var payload = new JsonObject { ["keys"] = arr, ["updated_utc"] = JsonValue.Create(updated) };
            using var doc = JsonDocument.Parse(payload.ToJsonString());
            await _ws.PublishAsync(Contracts.WsEvents.WsEventCatalog.ClientSettingsUpdated, doc.RootElement.Clone(),
                CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            LogUpdatedPublishFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "client_settings.updated WS event failed to publish")]
    partial void LogUpdatedPublishFailed(Exception ex);

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