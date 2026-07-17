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
    /// <summary>Record (upsert) the device most recently connected. Single-instance types keep one
    /// entry per type (newest wins); <see cref="DeviceType.Switch"/> is multi-instance so each switch
    /// is remembered independently (keyed by its Alpaca device number). Best-effort: a write failure is
    /// logged and swallowed so it never fails the connect that triggered it.</summary>
    Task RememberAsync(DiscoveredDeviceDto device, CancellationToken ct);

    /// <summary>Every remembered device — one per single-instance type, plus each remembered switch.
    /// Each entry carries its own <see cref="DiscoveredDeviceDto.Type"/> and Alpaca device number.
    /// Empty when nothing has been connected yet.</summary>
    Task<IReadOnlyList<DiscoveredDeviceDto>> GetAllAsync(CancellationToken ct);

    /// <summary>Drop every remembered entry for <paramref name="type"/> (all switches for
    /// <see cref="DeviceType.Switch"/>; FlatDevice and CoverCalibrator count as one group).
    /// Lets a client clear a stale selection — e.g. hardware the user no longer has — so
    /// auto-connect-on-boot stops attempting (and erroring on) it. Idempotent: returns the
    /// number of entries removed (0 when nothing was remembered).</summary>
    Task<int> ForgetAsync(DeviceType type, CancellationToken ct);
}

/// <summary>
/// File-backed <see cref="IEquipmentSelectionStore"/>: one <c>equipment-selection.json</c> under the
/// profile dir mapping a per-device key → the last <see cref="DiscoveredDeviceDto"/> connected for it
/// (device-type name for single-instance types; <c>Switch:{deviceNumber}</c> per switch).
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
            if (device.Type == DeviceType.Switch) {
                // Switch is multi-instance: drop any legacy single-switch entry (keyed by the
                // bare type name from before multi-switch) so it doesn't double up with the
                // device-number-keyed entries this and later remembers write.
                map.Remove(DeviceType.Switch.ToString());
            }
            map[KeyFor(device)] = device;
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

    public async Task<int> ForgetAsync(DeviceType type, CancellationToken ct) {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var tmp = _path + ".tmp";
        try {
            var map = await ReadLocked(ct).ConfigureAwait(false);
            // Match by the ENTRY's device type (not the raw key) so legacy/alias keys and
            // every Switch:{number} entry are all caught; FlatDevice and CoverCalibrator
            // are the same physical device under two tokens, so they forget together.
            var doomed = map.Where(kv => Canonical(kv.Value.Type) == Canonical(type))
                            .Select(kv => kv.Key)
                            .ToList();
            if (doomed.Count == 0) {
                return 0;
            }
            foreach (var key in doomed) {
                map.Remove(key);
            }
            var json = JsonSerializer.Serialize(map, JsonOptions);
            await File.WriteAllTextAsync(tmp, json, Encoding.UTF8, ct).ConfigureAwait(false);
            File.Move(tmp, _path, overwrite: true);
            return doomed.Count;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            SafeDeleteTmp(tmp);
            throw;
        }
        catch (IOException) {
            SafeDeleteTmp(tmp); // don't leave an orphaned .tmp if the write/move failed mid-way
            throw;
        }
        finally {
            _gate.Release();
        }
    }

    // FlatDevice/CoverCalibrator collapse — shared definition on DeviceTypeExtensions.
    private static DeviceType Canonical(DeviceType t) => t.Canonical();

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

    public async Task<IReadOnlyList<DiscoveredDeviceDto>> GetAllAsync(CancellationToken ct) {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        Dictionary<string, DiscoveredDeviceDto> raw;
        try {
            raw = await ReadLocked(ct).ConfigureAwait(false);
        } finally {
            _gate.Release();
        }
        // Re-key every value by its canonical KeyFor so a first-boot-after-upgrade read collapses a
        // legacy bare "Switch" entry (written before multi-switch) with the new "Switch:{number}"
        // entry for the same switch — otherwise auto-connect would attempt the same switch twice.
        // The entry whose raw key already equals its canonical key (the post-upgrade "Switch:0")
        // wins over a legacy alias, independent of dictionary iteration order: once an exact-keyed
        // entry is seen for a canonical key it locks it, and a non-canonical alias never overwrites
        // an exact one. (A canonical key is unique in the source dict, so at most one entry is exact
        // per key.) Single-instance types are always exact, so this is a no-op for them.
        var canonical = new Dictionary<string, DiscoveredDeviceDto>();
        var lockedByExactKey = new HashSet<string>();
        foreach (var (rawKey, device) in raw) {
            var key = KeyFor(device);
            if (lockedByExactKey.Contains(key)) {
                continue; // the canonical entry already won this key
            }
            canonical[key] = device;
            if (rawKey == key) {
                lockedByExactKey.Add(key);
            }
        }
        return canonical.Values.ToList();
    }

    // Upsert/dedup key: single-instance types collapse to one entry per type (newest wins);
    // Switch is multi-instance, so each switch is kept independently by its PHYSICAL endpoint
    // (host:port + device number — globally unique, unlike the bare device number, and stable
    // unlike the UniqueId, which bridges have been observed renaming across versions). Legacy
    // "Switch"/"Switch:{number}" keys are non-canonical aliases; GetAll's re-key migrates them.
    private static string KeyFor(DiscoveredDeviceDto device) =>
        device.Type == DeviceType.Switch
            ? $"{DeviceType.Switch}:{DiscoveredDeviceEndpoint.KeyOf(device)}"
            : device.Type.ToString();

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
