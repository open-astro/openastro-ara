import 'package:flutter/foundation.dart';

// The draft declares its own `ImagingDefaults` / `PlateSolveSettings` /
// `AutofocusSettings` bags; we map onto the profile *section* types from the
// settings-state files, so hide the draft's to disambiguate (the draft
// sub-objects are still reached via `d.<field>`).
import '../../models/profile_draft.dart'
    hide ImagingDefaults, PlateSolveSettings, AutofocusSettings;
import '../../services/profile_api.dart';
import '../../state/imaging/exposure_state.dart' show FrameKind;
import '../../state/settings/autofocus_settings_state.dart';
import '../../state/settings/imaging_defaults_state.dart';
import '../../state/settings/optics_settings_state.dart';
import '../../state/settings/phd2_settings_state.dart';
import '../../state/settings/plate_solve_settings_state.dart';
import '../../state/settings/site_settings_state.dart';
import '../../state/settings/storage_settings_state.dart';

/// §37 wizard Save. Maps the wizard's [ProfileDraft] onto the daemon's profile
/// sections and persists it as a new active profile.
///
/// The draft → section mappers are pure (base section + draft → new section)
/// so they're unit-testable without a server: each only overrides the fields
/// the wizard actually collected, leaving the freshly-created profile's
/// defaults for everything else (and for the screens 11–18 not built yet).

SiteSettings applyDraftToSite(SiteSettings base, ProfileDraft d) {
  // copyWith uses `?? base.field`, so a null override (an unset draft field, or a
  // blanked string) preserves the base value. The string fields get an explicit
  // trim/empty→null guard; the numeric fields are already nullable so a null is
  // passed straight through to the same preserve-on-null behavior.
  return base.copyWith(
    siteName: (d.siteName?.trim().isNotEmpty ?? false) ? d.siteName!.trim() : null,
    latitudeDeg: d.latitudeDeg,
    longitudeDeg: d.longitudeDeg,
    elevationM: d.altitudeMeters,
    timeZone: (d.timezone?.trim().isNotEmpty ?? false) ? d.timezone!.trim() : null,
  );
}

OpticsSettings applyDraftToOptics(OpticsSettings base, ProfileDraft d) {
  final fl = d.telescope.focalLengthMm;
  final px = d.camera.pixelSizeMicrons;
  return base.copyWith(
    focalLengthMm: (fl != null && fl > 0) ? fl : null,
    pixelSizeUm: (px != null && px > 0) ? px : null,
  );
}

ImagingDefaults applyDraftToImaging(ImagingDefaults base, ProfileDraft d) {
  final c = d.camera;
  final i = d.imagingDefaults; // screen 14
  return base.copyWith(
    coolerTargetC: c.coolingTargetC,
    coolerRampRatePerMin: c.coolerRampRateCPerMin,
    // The draft models warmup as an enum; the section flag is a bool.
    warmupAtSessionEnd: c.warmupMode != CoolerWarmupMode.off,
    defaultGain: c.defaultGain,
    defaultOffset: c.defaultOffset,
    defaultBin: c.defaultBin,
    // Screen 14 — default exposure + frame kind (null keeps the base).
    defaultExposure: i.exposure,
    defaultFrameKind: switch (i.frameType) {
      FrameType.light => FrameKind.light,
      FrameType.dark => FrameKind.dark,
      FrameType.bias => FrameKind.bias,
      FrameType.flat => FrameKind.flat,
      null => null,
    },
  );
}

Phd2Settings applyDraftToPhd2(Phd2Settings base, ProfileDraft d) {
  final g = d.guider;
  // Parse host[:port], handling IPv6 literals:
  //  - "[::1]"          → host only, no port (ends with ']')
  //  - "[::1]:4400"     → split on the LAST colon (after the closing bracket)
  //  - "::1" / "fe80::1" → bare (unbracketed) IPv6, host only — a value with 2+
  //                        colons and no brackets is an IPv6 address, since a
  //                        port on IPv6 requires [brackets] (RFC 3986 §3.2.2)
  //  - "localhost"      → host only
  //  - "localhost:4400" → split on the colon
  final hp = g.hostPort.trim();
  final String hostPart;
  final String portPart;
  if (hp.endsWith(']') ||
      (!hp.contains(']') && ':'.allMatches(hp).length >= 2)) {
    // Bracketed IPv6 with no port, OR a bare IPv6 literal: no port to split off.
    hostPart = hp;
    portPart = '';
  } else {
    // Reached only by `[ipv6]:port`, `host:port`, `host`, or `:port` — the
    // bracketed-no-port and bare-IPv6 cases are handled above. So for a
    // bracketed address here (`[::1]:4401`) lastIndexOf(':') lands on the colon
    // *after* the `]`, i.e. the real port separator; the in-bracket colons never
    // win because a value with a port can't also end in `]`.
    final idx = hp.lastIndexOf(':');
    // >= 0 (not > 0): a leading-colon ":4400" yields an empty host (→ base host)
    // and port 4400, rather than a nonsense "host = ':4400'".
    hostPart = idx >= 0 ? hp.substring(0, idx).trim() : hp;
    portPart = idx >= 0 ? hp.substring(idx + 1).trim() : '';
  }
  final host = hostPart.isNotEmpty ? hostPart : base.host;
  final port = portPart.isNotEmpty ? (int.tryParse(portPart) ?? base.port) : base.port;
  return base.copyWith(
    host: host,
    port: port,
    ditherPixels: g.ditherPixels,
    settlePixels: g.settleThresholdPx,
    settleTimeSec: g.settleDuration.inSeconds,
    forceCalibrationEachSession:
        g.calibrationCadence == CalibrationCadence.eachSession,
  );
}

