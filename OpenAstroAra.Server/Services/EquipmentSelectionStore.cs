#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OpenAstroAra.Server.Contracts;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §52.1 — remembers the last device a user connected per device type so the
/// daemon can re-establish it on boot (see <see cref="EquipmentAutoConnectService"/>).
/// The client's device selection is in-memory only, and the daemon's connection
/// state is in-memory only, so nothing else survives a restart; this store is the
/// missing link that lets auto-connect-on-boot know <em>which</em> device to talk to.
/// </summary>
public interface IEquipmentSelectionStore {
    /// <summary>Record (upsert) the device most recently connected for its type. Best-effort:
    /// a write failure is logged and swallowed so it never fails the connect that triggered it.</summary>
    Task RememberAsync(DiscoveredDeviceDto device, CancellationToken ct);

    /// <summary>The remembered device per type, newest write wins. Empty when nothing has been connected yet.</summary>
    Task<IReadOnlyDictionary<DeviceType, DiscoveredDeviceDto>> GetAllAsync(CancellationToken ct);
}

/// <summary>
/// File-backed <see cref="IEquipmentSelectionStore"/>: one <c>equipment-selection.json</c> under the
/// profile dir mapping device-type name → the last <see cref="DiscoveredDeviceDto"/> connected for it.
/// Writes are atomic (temp file + rename) and serialized by a semaphore so two concurrent connects
/// can't tear the file. Mirrors the <see cref="ClientSettingsService"/> persistence pattern.
/// </summary>
public sealed partial class EquipmentSelectionStore : IEquipmentSelectionStore, IDisposable {

    internal const string FileName = "equipment-selection.json";

    private static readonly JsonSerializerOptions JsonOptions = new() {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly ILogger<EquipmentSelectionStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public EquipmentSelectionStore(string profileDir, ILogger<EquipmentSelectionStore> logger) {
        ArgumentException.ThrowIfNullOrEmpty(profileDir);
        _path = Path.Combine(profileDir, FileName);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task RememberAsync(DiscoveredDeviceDto device, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(device);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        // Atomic replace: write a temp sibling then move over the target so a
        // crash mid-write can never leave a half-written selection file.
        var tmp = _path + ".tmp";
        try {
            var map = await ReadLocked(ct).ConfigureAwait(false);
            map[device.Type.ToString()] = device;
            var json = JsonSerializer.Serialize(map, JsonOptions);
            await File.WriteAllTextAsync(tmp, json, Encoding.UTF8, ct).ConfigureAwait(false);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            SafeDeleteTmp(tmp);
            throw;
        }
#pragma warning disable CA1031 // best-effort persistence must not fail the connect that triggered it
        catch (Exception ex) {
            SafeDeleteTmp(tmp); // don't leave an orphaned .tmp if the write/move failed mid-way
            LogWriteFailed(ex, device.Type);
        }
#pragma warning restore CA1031
        finally {
            _gate.Release();
        }
    }

    // Best-effort removal of the temp file after a failed atomic write.
    private static void SafeDeleteTmp(string tmp) {
        try {
            if (File.Exists(tmp)) {
                File.Delete(tmp);
            }
        } catch (IOException) {
            // leave it — the next successful write's overwrite move reclaims it
        } catch (UnauthorizedAccessException) {
            // same — best-effort cleanup only
        }
    }

    public async Task<IReadOnlyDictionary<DeviceType, DiscoveredDeviceDto>> GetAllAsync(CancellationToken ct) {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        Dictionary<string, DiscoveredDeviceDto> raw;
        try {
            raw = await ReadLocked(ct).ConfigureAwait(false);
        } finally {
            _gate.Release();
        }
        var result = new Dictionary<DeviceType, DiscoveredDeviceDto>();
        foreach (var (key, device) in raw) {
            // Tolerate an unknown/renamed type name in an older file rather than throwing.
            if (Enum.TryParse<DeviceType>(key, out var type)) {
                result[type] = device;
            }
        }
        return result;
    }

    // Caller holds _gate.
    private async Task<Dictionary<string, DiscoveredDeviceDto>> ReadLocked(CancellationToken ct) {
        try {
            var text = await File.ReadAllTextAsync(_path, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<Dictionary<string, DiscoveredDeviceDto>>(text, JsonOptions)
                ?? new Dictionary<string, DiscoveredDeviceDto>();
        } catch (FileNotFoundException) {
            return new Dictionary<string, DiscoveredDeviceDto>();
        } catch (DirectoryNotFoundException) {
            return new Dictionary<string, DiscoveredDeviceDto>();
        } catch (JsonException ex) {
            // A corrupt file shouldn't wedge connect/boot — start fresh, but say so.
            LogReadCorrupt(ex);
            return new Dictionary<string, DiscoveredDeviceDto>();
        }
    }

    public void Dispose() => _gate.Dispose();

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Failed to persist the connected {DeviceType} device for auto-connect-on-boot.")]
    private partial void LogWriteFailed(Exception ex, DeviceType deviceType);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "equipment-selection.json was unreadable JSON; ignoring it (auto-connect starts fresh).")]
    private partial void LogReadCorrupt(Exception ex);
}
