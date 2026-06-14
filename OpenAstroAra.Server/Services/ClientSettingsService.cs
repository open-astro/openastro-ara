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
        var json = settings.GetRawText();
        if (Encoding.UTF8.GetByteCount(json) > MaxBytes) {
            throw new ArgumentException($"Client settings exceed the {MaxBytes}-byte limit.", nameof(settings));
        }

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try {
            // Atomic publish: write a sibling temp file then rename over the target, so a reader never sees a
            // half-written file and a crash mid-write leaves the prior settings intact.
            var tmp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
            File.Move(tmp, _path, overwrite: true);
        } finally {
            _writeGate.Release();
        }

        var updated = new DateTimeOffset(File.GetLastWriteTimeUtc(_path), TimeSpan.Zero);
        using var doc = JsonDocument.Parse(json);
        return new ClientSettingsDto(doc.RootElement.Clone(), updated);
    }

    private static JsonElement EmptyObject() {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }

    public void Dispose() => _writeGate.Dispose();

    [LoggerMessage(Level = LogLevel.Warning, Message = "client-settings.json was unreadable JSON; serving an empty object")]
    partial void LogCorruptSettings(Exception ex);
}
