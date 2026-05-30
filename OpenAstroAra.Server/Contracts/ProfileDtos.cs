#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace OpenAstroAra.Server.Contracts;

/// <summary>
/// §37 profile-section DTOs. Phase 12h.6a wires the first section
/// (imaging-defaults) end-to-end (server-side store + endpoints); other
/// sections follow in 12h.6b-N, one per WILMA settings panel.
///
/// Mirrors the WILMA client's <c>ImagingDefaults</c> model
/// (<c>lib/state/settings/imaging_defaults_state.dart</c>) field-for-field.
/// The §69 default-is-no-tooltip principle on the client side determined
/// which fields exist; the server stores whatever the client sends.
/// </summary>
public sealed record ImagingDefaultsDto(
    int ExposureSeconds,
    int Gain,
    int Offset,
    int Bin,
    string FrameKind,
    double CoolerTargetC,
    double CoolerRampCPerMin,
    bool WarmupAtSessionEnd);

/// <summary>
/// §29 storage settings (save directory + file format + compression +
/// filename template). `FileFormat` is one of `fits`/`xisf`/`fits_rice`/
/// `fits_gzip`; `Compression` is `off`/`rice`/`gzip`. Both are strings on
/// the wire (matches the §60.6 enum-as-string convention used everywhere
/// else); the client maps them back to its `StorageFileFormat` +
/// `StorageCompression` enums.
/// </summary>
public sealed record StorageSettingsDto(
    string SaveDirectory,
    string FileFormat,
    string Compression,
    string FilenameTemplate);

/// <summary>
/// §54 notifications settings — channel toggles + per-channel tokens +
/// trigger toggles. Token fields are stored as plain strings here for
/// v0.0.1 simplicity; Phase 14 hardening will swap to either a
/// secret-ref by name (read from systemd-creds or similar) or
/// at-rest encryption per §40.
/// </summary>
public sealed record NotificationsSettingsDto(
    bool InAppBanner,
    bool OsDesktop,
    bool SoundAlert,
    string PushoverToken,
    string TelegramBotToken,
    bool OnSequenceComplete,
    bool OnSequencePaused,
    bool OnCriticalDiagnostic,
    bool OnSafetyEvent,
    bool OnAutofocusFailed,
    bool OnPlateSolveFailed,
    bool OnDiskSpaceLow);
