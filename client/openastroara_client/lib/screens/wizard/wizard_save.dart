// The draft declares its own `ImagingDefaults` (Stage-4 bag); we map onto the profile
// *section* ImagingDefaults from imaging_defaults_state, so hide the draft's to disambiguate.
import '../../models/profile_draft.dart' hide ImagingDefaults;
import '../../services/profile_api.dart';
import '../../state/settings/imaging_defaults_state.dart';
import '../../state/settings/optics_settings_state.dart';
import '../../state/settings/phd2_settings_state.dart';
import '../../state/settings/site_settings_state.dart';

/// §37 wizard Save. Maps the wizard's [ProfileDraft] onto the daemon's profile
/// sections and persists it as a new active profile.
///
/// The draft → section mappers are pure (base section + draft → new section)
/// so they're unit-testable without a server: each only overrides the fields
/// the wizard actually collected, leaving the freshly-created profile's
/// defaults for everything else (and for the screens 11–18 not built yet).

SiteSettings applyDraftToSite(SiteSettings base, ProfileDraft d) {
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
  return base.copyWith(
    coolerTargetC: c.coolingTargetC,
    coolerRampRatePerMin: c.coolerRampRateCPerMin,
    // The draft models warmup as an enum; the section flag is a bool.
    warmupAtSessionEnd: c.warmupMode != CoolerWarmupMode.off,
    defaultGain: c.defaultGain,
    defaultOffset: c.defaultOffset,
    defaultBin: c.defaultBin,
  );
}

Phd2Settings applyDraftToPhd2(Phd2Settings base, ProfileDraft d) {
  final g = d.guider;
  // Split on the LAST colon so a bracketed IPv6 literal ([::1]:4400) keeps its host
  // intact; "host" (no colon) keeps the base port.
  final hp = g.hostPort.trim();
  final idx = hp.lastIndexOf(':');
  final hostPart = idx > 0 ? hp.substring(0, idx).trim() : hp;
  final portPart = idx > 0 ? hp.substring(idx + 1).trim() : '';
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

/// Create a new active profile from the wizard draft and layer the configured
/// sections on top. Throws on transport failure — the caller surfaces it.
Future<void> saveWizardProfile(ProfileApi api, ProfileDraft d) async {
  final name = (d.profileName?.trim().isNotEmpty ?? false)
      ? d.profileName!.trim()
      : 'Untitled profile';
  // Create the profile exactly once. If a prior Save got as far as creating it
  // but failed applying a section, the profile already exists (and is active);
  // a retry re-applies the sections onto it rather than orphaning a new profile
  // each attempt.
  if (d.savedProfileId == null) {
    final meta = await api.createProfile(name);
    if (meta.id.isEmpty) {
      // Guard the empty-string case: storing "" is non-null, so a retry would
      // skip create and PUT sections against an unknown active profile.
      throw const FormatException('createProfile returned an empty profile id');
    }
    d.savedProfileId = meta.id;
  }

  // Each section is an independent GET-overlay-PUT against the active profile, so
  // run them concurrently rather than serially (4 round-trips → ~1 round-trip of
  // latency). Collect every failure (rather than letting Future.wait drop all but
  // the first) so a partial save reports exactly which sections didn't apply.
  final errors = (await Future.wait([
    _trySave(() async => api.putSiteSettings(applyDraftToSite(await api.getSiteSettings(), d))),
    _trySave(() async => api.putOptics(applyDraftToOptics(await api.getOptics(), d))),
    _trySave(() async =>
        api.putImagingDefaults(applyDraftToImaging(await api.getImagingDefaults(), d))),
    _trySave(() async => api.putPhd2Settings(applyDraftToPhd2(await api.getPhd2Settings(), d))),
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
  } catch (e) {
    return e;
  }
}