/// §37.4 screen 11 — map the draft's ASTAP paths + search tuning onto the
/// plate-solve section. The screen stores trimmed-or-null values, so every field
/// passes straight through: copyWith treats null as "keep", so a blank/untouched
/// field preserves the base profile's value.
PlateSolveSettings applyDraftToPlateSolve(PlateSolveSettings base, ProfileDraft d) {
  final ps = d.plateSolve;
  return base.copyWith(
    pathOrEndpoint: ps.astapBinaryPath,
    indexDownloadPath: ps.starDatabasePath,
    searchRadiusDeg: ps.searchRadiusDeg,
    downsampleFactor: ps.downsampleFactor,
  );
}

/// §37.4 screen 12 — map the draft's autofocus subset onto the profile's
/// autofocus section. Every field is nullable, so copyWith's `??` keeps the base
/// for anything the user left blank/untouched.
AutofocusSettings applyDraftToAutofocus(AutofocusSettings base, ProfileDraft d) {
  final af = d.autofocus;
  return base.copyWith(
    exposureSeconds: af.exposureSeconds,
    steps: af.steps,
    stepSize: af.stepSize,
    runAfterFilterChange: af.runAfterFilterChange,
  );
}

/// §37.4 screen 13 — map the draft's file-saving choices onto the profile's
/// storage section. The screen stores trimmed-or-null strings (null keeps base);
/// the format/compression enums translate from the draft's wizard model
/// (compress true → Rice, false → Off; gzip stays a Settings-only choice).
StorageSettings applyDraftToStorage(StorageSettings base, ProfileDraft d) {
  final f = d.fileSaving;
  return base.copyWith(
    saveDirectory: f.saveDirectory,
    filenameTemplate: f.filenameTemplate,
    fileFormat: switch (f.format) {
      ImageFormat.fits => StorageFileFormat.fits,
      ImageFormat.xisf => StorageFileFormat.xisf,
      null => null,
    },
    compression: switch (f.compress) {
      true => StorageCompression.rice,
      false => StorageCompression.off,
      null => null,
    },
  );
}

/// Create a new active profile from the wizard draft and layer the configured
/// sections on top. Throws on transport failure — the caller surfaces it.
///
/// Pass the **live** draft (not a copy): a successful create stamps its id onto
/// `d.savedProfileId`, which a retry after a mid-save failure reads to re-use the
/// same profile instead of orphaning a new one.
Future<void> saveWizardProfile(ProfileApi api, ProfileDraft d) async {
  final name = (d.profileName?.trim().isNotEmpty ?? false)
      ? d.profileName!.trim()
      : 'Untitled profile';
  // Create the profile exactly once. If a prior Save got as far as creating it
  // but failed applying a section, the profile already exists (and is active);
  // a retry re-applies the sections onto it rather than orphaning a new profile
  // each attempt.
  if (d.savedProfileId == null) {
    // createProfile throws on an empty/garbled id, so savedProfileId is always a
    // real id here — a retry then re-uses it rather than orphaning a new profile.
    final meta = await api.createProfile(name);
    d.savedProfileId = meta.id;
  }

  // Each section is an independent GET-overlay-PUT against the active profile, so
  // run them concurrently rather than serially (4 round-trips → ~1 round-trip of
  // latency). Collect every failure (rather than letting Future.wait drop all but
  // the first) so a partial save reports exactly which sections didn't apply.
  // The four sections are disjoint (no section's GET reads another's PUT target),
  // and the caller (_saveAndExit) runs this under a non-dismissible spinner that
  // blocks a second Save, so there is no GET-after-PUT overlap to worry about.
  final errors = (await Future.wait([
    _trySave(() async => api.putSiteSettings(applyDraftToSite(await api.getSiteSettings(), d))),
    _trySave(() async => api.putOptics(applyDraftToOptics(await api.getOptics(), d))),
    _trySave(() async =>
        api.putImagingDefaults(applyDraftToImaging(await api.getImagingDefaults(), d))),
    _trySave(() async => api.putPhd2Settings(applyDraftToPhd2(await api.getPhd2Settings(), d))),
    _trySave(() async => api.putPlateSolveSettings(
        applyDraftToPlateSolve(await api.getPlateSolveSettings(), d))),
    _trySave(() async => api.putAutofocusSettings(
        applyDraftToAutofocus(await api.getAutofocusSettings(), d))),
    _trySave(() async => api.putStorageSettings(
        applyDraftToStorage(await api.getStorageSettings(), d))),
  ]))
      .whereType<Object>()
      .toList();
  if (errors.isNotEmpty) {
    throw Exception('Failed to save ${errors.length} profile section(s): '
        '${errors.map((e) => e.toString()).join('; ')}');
  }
}

/// Runs a section save and captures its error (null = success) so a concurrent
/// batch can report every failure instead of only the first.
Future<Object?> _trySave(Future<void> Function() body) async {
  try {
    await body();
    return null;
  } catch (e, st) {
    // Log the full error + stack now; the aggregated Exception the caller throws
    // only carries the joined messages, so this keeps each section's trace.
    debugPrint('[wizard] profile section save failed: $e\n$st');
    return e;
  }
}
