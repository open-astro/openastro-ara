import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart' show visibleForTesting;

import '../models/guider_twin.dart';
import '../models/polar_align.dart';
import '../models/profile_list.dart';
import '../models/profile_meta.dart';
import '../models/profile_share_export.dart';
import '../models/profile_share_import_preview.dart';
import '../models/server.dart';
import 'custom_targets_service.dart';
import '../state/imaging/exposure_state.dart' show FrameKind;
import '../state/settings/autofocus_settings_state.dart';
import '../state/settings/unguided_settings_state.dart';
import '../state/settings/custom_horizon_state.dart';
import '../state/settings/camera_electronics_state.dart';
import '../state/settings/diagnostics_mode_state.dart';
import '../state/settings/equipment_connection_state.dart';
import '../state/settings/filter_wheel_policy_state.dart';
import '../state/settings/filenames_settings_state.dart';
import '../state/settings/filter_wheel_labels_state.dart';
import '../state/settings/filter_set_state.dart';
import '../state/settings/imaging_defaults_state.dart';
import '../state/settings/notifications_settings_state.dart';
import '../state/settings/optics_settings_state.dart';
import '../state/settings/phd2_settings_state.dart';
import '../state/settings/plate_solve_settings_state.dart';
import '../state/settings/safety_policies_state.dart';
import '../state/settings/site_settings_state.dart';
import '../state/settings/storage_settings_state.dart';
import '../models/dome_settings.dart';
import '../models/switch_device.dart' show CoolingFan;

/// Client-side wrapper around §37 profile endpoints. Phase 12h.6a landed the
/// daemon side (`GET`/`PUT /api/v1/profile/imaging-defaults` backed by an
/// in-memory store); this class is the client-side counterpart that the
/// settings panels call to round-trip their state.
///
/// Each profile section is one method pair (`get…` / `put…`). 12h.6b ships
/// imaging-defaults only; storage/notifications/site/etc follow as 12h.6c-N.
class ProfileApi {
  final Dio _dio;

  ProfileApi(AraServer server, {Dio? dio})
    : _dio =
          dio ??
          Dio(
            BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              receiveTimeout: const Duration(seconds: 5),
            ),
          ) {
    _dio.interceptors.add(ownWriteRecorder);
  }

  // #1644 — when this device last wrote each profile section. The daemon's
  // `profile.updated` event for that write comes back to the writer too, and
  // must not reset the screen the writer is still editing.
  static final Map<String, DateTime> _ownWrites = {};

  /// Notes each profile-section write as it goes out (the daemon publishes the
  /// event before its HTTP answer) and drops the note when the daemon refuses
  /// the write (an error answer, e.g. 409 stale-session): that published
  /// nothing, and must not hide another device's change to the section.
  static final Interceptor ownWriteRecorder = InterceptorsWrapper(
    onRequest: (req, handler) {
      const prefix = '/api/v1/profile/';
      if (req.method.toUpperCase() != 'GET' && req.path.startsWith(prefix)) {
        final section = req.path.substring(prefix.length).split('/').first;
        req.extra[_ownWriteAt] = _ownWrites[section] = DateTime.now();
      }
      handler.next(req);
    },
    onError: (err, handler) {
      final at = err.requestOptions.extra[_ownWriteAt];
      if (at != null && err.response != null) {
        _ownWrites.removeWhere((_, t) => identical(t, at));
      }
      handler.next(err);
    },
  );

  static const _ownWriteAt = 'ownWriteAt';

  /// True when this device wrote [section] within [within] of now.
  static bool wroteRecently(
    String section, {
    Duration within = const Duration(seconds: 3),
  }) {
    final at = _ownWrites[section];
    return at != null && DateTime.now().difference(at) < within;
  }

  /// Process-wide, so tests start each case without the last one's writes.
  @visibleForTesting
  static void forgetOwnWrites() => _ownWrites.clear();

  /// §37 multi-profile — list the known profiles + which one is active
  /// (`GET /api/v1/profiles`).
  Future<ProfileList> listProfiles() async {
    final res = await _dio.get<Map<String, dynamic>>('/api/v1/profiles');
    return ProfileList.fromJson(res.data ?? const <String, dynamic>{});
  }

  /// §37 multi-profile — rename a profile (`PUT /api/v1/profiles/{id}`).
  Future<void> renameProfile(String id, String name) async {
    await _dio.put<void>(
      '/api/v1/profiles/${Uri.encodeComponent(id)}',
      data: {'name': name},
    );
  }

  /// §37 multi-profile — make a profile active (`POST /api/v1/profiles/{id}/select`),
  /// loading its settings into the live store. Returns one message per device
  /// that did not come up (#1710); a failed device never fails the switch.
  Future<List<ProfileDeviceMessage>> selectProfile(String id) async {
    final res = await _dio.post<Map<String, dynamic>>(
      '/api/v1/profiles/${Uri.encodeComponent(id)}/select',
      // The daemon waits up to 30 s for the new profile's devices to settle.
      options: Options(receiveTimeout: const Duration(seconds: 45)),
    );
    final raw = res.data?['device_messages'];
    return [
      if (raw is List)
        for (final m in raw.whereType<Map<dynamic, dynamic>>())
          ProfileDeviceMessage.fromJson(Map<String, dynamic>.from(m)),
    ];
  }

  /// §63.4 — what the daemon knows about the profile's OpenAstro Guider profile
  /// (`GET /api/v1/profiles/{id}/guider-twin`), for the delete dialog.
  Future<GuiderTwinPreview> getGuiderTwin(String id) async {
    final res = await _dio.get<Map<String, dynamic>>(
      '/api/v1/profiles/${Uri.encodeComponent(id)}/guider-twin',
    );
    return GuiderTwinPreview.fromJson(res.data ?? const <String, dynamic>{});
  }

  /// §37 multi-profile — delete a profile (`DELETE /api/v1/profiles/{id}`).
  /// With [deleteGuiderProfile] the daemon also deletes the profile's OpenAstro
  /// Guider profile (calibration and dark library included) — now, or queued
  /// until the guider is connected and idle — and the result says which. The
  /// default never touches guider data.
  Future<ProfileDeleteResult> deleteProfile(
    String id, {
    bool deleteGuiderProfile = false,
  }) async {
    final res = await _dio.delete<dynamic>(
      '/api/v1/profiles/${Uri.encodeComponent(id)}',
      queryParameters: {
        'guider_profile': deleteGuiderProfile ? 'delete' : 'keep',
      },
      // The daemon releases and reconnects the guider's equipment inside this
      // request: 15 s before it queues the delete instead, plus the recovery
      // steps (profile refresh, twin re-ensure, reconnect) which run to the end
      // on their own 15 s RPC bounds. The 5 s default reported a failure for
      // a profile that WAS deleted; a minute covers the slow-daemon case.
      options: Options(receiveTimeout: const Duration(seconds: 60)),
    );
    final data = res.data;
    return data is Map<String, dynamic>
        ? ProfileDeleteResult.fromJson(data)
        : ProfileDeleteResult.kept; // an older daemon answers 204
  }

  /// §37 multi-profile — create a new profile and make it active. The daemon
  /// clones the current active profile's settings as the starting point (the
  /// wizard then PUTs the sections the user configured on top). Returns the
  /// new profile's metadata.
  Future<ProfileMeta> createProfile(String name) async {
    final res = await _dio.post<Map<String, dynamic>>(
      '/api/v1/profiles',
      data: {'name': name},
      // The daemon changes the connected devices before it answers (#1710).
      options: Options(receiveTimeout: const Duration(seconds: 60)),
    );
    final meta = ProfileMeta.fromJson(res.data ?? const <String, dynamic>{});
    if (meta.id.isEmpty) {
      // A 2xx with no usable id (empty/garbled body) — fail loudly with the
      // status code rather than returning a profile whose id we can't address.
      throw StateError(
        'createProfile got HTTP ${res.statusCode} with no profile id in the response body',
      );
    }
    return meta;
  }

  /// §70 profile-share export — render a profile into a shareable
  /// `profile-share-v1` template (`POST /api/v1/profiles/{id}/share-export`). The
  /// daemon strips all paths / secrets / location / network (§70.1); the client
  /// writes the returned manifest to a file. An unknown id surfaces as a Dio 404.
  Future<ProfileShareExport> exportProfile(String id) async {
    final res = await _dio.post<Map<String, dynamic>>(
      '/api/v1/profiles/${Uri.encodeComponent(id)}/share-export',
    );
    return ProfileShareExport.fromJson(res.data ?? const <String, dynamic>{});
  }

  /// §70 profile-share import — preview a `profile-share-v1` template
  /// (`POST /api/v1/profiles/share-import`). [manifest] is the parsed JSON of
  /// the share file; the daemon validates it, parks it under a single-use token,
  /// and returns what it'll create + what the recipient must re-enter. A file
  /// that isn't a recognized share surfaces as a Dio 422.
  Future<ProfileShareImportPreview> importPreview(
    Map<String, dynamic> manifest,
  ) async {
    final res = await _dio.post<Map<String, dynamic>>(
      '/api/v1/profiles/share-import',
      data: manifest,
    );
    // A null body (Dio's empty-response case) becomes an empty map, which
    // fromJson rejects as "missing import token" — the right surfaced error even
    // though it can't distinguish a null body from a token-less DTO.
    return ProfileShareImportPreview.fromJson(
      res.data ?? const <String, dynamic>{},
    );
  }

  /// §70 profile-share import — commit a previewed template into a new,
  /// non-active profile (`POST /api/v1/profiles/share-import/commit`). The token
  /// travels in the body (never the query string, so it can't leak into access
  /// logs). Returns the new profile's id; an unknown/expired token surfaces as a
  /// Dio 404.
  Future<String> importCommit(String importToken) async {
    final res = await _dio.post<dynamic>(
      '/api/v1/profiles/share-import/commit',
      data: {'import_token': importToken},
    );
    // The daemon returns the new Guid as a bare JSON string (201 Created). Require
    // a String specifically — a non-string body (Map/int from a version skew)
    // would otherwise toString() into a bogus, unaddressable id past the guard.
    final id = res.data is String ? res.data as String : '';
    if (id.trim().isEmpty) {
      // trim() so a whitespace-only body (version skew) is rejected too, not just
      // an empty string — both are unaddressable ids.
      throw StateError(
        'importCommit got HTTP ${res.statusCode} with no profile id in the response body',
      );
    }
    return id;
  }

  /// GET the active profile's imaging-defaults section.
  Future<ImagingDefaults> getImagingDefaults() async {
    final res = await _dio.get<Map<String, dynamic>>(
      '/api/v1/profile/imaging-defaults',
    );
    return imagingDefaultsFromJson(res.data ?? const {});
  }

  /// GET the active profile's optics section (§36 — FOV geometry inputs).
  Future<OpticsSettings> getOptics() async {
    final res = await _dio.get<Map<String, dynamic>>('/api/v1/profile/optics');
    return opticsFromJson(res.data ?? const {});
  }

  /// GET the active profile's camera-electronics section (NEXTGEN §3/§4 —
  /// exposure-planning inputs).
  Future<CameraElectronics> getCameraElectronics() async {
    final res = await _dio.get<Map<String, dynamic>>(
      '/api/v1/profile/camera-electronics',
    );
    return cameraElectronicsFromJson(res.data ?? const {});
  }

  /// GET the active profile's recent and saved targets (#1267 item 5),
  /// newest first, with the rig's `ETag`. A daemon predating the section
  /// (404) answers empty with no tag, so nothing is pushed to it.
  Future<({List<CustomTarget> value, String? etag})> getSavedTargets() async {
    try {
      return await _getTagged('saved-targets', savedTargetsFromJson);
    } on DioException catch (e) {
      if (e.response?.statusCode == 404) {
        return (value: const <CustomTarget>[], etag: null);
      }
      rethrow;
    }
  }

  /// PUT the whole list over the copy [ifMatch] names (the read's `ETag`); a
  /// list another device saved since answers 412. The daemon canonicalizes
  /// (one per name, newest first, capped) and echoes it with its new tag.
  Future<({List<CustomTarget> value, String? etag})> putSavedTargets(
    List<CustomTarget> targets, {
    required String? ifMatch,
  }) => _putTagged(
    'saved-targets',
    {
      'targets': [for (final t in targets) t.toWire()],
    },
    savedTargetsFromJson,
    ifMatch,
  );

  static List<CustomTarget> savedTargetsFromJson(Map<String, dynamic> j) {
    final list = j['targets'];
    if (list is! List) return const [];
    return [for (final e in list) ?CustomTarget.fromWire(e)];
  }

  /// GET the active profile's §36 custom terrain horizon (empty = none entered).
  Future<List<CustomHorizonPoint>> getCustomHorizon() async {
    final res = await _dio.get<Map<String, dynamic>>(
      '/api/v1/profile/custom-horizon',
    );
    return customHorizonFromJson(res.data ?? const {});
  }

  /// Wire shape of the §36 custom horizon (`{points: [{azimuth_deg, altitude_deg}]}`),
  /// also what the offline profile cache stores (#1137).
  static Map<String, dynamic> customHorizonToJson(
    List<CustomHorizonPoint> points,
  ) => {
    'points': [
      for (final p in points)
        {'azimuth_deg': p.azimuthDeg, 'altitude_deg': p.altitudeDeg},
    ],
  };

  static List<CustomHorizonPoint> customHorizonFromJson(
    Map<String, dynamic> json,
  ) {
    final raw = json['points'];
    if (raw is! List) return const [];
    return [
      for (final e in raw)
        if (e is Map)
          CustomHorizonPoint(
            azimuthDeg: (e['azimuth_deg'] as num?)?.toDouble() ?? 0,
            altitudeDeg: (e['altitude_deg'] as num?)?.toDouble() ?? 0,
          ),
    ];
  }

  /// GET the active profile's planning filter set (NEXTGEN §1/§4).
  Future<FilterSetSettings> getFilterSet() async {
    final res = await _dio.get<Map<String, dynamic>>(
      '/api/v1/profile/filter-set',
    );
    return filterSetFromJson(res.data ?? const {});
  }

  /// GET the storage section with the rig's `ETag` (ADR 0015).
  Future<({StorageSettings value, String? etag})> getStorageSection() =>
      _getTagged('storage', _storageSettingsFromJson);

  /// PUT the storage section; [ifMatch] is the `ETag` of the copy that was
  /// loaded, so a newer copy on the rig answers 412 and is left alone.
  Future<({StorageSettings value, String? etag})> putStorageSection(
    StorageSettings value, {
    String? ifMatch,
  }) => _putTagged(
    'storage',
    _storageSettingsToJson(value),
    _storageSettingsFromJson,
    ifMatch,
  );

  // ADR 0015 — the sections that load before they save. Each pair reads
  // `/api/v1/profile/<route>` with the rig's `ETag` and writes it back with
  // the loaded one as `If-Match`. Every section write goes through
  // [_putTagged], which sends nothing without that tag: the daemon refuses a
  // section PUT with no `If-Match` (428).
  Future<({T value, String? etag})> _getTagged<T>(
    String route,
    T Function(Map<String, dynamic>) parse,
  ) async {
    final res = await _dio.get<Map<String, dynamic>>('/api/v1/profile/$route');
    return (
      value: parse(res.data ?? const {}),
      etag: res.headers.value('etag'),
    );
  }

  Future<({T value, String? etag})> _putTagged<T>(
    String route,
    Object body,
    T Function(Map<String, dynamic>) parse,
    String? ifMatch,
  ) async {
    // No tag: the copy was not loaded from this rig, or the rig is from before
    // section versions (#1886) and sent none. Saving it would write over a
    // copy nobody saw, so nothing is sent.
    if (ifMatch == null || ifMatch.isEmpty) {
      throw SectionVersionMissingException(route);
    }
    final res = await _dio.put<Map<String, dynamic>>(
      '/api/v1/profile/$route',
      data: body,
      options: Options(headers: {'If-Match': ifMatch}),
    );
    return (
      value: parse(res.data ?? const {}),
      etag: res.headers.value('etag'),
    );
  }

  Future<({NotificationsSettings value, String? etag})>
  getNotificationsSection() =>
      _getTagged('notifications', _notificationsSettingsFromJson);
  Future<({NotificationsSettings value, String? etag})> putNotificationsSection(
    NotificationsSettings v, {
    String? ifMatch,
  }) => _putTagged(
    'notifications',
    _notificationsSettingsToJson(v),
    _notificationsSettingsFromJson,
    ifMatch,
  );

  Future<({FilenamesSettings value, String? etag})> getFilenamesSection() =>
      _getTagged('filenames', _filenamesSettingsFromJson);
  Future<({FilenamesSettings value, String? etag})> putFilenamesSection(
    FilenamesSettings v, {
    String? ifMatch,
  }) => _putTagged(
    'filenames',
    _filenamesSettingsToJson(v),
    _filenamesSettingsFromJson,
    ifMatch,
  );

  Future<({DiagnosticsMode value, String? etag})> getDiagnosticsModeSection() =>
      _getTagged(
        'diagnostics-mode',
        (j) => _diagnosticsModeFromString(j['mode'] as String?),
      );
  Future<({DiagnosticsMode value, String? etag})> putDiagnosticsModeSection(
    DiagnosticsMode v, {
    String? ifMatch,
  }) => _putTagged(
    'diagnostics-mode',
    {'mode': _diagnosticsModeToString(v)},
    (j) => _diagnosticsModeFromString(j['mode'] as String?),
    ifMatch,
  );

  Future<({CameraElectronics value, String? etag})>
  getCameraElectronicsSection() =>
      _getTagged('camera-electronics', _cameraElectronicsFromRig);
  Future<({CameraElectronics value, String? etag})> putCameraElectronicsSection(
    CameraElectronics v, {
    String? ifMatch,
  }) => _putTagged(
    'camera-electronics',
    cameraElectronicsToJson(v),
    _cameraElectronicsFromRig,
    ifMatch,
  );

  Future<({List<CustomHorizonPoint> value, String? etag})>
  getCustomHorizonSection() =>
      _getTagged('custom-horizon', _customHorizonFromRig);
  Future<({List<CustomHorizonPoint> value, String? etag})>
  putCustomHorizonSection(List<CustomHorizonPoint> v, {String? ifMatch}) =>
      _putTagged(
        'custom-horizon',
        customHorizonToJson(v),
        _customHorizonFromRig,
        ifMatch,
      );

  Future<({FilterSetSettings value, String? etag})> getFilterSetSection() =>
      _getTagged('filter-set', _filterSetFromRig);
  Future<({FilterSetSettings value, String? etag})> putFilterSetSection(
    FilterSetSettings v, {
    String? ifMatch,
  }) =>
      _putTagged('filter-set', filterSetToJson(v), _filterSetFromRig, ifMatch);

  Future<({FilterWheelPolicy value, String? etag})>
  getFilterWheelPolicySection() =>
      _getTagged('filter-wheel/policy', _filterWheelPolicyFromRig);
  Future<({FilterWheelPolicy value, String? etag})> putFilterWheelPolicySection(
    FilterWheelPolicy v, {
    String? ifMatch,
  }) => _putTagged(
    'filter-wheel/policy',
    v.toJson(),
    _filterWheelPolicyFromRig,
    ifMatch,
  );

  Future<({FilterWheelLabels value, String? etag})>
  getFilterWheelLabelsSection() =>
      _getTagged('filter-wheel/labels', _filterWheelLabelsFromRig);
  Future<({FilterWheelLabels value, String? etag})> putFilterWheelLabelsSection(
    FilterWheelLabels v, {
    String? ifMatch,
  }) => _putTagged(
    'filter-wheel/labels',
    {
      'labels': [for (var s = 1; s <= v.slotCount; s++) v.labelAt(s)],
    },
    _filterWheelLabelsFromRig,
    ifMatch,
  );

  Future<({UnguidedSettings value, String? etag})> getUnguidedSection() =>
      _getTagged('unguided', _unguidedSettingsFromRig);
  Future<({UnguidedSettings value, String? etag})> putUnguidedSection(
    UnguidedSettings v, {
    String? ifMatch,
  }) => _putTagged(
    'unguided',
    unguidedSettingsToJson(v),
    _unguidedSettingsFromRig,
    ifMatch,
  );

  Future<({PlateSolveSettings value, String? etag})> getPlateSolveSection() =>
      _getTagged('plate-solve', _plateSolveSettingsFromRig);
  Future<({PlateSolveSettings value, String? etag})> putPlateSolveSection(
    PlateSolveSettings v, {
    String? ifMatch,
  }) => _putTagged(
    'plate-solve',
    _plateSolveSettingsToJson(v),
    _plateSolveSettingsFromRig,
    ifMatch,
  );

  Future<({ImagingDefaults value, String? etag})> getImagingDefaultsSection() =>
      _getTagged('imaging-defaults', _imagingDefaultsFromRig);
  Future<({ImagingDefaults value, String? etag})> putImagingDefaultsSection(
    ImagingDefaults v, {
    String? ifMatch,
  }) => _putTagged(
    'imaging-defaults',
    imagingDefaultsToJson(v),
    _imagingDefaultsFromRig,
    ifMatch,
  );

  Future<({OpticsSettings value, String? etag})> getOpticsSection() =>
      _getTagged('optics', _opticsFromRig);
  Future<({OpticsSettings value, String? etag})> putOpticsSection(
    OpticsSettings v, {
    String? ifMatch,
  }) => _putTagged('optics', opticsToJson(v), _opticsFromRig, ifMatch);

  Future<({SiteSettings value, String? etag})> getSiteSection() =>
      _getTagged('site', _siteFromRig);
  Future<({SiteSettings value, String? etag})> putSiteSection(
    SiteSettings v, {
    String? ifMatch,
  }) => _putTagged('site', siteSettingsToJson(v), _siteFromRig, ifMatch);

  Future<({SafetyPolicies value, String? etag})> getSafetySection() =>
      _getTagged('safety', _safetyFromRig);
  Future<({SafetyPolicies value, String? etag})> putSafetySection(
    SafetyPolicies v, {
    String? ifMatch,
  }) => _putTagged('safety', safetyPoliciesToJson(v), _safetyFromRig, ifMatch);

  /// §58.8 — re-arm the unconfirmed-flip announce (the daemon clears
  /// `first_flip_confirmed` and the completed-flip record, so the next flip
  /// alerts and waits again). Answers the rig's policies with their new tag
  /// (#1807): this device's own write, so no `profile.updated` reloads it.
  Future<({SafetyPolicies value, String? etag})> rearmFirstFlip() =>
      _postSafetyAction('first-flip/rearm');

  /// #1589 — confirm the profile's completed meridian flip: the user watched
  /// it and the scope cleared the mount. The daemon sets
  /// `first_flip_confirmed` and keeps the record; with no completed flip on
  /// record it refuses (409 `first-flip-not-completed`). Answers the rig's
  /// policies with their new tag, as [rearmFirstFlip] does.
  Future<({SafetyPolicies value, String? etag})> confirmFirstFlip() =>
      _postSafetyAction('first-flip/confirm');

  Future<({SafetyPolicies value, String? etag})> _postSafetyAction(
    String route,
  ) async {
    final res = await _dio.post<Map<String, dynamic>>(
      '/api/v1/profile/safety/$route',
    );
    return (
      value: _safetyFromRig(res.data ?? const {}),
      etag: res.headers.value('etag'),
    );
  }

  Future<({PolarAlignSettings value, String? etag})> getPolarAlignSection() =>
      _getTagged('polar-align', _polarAlignFromRig);
  Future<({PolarAlignSettings value, String? etag})> putPolarAlignSection(
    PolarAlignSettings v, {
    String? ifMatch,
  }) => _putTagged('polar-align', v.toJson(), _polarAlignFromRig, ifMatch);

  Future<({Phd2Settings value, String? etag})> getPhd2Section() =>
      _getTagged('phd2', _phd2FromRig);
  Future<({Phd2Settings value, String? etag})> putPhd2Section(
    Phd2Settings v, {
    String? ifMatch,
  }) => _putTagged('phd2', phd2SettingsToJson(v), _phd2FromRig, ifMatch);

  Future<({AutofocusSettings value, String? etag})> getAutofocusSection() =>
      _getTagged('autofocus', _autofocusSettingsFromRig);
  Future<({AutofocusSettings value, String? etag})> putAutofocusSection(
    AutofocusSettings v, {
    String? ifMatch,
  }) => _putTagged(
    'autofocus',
    autofocusSettingsToJson(v),
    _autofocusSettingsFromRig,
    ifMatch,
  );

  Future<({EquipmentConnectionSettings value, String? etag})>
  getEquipmentConnectionSection() =>
      _getTagged('equipment-connection', _equipmentConnectionFromRig);
  Future<({EquipmentConnectionSettings value, String? etag})>
  putEquipmentConnectionSection(
    EquipmentConnectionSettings v, {
    String? ifMatch,
  }) => _putTagged(
    'equipment-connection',
    _equipmentConnectionToJson(v),
    _equipmentConnectionFromRig,
    ifMatch,
  );

  Future<({CoolingFan value, String? etag})> getCoolingFanSection() =>
      _getTagged('cooling-fan', _coolingFanFromRig);
  Future<({CoolingFan value, String? etag})> putCoolingFanSection(
    CoolingFan v, {
    String? ifMatch,
  }) => _putTagged('cooling-fan', v.toJson(), _coolingFanFromRig, ifMatch);

  Future<({DomeSettings value, String? etag})> getDomeSection() =>
      _getTagged('dome', _domeFromRig);
  Future<({DomeSettings value, String? etag})> putDomeSection(
    DomeSettings v, {
    String? ifMatch,
  }) => _putTagged('dome', v.toJson(), _domeFromRig, ifMatch);

  // The rig's copy (a GET or a PUT reply) takes no client default (ADR 0015
  // rule 3): as with storage, a missing or mistyped key fails the load or the
  // Save, and so does an unknown engine, so no default is written back. The
  // lenient readers stay for the offline cache and the untagged reads.
  static CameraElectronics _cameraElectronicsFromRig(Map<String, dynamic> j) =>
      CameraElectronics(
        sensorName: j['sensor_name'] as String,
        readNoiseE: (j['read_noise_e'] as num).toDouble(),
        fullWellE: (j['full_well_e'] as num).toDouble(),
        electronsPerAdu: (j['electrons_per_adu'] as num).toDouble(),
        gain: (j['gain'] as num).toInt(),
        quantumEfficiencyPeak: (j['quantum_efficiency_peak'] as num).toDouble(),
        autoCaptured: j['auto_captured'] as bool,
      );

  static List<CustomHorizonPoint> _customHorizonFromRig(
    Map<String, dynamic> j,
  ) => [
    for (final e in j['points'] as List)
      CustomHorizonPoint(
        azimuthDeg: ((e as Map)['azimuth_deg'] as num).toDouble(),
        altitudeDeg: (e['altitude_deg'] as num).toDouble(),
      ),
  ];

  static UnguidedSettings _unguidedSettingsFromRig(Map<String, dynamic> j) {
    if (!j.containsKey('probe_rungs_seconds')) {
      throw const FormatException('probe_rungs_seconds missing');
    }
    // Null is the daemon's own ladder (EffectiveProbeRungsSeconds): kept as
    // null, so Save writes it back unchanged.
    final rungs = j['probe_rungs_seconds'] as List?;
    return UnguidedSettings(
      guardEnabled: j['guard_enabled'] as bool,
      eccentricityTolerance: (j['eccentricity_tolerance'] as num).toDouble(),
      goodEccentricity: (j['good_eccentricity'] as num).toDouble(),
      stepDownAfter: (j['step_down_after'] as num).toInt(),
      stepUpAfter: (j['step_up_after'] as num).toInt(),
      probeRungsSeconds: rungs?.map((r) => (r as num).toDouble()).toList(),
      recentreDriftArcmin: (j['recentre_drift_arcmin'] as num).toDouble(),
      recentreSolveEveryN: (j['recentre_solve_every_n'] as num).toInt(),
      priorToleranceBasis: j['prior_tolerance_basis'] as String,
    );
  }

  // Strict like the readers above: an unknown filter kind, a missing name or a
  // wheel with no slot list fails the load instead of reading as `l`, '' or the
  // reference wheel (the lenient filterSetFromJson stays for the offline cache).
  static FilterSetSettings _filterSetFromRig(Map<String, dynamic> j) =>
      FilterSetSettings(
        filters: [
          for (final f in j['filters'] as List)
            PlanningFilter(
              name: (f as Map)['name'] as String,
              kind: FilterKind.values.firstWhere(
                (k) => k.wire == f['kind'],
                orElse: () => throw FormatException('unknown kind', f['kind']),
              ),
              bandwidthNm: (f['bandwidth_nm'] as num).toDouble(),
            ),
        ],
      );

  static FilterWheelPolicy _filterWheelPolicyFromRig(Map<String, dynamic> j) =>
      FilterWheelPolicy(
        homeOnFirstConnect: j['home_on_first_connect'] as bool,
        useFilterWheelOffsets: j['use_filter_wheel_offsets'] as bool,
      );

  // Strict like the readers above: a missing, non-list or empty labels value or
  // a non-string slot fails the load instead of reading as the reference wheel.
  static FilterWheelLabels _filterWheelLabelsFromRig(Map<String, dynamic> j) {
    final raw = j['labels'] as List;
    if (raw.isEmpty) throw const FormatException('labels empty');
    return FilterWheelLabels(labels: [for (final e in raw) e as String]);
  }

  static PlateSolveSettings _plateSolveSettingsFromRig(
    Map<String, dynamic> j,
  ) => PlateSolveSettings(
    engine: PlateSolveEngine.values.firstWhere(
      (e) => _plateSolveEngineToString(e) == j['engine'],
      orElse: () => throw FormatException('unknown engine', j['engine']),
    ),
    pathOrEndpoint: j['path_or_endpoint'] as String,
    indexDownloadPath: j['index_download_path'] as String,
    searchRadiusDeg: (j['search_radius_deg'] as num).toDouble(),
    downsampleFactor: (j['downsample_factor'] as num).toInt(),
    timeoutSeconds: (j['timeout_seconds'] as num).toInt(),
    useBlindFallback: j['use_blind_fallback'] as bool,
    centerAfterSlew: j['center_after_slew'] as bool,
    syncToCoordinates: j['sync_to_coordinates'] as bool,
    maxIterations: (j['max_iterations'] as num).toInt(),
    convergenceToleranceArcsec: (j['convergence_tolerance_arcsec'] as num)
        .toDouble(),
  );

  // Strict like the readers above: an unknown frame type, autofocus method or
  // telescope type fails the load instead of reading as light, the V-curve or
  // `other` (the lenient readers stay for the offline cache and the wizard).
  static ImagingDefaults _imagingDefaultsFromRig(Map<String, dynamic> j) =>
      ImagingDefaults(
        defaultExposure: Duration(
          seconds: (j['exposure_seconds'] as num).toInt(),
        ),
        defaultGain: (j['gain'] as num).toInt(),
        defaultOffset: (j['offset'] as num).toInt(),
        defaultBin: (j['bin'] as num).toInt(),
        defaultFrameKind: FrameKind.values.firstWhere(
          (k) => k.name == j['frame_kind'],
          orElse: () => throw FormatException('unknown frame kind', j['frame_kind']),
        ),
        coolerTargetC: (j['cooler_target_c'] as num).toDouble(),
        coolerRampRatePerMin: (j['cooler_ramp_c_per_min'] as num).toDouble(),
        warmupAtSessionEnd: j['warmup_at_session_end'] as bool,
      );

  static AutofocusSettings _autofocusSettingsFromRig(Map<String, dynamic> j) =>
      AutofocusSettings(
        method: AutofocusMethod.values.firstWhere(
          (m) => _autofocusMethodToString(m) == j['method'],
          orElse: () => throw FormatException('unknown method', j['method']),
        ),
        steps: (j['steps'] as num).toInt(),
        stepSize: (j['step_size'] as num).toInt(),
        stepSizeAuto: j['step_size_auto'] as bool,
        exposureSeconds: (j['exposure_seconds'] as num).toInt(),
        binning: (j['binning'] as num).toInt(),
        afFilter: j['af_filter'] as String,
        runAfterFilterChange: j['run_after_filter_change'] as bool,
        triggerTempDeltaC: (j['trigger_temp_delta_c'] as num).toDouble(),
        triggerHfrDriftPct: (j['trigger_hfr_drift_pct'] as num).toDouble(),
        everyNHours: (j['every_n_hours'] as num).toInt(),
        abortSequenceOnAfFailure: j['abort_sequence_on_af_failure'] as bool,
        restorePositionOnFailure: j['restore_position_on_failure'] as bool,
        telescopeType: TelescopeType.values.firstWhere(
          (t) => telescopeTypeToWire(t) == j['telescope_type'],
          orElse: () =>
              throw FormatException('unknown telescope type', j['telescope_type']),
        ),
        backlashInSteps: (j['backlash_in_steps'] as num).toInt(),
        backlashOutSteps: (j['backlash_out_steps'] as num).toInt(),
      );

  // Strict like the readers above: a missing key fails the load instead of
  // reading as 0 ("unset") or a 1.0 reducer, which a Save would then write
  // over the rig's sensor or focal length (#2041). The reducer is read as the
  // rig holds it; the daemon refuses a Save of one at or below 0.
  static OpticsSettings _opticsFromRig(Map<String, dynamic> j) =>
      OpticsSettings(
        focalLengthMm: (j['focal_length_mm'] as num).toDouble(),
        reducerFactor: (j['reducer_factor'] as num).toDouble(),
        sensorWidthPx: (j['sensor_width_px'] as num).toInt(),
        sensorHeightPx: (j['sensor_height_px'] as num).toInt(),
        pixelSizeUm: (j['pixel_size_um'] as num).toDouble(),
        apertureMm: (j['aperture_mm'] as num).toDouble(),
        telescopeName: j['telescope_name'] as String,
      );

  // Strict like the readers above: a missing key, or a twilight line the
  // client does not know, fails the load instead of reading as "Backyard" at
  // 0/0, which a Save would then write over the rig's site.
  static SiteSettings _siteFromRig(Map<String, dynamic> j) => SiteSettings(
    siteName: j['site_name'] as String,
    latitudeDeg: (j['latitude_deg'] as num).toDouble(),
    longitudeDeg: (j['longitude_deg'] as num).toDouble(),
    elevationM: (j['elevation_m'] as num).roundToDouble(),
    timeZone: j['time_zone'] as String,
    useCustomHorizon: j['use_custom_horizon'] as bool,
    defaultHorizonAltitudeDeg: (j['default_horizon_altitude_deg'] as num)
        .toDouble(),
    bortleClass: (j['bortle_class'] as num).toInt(),
    sqmMagPerArcsec2: (j['sqm_mag_per_arcsec2'] as num).toDouble(),
    typicalSeeingArcsec: (j['typical_seeing_arcsec'] as num).toDouble(),
    twilightDefinition: TwilightDefinition.values.byName(
      j['twilight_definition'] as String,
    ),
    maxSequenceRuntimeMin: (j['max_sequence_runtime_min'] as num).toInt(),
    softWarningAltitudeDeg: (j['soft_warning_altitude_deg'] as num).toDouble(),
    observerName: j['observer_name'] as String,
    permanentPier: j['permanent_pier'] as bool,
  );

  // Strict like the readers above: a missing key, or an action the client
  // does not know, fails the load instead of reading as the client's own
  // default (pause and park, auto flip on), which a Save would then write over
  // the rig's unattended running rules. The guider mode of a profile from
  // before the mode and a blank unattended window come as null, but their keys
  // are there.
  static SafetyPolicies _safetyFromRig(Map<String, dynamic> j) {
    E pick<E>(List<E> values, String Function(E) wire, String key) {
      final raw = j[key];
      return values.firstWhere(
        (v) => wire(v) == raw,
        orElse: () => throw FormatException('unknown $key', raw),
      );
    }

    Object? nullable(String key) =>
        j.containsKey(key) ? j[key] : throw FormatException('missing $key');
    final recal = nullable('meridian_guider_recal');
    return SafetyPolicies(
      onUnsafe: pick(UnsafeAction.values, _unsafeActionToString, 'on_unsafe'),
      autoResumeWhenSafe: j['auto_resume_when_safe'] as bool,
      resumeDelayMin: (j['resume_delay_min'] as num).toInt(),
      meridianFlipAuto: j['meridian_flip_auto'] as bool,
      meridianPauseMin: (j['meridian_pause_min'] as num).toInt(),
      meridianRecenter: j['meridian_recenter'] as bool,
      meridianRecalGuider: j['meridian_recal_guider'] as bool,
      meridianRefocus: pick(
        MeridianRefocusMode.values,
        _meridianRefocusToString,
        'meridian_refocus',
      ),
      meridianRefocusTempThresholdC:
          (j['meridian_refocus_temp_threshold_c'] as num).toDouble(),
      meridianGuiderRecal: recal == null
          ? null
          : pick<GuiderRecalMode>(
              GuiderRecalMode.values,
              _guiderRecalToString,
              'meridian_guider_recal',
            ),
      unattendedWindowStart:
          (nullable('unattended_window_start') as String?) ?? '',
      unattendedWindowEnd: (nullable('unattended_window_end') as String?) ?? '',
      onAltitudeLimit: pick(
        AltitudeLimitAction.values,
        _altitudeLimitActionToString,
        'on_altitude_limit',
      ),
      parkIfNoMoreTargets: j['park_if_no_more_targets'] as bool,
      onGuiderLost: pick(
        GuiderLostAction.values,
        _guiderLostActionToString,
        'on_guider_lost',
      ),
      guiderRetryTimeoutSec: (j['guider_retry_timeout_sec'] as num).toInt(),
      skipTargetIfRecoveryFails: j['skip_target_if_recovery_fails'] as bool,
      // The lenient reader takes any case here; so does this one.
      onDiskSpaceCritical: DiskSpaceCriticalAction.values.firstWhere(
        (a) => a.name == (j['on_disk_space_critical'] as String).toLowerCase(),
        orElse: () => throw FormatException(
          'unknown on_disk_space_critical',
          j['on_disk_space_critical'],
        ),
      ),
      flipSafetyEnabled: j['flip_safety_enabled'] as bool,
      expectedFlipSlewSeconds: (j['expected_flip_slew_seconds'] as num).toInt(),
      firstFlipConfirmed: j['first_flip_confirmed'] as bool,
      // #1589 — the daemon's completed-flip record (null: no flip on record;
      // the key is still there).
      firstFlipCompletedUtc: switch (nullable('first_flip_completed_utc')) {
        null => null,
        final String at => DateTime.parse(at).toUtc(),
        final other => throw FormatException(
          'bad first_flip_completed_utc',
          other,
        ),
      },
      firstFlipPierSideChanged: j['first_flip_pier_side_changed'] as bool,
      unattendedEscalation: j['unattended_escalation'] as bool,
      unattendedShutdownEnabled: j['unattended_shutdown_enabled'] as bool,
      unattendedShutdownWaitMinutes:
          (j['unattended_shutdown_wait_minutes'] as num).toInt(),
      calibrationCaptureDefault: pick(
        CalibrationCaptureDefault.values,
        _calibrationCaptureDefaultToString,
        'calibration_capture_default',
      ),
      weatherTriggersEnabled: j['weather_triggers_enabled'] as bool,
      maxWindKmh: (j['max_wind_kmh'] as num).toInt(),
      maxHumidityPct: (j['max_humidity_pct'] as num).toInt(),
      minDewDeltaC: (j['min_dew_delta_c'] as num).toDouble(),
      flatTargetAdu: (j['flat_target_adu'] as num).toInt(),
      flatTargetPercent: (j['flat_target_percent'] as num).toDouble(),
      flatTargetAduTolerancePct: (j['flat_target_adu_tolerance_pct'] as num)
          .toDouble(),
      flatFramesPerFilter: (j['flat_frames_per_filter'] as num).toInt(),
      postFlatParkMount: j['post_flat_park_mount'] as bool,
      skyFlatTargetAdu: (j['sky_flat_target_adu'] as num).toInt(),
      skyFlatFramesPerFilter: (j['sky_flat_frames_per_filter'] as num).toInt(),
      skyFlatTargetAzimuth: (j['sky_flat_target_azimuth'] as num).toDouble(),
      skyFlatTargetAltitude: (j['sky_flat_target_altitude'] as num).toDouble(),
      skyFlatStopAtMaxAdu: (j['sky_flat_stop_at_max_adu'] as num).toDouble(),
      skyFlatStopAtMinAdu: (j['sky_flat_stop_at_min_adu'] as num).toDouble(),
      skyFlatSunAltitude: (j['sky_flat_sun_altitude'] as num).toDouble(),
      unmodeled: _safetyUnmodeled(j),
    );
  }

  // Strict like the readers above: a missing key, or a loop mode the client
  // does not know, fails the load instead of reading as the client's own
  // default (1 s, Loop, motors on), which a Save would then write over the
  // rig's polar-align knobs (#1807).
  static PolarAlignSettings _polarAlignFromRig(Map<String, dynamic> j) {
    final mode = j['loop_mode'];
    if (mode != PolarAlignLoopModes.loop &&
        mode != PolarAlignLoopModes.single) {
      throw FormatException('unknown loop_mode', mode);
    }
    final camera = j['camera'];
    if (camera != PolarAlignCameras.guide && camera != PolarAlignCameras.main) {
      throw FormatException('unknown camera', camera);
    }
    return PolarAlignSettings(
      exposureSeconds: (j['exposure_seconds'] as num).toDouble(),
      binning: (j['binning'] as num).toInt(),
      targetToleranceArcmin: (j['target_tolerance_arcmin'] as num).toDouble(),
      seedRotationDeg: (j['seed_rotation_deg'] as num).toDouble(),
      loopCadenceMs: (j['loop_cadence_ms'] as num).toInt(),
      settleSeconds: (j['settle_seconds'] as num).toDouble(),
      loopMode: mode as String,
      checkStepDeg: (j['check_step_deg'] as num).toDouble(),
      coarseExposureSeconds: (j['coarse_exposure_seconds'] as num).toDouble(),
      polarAlignerAuto: j['polar_aligner_auto'] as bool,
      stopTrackingWhenDone: j['stop_tracking_when_done'] as bool,
      camera: camera as String,
      framesPerPoint: (j['frames_per_point'] as num).toInt(),
      mountClearance: _mountClearanceFromRig(j),
    );
  }

  // #2068 strict too: the key must be there (null is a profile with no mount's
  // entry yet); an entry missing a field fails the load.
  static Map<String, PolarAlignMountClearance> _mountClearanceFromRig(
    Map<String, dynamic> j,
  ) {
    if (!j.containsKey('mount_clearance')) {
      throw const FormatException('missing mount_clearance');
    }
    final raw = j['mount_clearance'] as Map<String, dynamic>?;
    return {
      for (final e in (raw ?? const <String, dynamic>{}).entries)
        e.key: _mountClearanceEntryFromRig(e.value as Map<String, dynamic>),
    };
  }

  // Every key of an entry must be there; confirmed_max_cw_angle_deg is null
  // while the mount is not confirmed.
  static PolarAlignMountClearance _mountClearanceEntryFromRig(
    Map<String, dynamic> j,
  ) {
    if (!j.containsKey('confirmed_max_cw_angle_deg')) {
      throw const FormatException('missing confirmed_max_cw_angle_deg');
    }
    return PolarAlignMountClearance(
      maxCwAngleDeg: (j['max_cw_angle_deg'] as num).toDouble(),
      clearanceConfirmed: j['clearance_confirmed'] as bool,
      confirmedMaxCwAngleDeg: (j['confirmed_max_cw_angle_deg'] as num?)
          ?.toDouble(),
    );
  }

  /// Every safety key the client has a field for.
  static final Set<String> _safetyModeledKeys = safetyPoliciesToJson(
    const SafetyPolicies(),
  ).keys.toSet();

  /// The keys of a safety reply the client has no field for, as served, so a
  /// Save sends them back unchanged (SafetyPolicies.unmodeled).
  static Map<String, Object?> _safetyUnmodeled(Map<String, dynamic> j) => {
    for (final e in j.entries)
      if (!_safetyModeledKeys.contains(e.key)) e.key: e.value,
  };

  /// GET the active profile's site-settings section.
  Future<SiteSettings> getSiteSettings() async {
    final res = await _dio.get<Map<String, dynamic>>('/api/v1/profile/site');
    return siteSettingsFromJson(res.data ?? const {});
  }

  /// GET the active profile's unguided-imaging settings (#1264). A daemon
  /// predating the section (404) answers the defaults.
  Future<UnguidedSettings> getUnguidedSettings() async {
    try {
      final res = await _dio.get<Map<String, dynamic>>(
        '/api/v1/profile/unguided',
      );
      return unguidedSettingsFromJson(res.data ?? const {});
    } on DioException catch (e) {
      if (e.response?.statusCode == 404) return const UnguidedSettings();
      rethrow;
    }
  }

  static UnguidedSettings unguidedSettingsFromJson(Map<String, dynamic> j) {
    final rungs = (j['probe_rungs_seconds'] as List<dynamic>?)
        ?.whereType<num>()
        .map((n) => n.toDouble())
        .toList();
    return UnguidedSettings(
      guardEnabled: (j['guard_enabled'] as bool?) ?? true,
      eccentricityTolerance:
          (j['eccentricity_tolerance'] as num?)?.toDouble() ?? 0.5,
      goodEccentricity: (j['good_eccentricity'] as num?)?.toDouble() ?? 0.3,
      stepDownAfter: (j['step_down_after'] as num?)?.toInt() ?? 2,
      stepUpAfter: (j['step_up_after'] as num?)?.toInt() ?? 10,
      probeRungsSeconds: rungs == null || rungs.isEmpty
          ? UnguidedSettings.defaultProbeRungsSeconds
          : rungs,
      recentreDriftArcmin:
          (j['recentre_drift_arcmin'] as num?)?.toDouble() ?? 10,
      recentreSolveEveryN: (j['recentre_solve_every_n'] as num?)?.toInt() ?? 1,
      priorToleranceBasis: (j['prior_tolerance_basis'] as String?) ?? 'max',
    );
  }

  static Map<String, dynamic> unguidedSettingsToJson(UnguidedSettings v) => {
    'guard_enabled': v.guardEnabled,
    'eccentricity_tolerance': v.eccentricityTolerance,
    'good_eccentricity': v.goodEccentricity,
    'step_down_after': v.stepDownAfter,
    'step_up_after': v.stepUpAfter,
    'probe_rungs_seconds': v.probeRungsSeconds,
    'recentre_drift_arcmin': v.recentreDriftArcmin,
    'recentre_solve_every_n': v.recentreSolveEveryN,
    'prior_tolerance_basis': v.priorToleranceBasis,
  };

  /// GET the active profile's autofocus settings.
  Future<AutofocusSettings> getAutofocusSettings() async {
    final res = await _dio.get<Map<String, dynamic>>(
      '/api/v1/profile/autofocus',
    );
    return autofocusSettingsFromJson(res.data ?? const {});
  }

  /// GET the active profile's plate-solve settings.
  Future<PlateSolveSettings> getPlateSolveSettings() async {
    final res = await _dio.get<Map<String, dynamic>>(
      '/api/v1/profile/plate-solve',
    );
    return _plateSolveSettingsFromJson(res.data ?? const {});
  }

  /// GET the active profile's OpenAstro Guider settings.
  Future<Phd2Settings> getPhd2Settings() async {
    final res = await _dio.get<Map<String, dynamic>>('/api/v1/profile/phd2');
    return _phd2SettingsFromJson(res.data ?? const {});
  }

  // ── JSON mapping ────────────────────────────────────────────────────────
  // The server's `ConfigureHttpJsonOptions` uses snake_case, so the wire
  // shape is `exposure_seconds` etc. (not `defaultExposure`).

  static ImagingDefaults imagingDefaultsFromJson(Map<String, dynamic> j) =>
      ImagingDefaults(
        defaultExposure: Duration(
          seconds: (j['exposure_seconds'] as num?)?.toInt() ?? 5,
        ),
        defaultGain: (j['gain'] as num?)?.toInt() ?? 100,
        defaultOffset: (j['offset'] as num?)?.toInt() ?? 50,
        defaultBin: (j['bin'] as num?)?.toInt() ?? 1,
        defaultFrameKind: _frameKindFromString(j['frame_kind'] as String?),
        coolerTargetC: (j['cooler_target_c'] as num?)?.toDouble() ?? -10.0,
        coolerRampRatePerMin:
            (j['cooler_ramp_c_per_min'] as num?)?.toDouble() ?? 1.0,
        warmupAtSessionEnd: (j['warmup_at_session_end'] as bool?) ?? false,
      );

  static Map<String, dynamic> imagingDefaultsToJson(ImagingDefaults v) => {
    'exposure_seconds': v.defaultExposure.inSeconds,
    'gain': v.defaultGain,
    'offset': v.defaultOffset,
    'bin': v.defaultBin,
    'frame_kind': v.defaultFrameKind.name,
    'cooler_target_c': v.coolerTargetC,
    'cooler_ramp_c_per_min': v.coolerRampRatePerMin,
    'warmup_at_session_end': v.warmupAtSessionEnd,
  };

  static FrameKind _frameKindFromString(String? s) {
    switch (s) {
      case 'dark':
        return FrameKind.dark;
      case 'bias':
        return FrameKind.bias;
      case 'flat':
        return FrameKind.flat;
      case 'light':
      default:
        return FrameKind.light;
    }
  }

  // ── Storage settings JSON mapping ──────────────────────────────────────

  static OpticsSettings opticsFromJson(Map<String, dynamic> j) {
    // The daemon defaults reducer_factor to 1.0, back-fills it on upgrade, and PUT
    // rejects ≤ 0 (#467), so the wire value is always positive in practice. Coalesce
    // a missing OR non-positive value (e.g. a hand-edited profile.json) to 1.0 = "no
    // reducer" — a 0 would otherwise multiply the focal length to 0 and silently mark
    // an otherwise-complete rig unconfigured (no FOV box).
    final reducer = (j['reducer_factor'] as num?)?.toDouble();
    return OpticsSettings(
      focalLengthMm: (j['focal_length_mm'] as num?)?.toDouble() ?? 0,
      reducerFactor: (reducer != null && reducer > 0) ? reducer : 1.0,
      sensorWidthPx: (j['sensor_width_px'] as num?)?.toInt() ?? 0,
      sensorHeightPx: (j['sensor_height_px'] as num?)?.toInt() ?? 0,
      pixelSizeUm: (j['pixel_size_um'] as num?)?.toDouble() ?? 0,
      apertureMm: (j['aperture_mm'] as num?)?.toDouble() ?? 0,
      telescopeName: (j['telescope_name'] as String?) ?? '',
    );
  }

  static Map<String, dynamic> opticsToJson(OpticsSettings v) => {
    'focal_length_mm': v.focalLengthMm,
    'reducer_factor': v.reducerFactor,
    'sensor_width_px': v.sensorWidthPx,
    'sensor_height_px': v.sensorHeightPx,
    'pixel_size_um': v.pixelSizeUm,
    'aperture_mm': v.apertureMm,
    'telescope_name': v.telescopeName,
  };

  // ── Camera electronics + filter set JSON mapping (NEXTGEN §4) ──────────

  static CameraElectronics cameraElectronicsFromJson(Map<String, dynamic> j) =>
      CameraElectronics(
        sensorName: (j['sensor_name'] as String?) ?? '',
        readNoiseE: (j['read_noise_e'] as num?)?.toDouble() ?? 0,
        fullWellE: (j['full_well_e'] as num?)?.toDouble() ?? 0,
        electronsPerAdu: (j['electrons_per_adu'] as num?)?.toDouble() ?? 0,
        gain: (j['gain'] as num?)?.toInt() ?? -1,
        quantumEfficiencyPeak:
            (j['quantum_efficiency_peak'] as num?)?.toDouble() ?? 0,
        autoCaptured: (j['auto_captured'] as bool?) ?? false,
      );

  static Map<String, dynamic> cameraElectronicsToJson(CameraElectronics v) => {
    'sensor_name': v.sensorName,
    'read_noise_e': v.readNoiseE,
    'full_well_e': v.fullWellE,
    'electrons_per_adu': v.electronsPerAdu,
    'gain': v.gain,
    'quantum_efficiency_peak': v.quantumEfficiencyPeak,
    'auto_captured': v.autoCaptured,
  };

  static FilterSetSettings filterSetFromJson(Map<String, dynamic> j) =>
      FilterSetSettings(
        filters: ((j['filters'] as List?) ?? const [])
            .whereType<Map<String, dynamic>>()
            .map(
              (f) => PlanningFilter(
                name: (f['name'] as String?) ?? '',
                kind: FilterKind.fromWire(f['kind'] as String?),
                bandwidthNm: (f['bandwidth_nm'] as num?)?.toDouble() ?? 0,
              ),
            )
            .where((f) => f.name.isNotEmpty)
            .toList(),
      );

  static Map<String, dynamic> filterSetToJson(FilterSetSettings v) => {
    'filters': [
      for (final f in v.filters)
        {'name': f.name, 'kind': f.kind.wire, 'bandwidth_nm': f.bandwidthNm},
    ],
  };

  static StorageSettings _storageSettingsFromJson(
    Map<String, dynamic> j,
  ) => StorageSettings(
    // No client defaults (ADR 0012/0015): the daemon's normalizer owns them, so
    // a missing key fails the load instead of inventing a value.
    saveDirectory: j['save_directory'] as String,
    fileFormat: StorageFileFormat.fromWire(j['file_format'] as String),
    compression: StorageCompression.fromWire(j['compression'] as String),
    filenameTemplate: j['filename_template'] as String,
    minFreeDiskWarnGb: (j['min_free_disk_warn_gb'] as num).toInt(),
    minFreeDiskCriticalGb: (j['min_free_disk_critical_gb'] as num).toInt(),
    backupRetentionCount: (j['backup_retention_count'] as num).toInt(),
    faultLogRetentionDays: (j['fault_log_retention_days'] as num).toInt(),
  );

  static Map<String, dynamic> _storageSettingsToJson(StorageSettings v) => {
    'save_directory': v.saveDirectory,
    'file_format': v.fileFormat.wire,
    'compression': v.compression.wire,
    'filename_template': v.filenameTemplate,
    'min_free_disk_warn_gb': v.minFreeDiskWarnGb,
    'min_free_disk_critical_gb': v.minFreeDiskCriticalGb,
    'backup_retention_count': v.backupRetentionCount,
    'fault_log_retention_days': v.faultLogRetentionDays,
  };

  // ── Equipment-connection JSON mapping ──────────────────────────────────

  static const _equipmentConnectionKeys = {
    EquipmentDeviceType.camera: 'camera',
    EquipmentDeviceType.mount: 'mount',
    EquipmentDeviceType.focuser: 'focuser',
    EquipmentDeviceType.filterWheel: 'filter_wheel',
    EquipmentDeviceType.rotator: 'rotator',
    EquipmentDeviceType.guider: 'guider',
    EquipmentDeviceType.flatPanel: 'flat_panel',
    EquipmentDeviceType.dome: 'dome',
    EquipmentDeviceType.weather: 'weather',
    EquipmentDeviceType.safetyMonitor: 'safety_monitor',
    EquipmentDeviceType.switchDevice: 'switch',
  };

  // Strict (ADR 0015 rule 3): a device type the rig did not report fails the
  // load instead of reading as a default, so Save never writes one back.
  static EquipmentConnectionSettings _equipmentConnectionFromRig(
    Map<String, dynamic> j,
  ) => EquipmentConnectionSettings(
    autoConnectOnBoot: {
      for (final e in _equipmentConnectionKeys.entries)
        e.key: j[e.value] as bool,
    },
  );

  // Both keys must be present: null is "no fan", an absent key is a bad reply.
  static CoolingFan _coolingFanFromRig(Map<String, dynamic> j) {
    if (!j.containsKey('device_id') || !j.containsKey('port_id')) {
      throw const FormatException('cooling fan keys missing');
    }
    return CoolingFan.fromJson(j);
  }

  static DomeSettings _domeFromRig(Map<String, dynamic> j) => DomeSettings(
    domeRadiusMm: (j['dome_radius_mm'] as num).toDouble(),
    scopePositionNorthSouthMm: (j['scope_position_north_south_mm'] as num)
        .toDouble(),
    scopePositionEastWestMm: (j['scope_position_east_west_mm'] as num)
        .toDouble(),
    scopePositionUpDownMm: (j['scope_position_up_down_mm'] as num).toDouble(),
    mountType: j['mount_type'] as String,
    gemAxisMm: (j['gem_axis_mm'] as num).toDouble(),
    lateralAxisMm: (j['lateral_axis_mm'] as num).toDouble(),
    decOffsetHorizontalMm: (j['dec_offset_horizontal_mm'] as num).toDouble(),
    azimuthToleranceDegrees: (j['azimuth_tolerance_degrees'] as num).toDouble(),
    synchronizeDuringMountSlew: j['synchronize_during_mount_slew'] as bool,
    domeSyncTimeoutSeconds: (j['dome_sync_timeout_seconds'] as num).toInt(),
  );

  static Map<String, dynamic> _equipmentConnectionToJson(
    EquipmentConnectionSettings v,
  ) => {
    'camera': v.autoConnect(EquipmentDeviceType.camera),
    'mount': v.autoConnect(EquipmentDeviceType.mount),
    'focuser': v.autoConnect(EquipmentDeviceType.focuser),
    'filter_wheel': v.autoConnect(EquipmentDeviceType.filterWheel),
    'rotator': v.autoConnect(EquipmentDeviceType.rotator),
    'guider': v.autoConnect(EquipmentDeviceType.guider),
    'flat_panel': v.autoConnect(EquipmentDeviceType.flatPanel),
    'dome': v.autoConnect(EquipmentDeviceType.dome),
    'weather': v.autoConnect(EquipmentDeviceType.weather),
    'safety_monitor': v.autoConnect(EquipmentDeviceType.safetyMonitor),
    'switch': v.autoConnect(EquipmentDeviceType.switchDevice),
  };

  // ── OpenAstro Guider settings JSON mapping ─────────────────────────────────────────

  static Phd2Settings _phd2SettingsFromJson(
    Map<String, dynamic> j,
  ) => Phd2Settings(
    host: (j['host'] as String?) ?? 'localhost',
    port: (j['port'] as num?)?.toInt() ?? 4400,
    phd2Profile: (j['phd2_profile'] as String?) ?? 'Default',
    ditherEnabled: (j['dither_enabled'] as bool?) ?? true,
    ditherEveryNFrames: (j['dither_every_n_frames'] as num?)?.toInt() ?? 1,
    ditherPixels: (j['dither_pixels'] as num?)?.toDouble() ?? 5.0,
    settlePixels: (j['settle_pixels'] as num?)?.toDouble() ?? 1.5,
    settleTimeSec: (j['settle_time_sec'] as num?)?.toInt() ?? 10,
    settleTimeoutSec: (j['settle_timeout_sec'] as num?)?.toInt() ?? 60,
    forceCalibrationEachSession:
        (j['force_calibration_each_session'] as bool?) ?? false,
    // §63.19 guide setup type — unknown tokens coerce to 'guide_scope'.
    guiderSetupType: Phd2SettingsNotifier.normalizeGuiderSetupType(
      (j['guider_setup_type'] as String?) ?? 'guide_scope',
    ),
    // §63.5 guider-engine config (defaults match the server's optional-field defaults).
    guideFocalLength: (j['guide_focal_length'] as num?)?.toInt() ?? 0,
    guidePixelSize: (j['guide_pixel_size'] as num?)?.toDouble() ?? 0,
    raAggressiveness: (j['ra_aggressiveness'] as num?)?.toDouble() ?? 0.7,
    decAggressiveness: (j['dec_aggressiveness'] as num?)?.toDouble() ?? 0.7,
    minimumMove: (j['minimum_move'] as num?)?.toDouble() ?? 0.15,
    // #1156 — a daemon profile from before the split has no Dec value: Dec follows RA.
    decMinimumMove:
        (j['dec_minimum_move'] as num?)?.toDouble() ??
        (j['minimum_move'] as num?)?.toDouble() ??
        0.15,
    decGuideMode: (j['dec_guide_mode'] as String?) ?? 'auto',
    // §63.17 guider equipment selection ("" / 0 = unset).
    guiderCamera: (j['guider_camera'] as String?) ?? '',
    guiderCameraId: (j['guider_camera_id'] as String?) ?? '',
    guiderCameraAlpacaUniqueId:
        (j['guider_camera_alpaca_unique_id'] as String?) ?? '',
    guiderMount: (j['guider_mount'] as String?) ?? '',
    guiderAuxMount: (j['guider_aux_mount'] as String?) ?? '',
    guiderRotator: (j['guider_rotator'] as String?) ?? '',
    guiderAlpacaHost: (j['guider_alpaca_host'] as String?) ?? '',
    guiderAlpacaPort: (j['guider_alpaca_port'] as num?)?.toInt() ?? 0,
    // §76.2 guide exposure range (defaults match the server's optional fields).
    guideExposureMinMs: (j['guide_exposure_min_ms'] as num?)?.toInt() ?? 1000,
    guideExposureMaxMs: (j['guide_exposure_max_ms'] as num?)?.toInt() ?? 6000,
    guideExposureMs: (j['guide_exposure_ms'] as num?)?.toInt() ?? 0,
    guideCameraGain: (j['guide_camera_gain'] as num?)?.toInt() ?? 50,
    guiderProfileId: (j['phd2_profile_id'] as num?)?.toInt() ?? 0,
  );

  // Strict like the readers above: a missing key fails the load instead of
  // reading as the client's own default (localhost, dither every frame, no
  // guide camera), which a Save would then write over the rig's guider
  // settings. The Dec minimum move of a profile from before the split and an
  // Automatic guider profile come as null, but their keys are there.
  static Phd2Settings _phd2FromRig(Map<String, dynamic> j) {
    Object? nullable(String key) =>
        j.containsKey(key) ? j[key] : throw FormatException('missing $key');
    final decMinimumMove = nullable('dec_minimum_move') as num?;
    final profileId = nullable('phd2_profile_id') as num?;
    return Phd2Settings(
      host: j['host'] as String,
      port: (j['port'] as num).toInt(),
      phd2Profile: j['phd2_profile'] as String,
      ditherEnabled: j['dither_enabled'] as bool,
      ditherEveryNFrames: (j['dither_every_n_frames'] as num).toInt(),
      ditherPixels: (j['dither_pixels'] as num).toDouble(),
      settlePixels: (j['settle_pixels'] as num).toDouble(),
      settleTimeSec: (j['settle_time_sec'] as num).toInt(),
      settleTimeoutSec: (j['settle_timeout_sec'] as num).toInt(),
      forceCalibrationEachSession: j['force_calibration_each_session'] as bool,
      guiderSetupType: Phd2SettingsNotifier.normalizeGuiderSetupType(
        j['guider_setup_type'] as String,
      ),
      guideFocalLength: (j['guide_focal_length'] as num).toInt(),
      guidePixelSize: (j['guide_pixel_size'] as num).toDouble(),
      raAggressiveness: (j['ra_aggressiveness'] as num).toDouble(),
      decAggressiveness: (j['dec_aggressiveness'] as num).toDouble(),
      minimumMove: (j['minimum_move'] as num).toDouble(),
      decMinimumMove: (decMinimumMove ?? j['minimum_move'] as num).toDouble(),
      decGuideMode: j['dec_guide_mode'] as String,
      guiderCamera: j['guider_camera'] as String,
      guiderCameraId: j['guider_camera_id'] as String,
      guiderCameraAlpacaUniqueId: j['guider_camera_alpaca_unique_id'] as String,
      guiderMount: j['guider_mount'] as String,
      guiderAuxMount: j['guider_aux_mount'] as String,
      guiderRotator: j['guider_rotator'] as String,
      guiderAlpacaHost: j['guider_alpaca_host'] as String,
      guiderAlpacaPort: (j['guider_alpaca_port'] as num).toInt(),
      guideExposureMinMs: (j['guide_exposure_min_ms'] as num).toInt(),
      guideExposureMaxMs: (j['guide_exposure_max_ms'] as num).toInt(),
      guideExposureMs: (j['guide_exposure_ms'] as num).toInt(),
      guideCameraGain: (j['guide_camera_gain'] as num).toInt(),
      guiderProfileId: profileId?.toInt() ?? 0,
    );
  }

  /// The guider settings as sent on PUT (also how the guider panel tells a
  /// real edit from a write that changes nothing).
  static Map<String, dynamic> phd2SettingsToJson(Phd2Settings v) => {
    'host': v.host,
    'port': v.port,
    'phd2_profile': v.phd2Profile,
    'dither_enabled': v.ditherEnabled,
    'dither_every_n_frames': v.ditherEveryNFrames,
    'dither_pixels': v.ditherPixels,
    'settle_pixels': v.settlePixels,
    'settle_time_sec': v.settleTimeSec,
    'settle_timeout_sec': v.settleTimeoutSec,
    'force_calibration_each_session': v.forceCalibrationEachSession,
    'guider_setup_type': v.guiderSetupType,
    'guide_focal_length': v.guideFocalLength,
    'guide_pixel_size': v.guidePixelSize,
    'ra_aggressiveness': v.raAggressiveness,
    'dec_aggressiveness': v.decAggressiveness,
    'minimum_move': v.minimumMove,
    'dec_minimum_move': v.decMinimumMove,
    'dec_guide_mode': v.decGuideMode,
    'guider_camera': v.guiderCamera,
    'guider_camera_id': v.guiderCameraId,
    'guider_camera_alpaca_unique_id': v.guiderCameraAlpacaUniqueId,
    'guider_mount': v.guiderMount,
    'guider_aux_mount': v.guiderAuxMount,
    'guider_rotator': v.guiderRotator,
    'guider_alpaca_host': v.guiderAlpacaHost,
    'guider_alpaca_port': v.guiderAlpacaPort,
    'guide_exposure_min_ms': v.guideExposureMinMs,
    'guide_exposure_max_ms': v.guideExposureMaxMs,
    'guide_exposure_ms': v.guideExposureMs,
    'guide_camera_gain': v.guideCameraGain,
    'phd2_profile_id': v.guiderProfileId > 0 ? v.guiderProfileId : null,
  };

  // ── Diagnostics mode JSON mapping ──────────────────────────────────────

  static DiagnosticsMode _diagnosticsModeFromString(String? s) {
    switch (s) {
      case 'pause_on_critical':
        return DiagnosticsMode.pauseOnCritical;
      case 'abort_on_critical':
        return DiagnosticsMode.abortOnCritical;
      case 'notify_only':
      default:
        return DiagnosticsMode.notifyOnly;
    }
  }

  static String _diagnosticsModeToString(DiagnosticsMode m) {
    switch (m) {
      case DiagnosticsMode.notifyOnly:
        return 'notify_only';
      case DiagnosticsMode.pauseOnCritical:
        return 'pause_on_critical';
      case DiagnosticsMode.abortOnCritical:
        return 'abort_on_critical';
    }
  }

  // ── Plate-solve settings JSON mapping ──────────────────────────────────

  static PlateSolveSettings _plateSolveSettingsFromJson(
    Map<String, dynamic> j,
  ) => PlateSolveSettings(
    engine: _plateSolveEngineFromString(j['engine'] as String?),
    pathOrEndpoint:
        (j['path_or_endpoint'] as String?) ?? '/opt/openastroara/astap_cli',
    indexDownloadPath:
        (j['index_download_path'] as String?) ?? '/var/lib/astap',
    searchRadiusDeg: (j['search_radius_deg'] as num?)?.toDouble() ?? 30,
    downsampleFactor: (j['downsample_factor'] as num?)?.toInt() ?? 0,
    timeoutSeconds: (j['timeout_seconds'] as num?)?.toInt() ?? 60,
    useBlindFallback: (j['use_blind_fallback'] as bool?) ?? true,
    centerAfterSlew: (j['center_after_slew'] as bool?) ?? true,
    syncToCoordinates: (j['sync_to_coordinates'] as bool?) ?? true,
    maxIterations: (j['max_iterations'] as num?)?.toInt() ?? 5,
    convergenceToleranceArcsec:
        (j['convergence_tolerance_arcsec'] as num?)?.toDouble() ?? 60.0,
  );

  static Map<String, dynamic> _plateSolveSettingsToJson(PlateSolveSettings v) =>
      {
        'engine': _plateSolveEngineToString(v.engine),
        'path_or_endpoint': v.pathOrEndpoint,
        'index_download_path': v.indexDownloadPath,
        'search_radius_deg': v.searchRadiusDeg,
        'downsample_factor': v.downsampleFactor,
        'timeout_seconds': v.timeoutSeconds,
        'use_blind_fallback': v.useBlindFallback,
        'center_after_slew': v.centerAfterSlew,
        'sync_to_coordinates': v.syncToCoordinates,
        'max_iterations': v.maxIterations,
        'convergence_tolerance_arcsec': v.convergenceToleranceArcsec,
      };

  static PlateSolveEngine _plateSolveEngineFromString(String? s) {
    switch (s) {
      case 'astrometry_net':
        return PlateSolveEngine.astrometryNet;
      case 'platesolve2':
        return PlateSolveEngine.platesolve2;
      case 'astap':
      default:
        return PlateSolveEngine.astap;
    }
  }

  static String _plateSolveEngineToString(PlateSolveEngine e) {
    switch (e) {
      case PlateSolveEngine.astap:
        return 'astap';
      case PlateSolveEngine.astrometryNet:
        return 'astrometry_net';
      case PlateSolveEngine.platesolve2:
        return 'platesolve2';
    }
  }

  // ── Autofocus settings JSON mapping ────────────────────────────────────

  static AutofocusSettings autofocusSettingsFromJson(Map<String, dynamic> j) =>
      AutofocusSettings(
        method: _autofocusMethodFromString(j['method'] as String?),
        steps: (j['steps'] as num?)?.toInt() ?? 7,
        stepSize: (j['step_size'] as num?)?.toInt() ?? 50,
        stepSizeAuto: (j['step_size_auto'] as bool?) ?? true,
        exposureSeconds: (j['exposure_seconds'] as num?)?.toInt() ?? 5,
        binning: (j['binning'] as num?)?.toInt() ?? 1,
        afFilter: (j['af_filter'] as String?) ?? 'L',
        runAfterFilterChange: (j['run_after_filter_change'] as bool?) ?? true,
        triggerTempDeltaC:
            (j['trigger_temp_delta_c'] as num?)?.toDouble() ?? 2.0,
        triggerHfrDriftPct:
            (j['trigger_hfr_drift_pct'] as num?)?.toDouble() ?? 15.0,
        everyNHours: (j['every_n_hours'] as num?)?.toInt() ?? 2,
        abortSequenceOnAfFailure:
            (j['abort_sequence_on_af_failure'] as bool?) ?? true,
        restorePositionOnFailure:
            (j['restore_position_on_failure'] as bool?) ?? true,
        telescopeType: telescopeTypeFromWire(j['telescope_type'] as String?),
        backlashInSteps: (j['backlash_in_steps'] as num?)?.toInt() ?? 0,
        backlashOutSteps: (j['backlash_out_steps'] as num?)?.toInt() ?? 0,
      );

  static Map<String, dynamic> autofocusSettingsToJson(AutofocusSettings v) => {
    'method': _autofocusMethodToString(v.method),
    'steps': v.steps,
    'step_size': v.stepSize,
    'step_size_auto': v.stepSizeAuto,
    'exposure_seconds': v.exposureSeconds,
    'binning': v.binning,
    'af_filter': v.afFilter,
    'run_after_filter_change': v.runAfterFilterChange,
    'trigger_temp_delta_c': v.triggerTempDeltaC,
    'trigger_hfr_drift_pct': v.triggerHfrDriftPct,
    'every_n_hours': v.everyNHours,
    'abort_sequence_on_af_failure': v.abortSequenceOnAfFailure,
    'restore_position_on_failure': v.restorePositionOnFailure,
    'telescope_type': telescopeTypeToWire(v.telescopeType),
    'backlash_in_steps': v.backlashInSteps,
    'backlash_out_steps': v.backlashOutSteps,
  };

  static AutofocusMethod _autofocusMethodFromString(String? s) {
    switch (s) {
      case 'brightest_star_hfr':
        return AutofocusMethod.brightestStarHfr;
      case 'fwhm':
        return AutofocusMethod.fwhm;
      case 'hfr_v_curve':
      default:
        return AutofocusMethod.hfrVCurve;
    }
  }

  static String _autofocusMethodToString(AutofocusMethod m) {
    switch (m) {
      case AutofocusMethod.hfrVCurve:
        return 'hfr_v_curve';
      case AutofocusMethod.brightestStarHfr:
        return 'brightest_star_hfr';
      case AutofocusMethod.fwhm:
        return 'fwhm';
    }
  }

  // ── Safety policies JSON mapping ───────────────────────────────────────

  /// The safety-policies wire mapping, exposed for the round-trip test (#1138).
  @visibleForTesting
  static SafetyPolicies safetyPoliciesFromJson(
    Map<String, dynamic> j,
  ) => SafetyPolicies(
    onUnsafe: _unsafeActionFromString(j['on_unsafe'] as String?),
    autoResumeWhenSafe: (j['auto_resume_when_safe'] as bool?) ?? true,
    resumeDelayMin: (j['resume_delay_min'] as num?)?.toInt() ?? 10,
    meridianFlipAuto: (j['meridian_flip_auto'] as bool?) ?? true,
    meridianPauseMin: (j['meridian_pause_min'] as num?)?.toInt() ?? 5,
    meridianRecenter: (j['meridian_recenter'] as bool?) ?? true,
    meridianRecalGuider: (j['meridian_recal_guider'] as bool?) ?? true,
    meridianRefocus: _meridianRefocusFromString(
      j['meridian_refocus'] as String?,
    ),
    meridianRefocusTempThresholdC:
        (j['meridian_refocus_temp_threshold_c'] as num?)?.toDouble() ?? 2.0,
    // A daemon/profile from before the mode carries null: left null here too, so a save
    // does not invent a mode (the daemon keeps its legacy behaviour until the user picks one).
    meridianGuiderRecal: j['meridian_guider_recal'] is String
        ? _guiderRecalFromString(j['meridian_guider_recal'] as String)
        : null,
    unattendedWindowStart: (j['unattended_window_start'] as String?) ?? '',
    unattendedWindowEnd: (j['unattended_window_end'] as String?) ?? '',
    onAltitudeLimit: _altitudeLimitActionFromString(
      j['on_altitude_limit'] as String?,
    ),
    parkIfNoMoreTargets: (j['park_if_no_more_targets'] as bool?) ?? true,
    onGuiderLost: _guiderLostActionFromString(j['on_guider_lost'] as String?),
    guiderRetryTimeoutSec:
        (j['guider_retry_timeout_sec'] as num?)?.toInt() ?? 60,
    skipTargetIfRecoveryFails:
        (j['skip_target_if_recovery_fails'] as bool?) ?? true,
    onDiskSpaceCritical: switch ((j['on_disk_space_critical'] as String?)
        ?.toLowerCase()) {
      'abort' => DiskSpaceCriticalAction.abort,
      'pause' => DiskSpaceCriticalAction.pause,
      _ => DiskSpaceCriticalAction.warn,
    },
    flipSafetyEnabled: (j['flip_safety_enabled'] as bool?) ?? true,
    expectedFlipSlewSeconds:
        (j['expected_flip_slew_seconds'] as num?)?.toInt() ?? 90,
    firstFlipConfirmed: (j['first_flip_confirmed'] as bool?) ?? false,
    firstFlipCompletedUtc: j['first_flip_completed_utc'] is String
        ? DateTime.tryParse(j['first_flip_completed_utc'] as String)?.toUtc()
        : null,
    firstFlipPierSideChanged:
        (j['first_flip_pier_side_changed'] as bool?) ?? false,
    unattendedEscalation: (j['unattended_escalation'] as bool?) ?? true,
    unattendedShutdownEnabled:
        (j['unattended_shutdown_enabled'] as bool?) ?? true,
    unattendedShutdownWaitMinutes:
        (j['unattended_shutdown_wait_minutes'] as num?)?.toInt() ?? 30,
    calibrationCaptureDefault: _calibrationCaptureDefaultFromString(
      j['calibration_capture_default'] as String?,
    ),
    weatherTriggersEnabled: (j['weather_triggers_enabled'] as bool?) ?? false,
    maxWindKmh: (j['max_wind_kmh'] as num?)?.toInt() ?? 36,
    maxHumidityPct: (j['max_humidity_pct'] as num?)?.toInt() ?? 85,
    minDewDeltaC: (j['min_dew_delta_c'] as num?)?.toDouble() ?? 2.0,
    flatTargetAdu: (j['flat_target_adu'] as num?)?.toInt() ?? 30000,
    flatTargetPercent: (j['flat_target_percent'] as num?)?.toDouble() ?? 50,
    flatTargetAduTolerancePct:
        (j['flat_target_adu_tolerance_pct'] as num?)?.toDouble() ?? 5.0,
    flatFramesPerFilter: (j['flat_frames_per_filter'] as num?)?.toInt() ?? 30,
    postFlatParkMount: (j['post_flat_park_mount'] as bool?) ?? false,
    skyFlatTargetAdu: (j['sky_flat_target_adu'] as num?)?.toInt() ?? 25000,
    skyFlatFramesPerFilter:
        (j['sky_flat_frames_per_filter'] as num?)?.toInt() ?? 20,
    skyFlatTargetAzimuth:
        (j['sky_flat_target_azimuth'] as num?)?.toDouble() ?? 90.0,
    skyFlatTargetAltitude:
        (j['sky_flat_target_altitude'] as num?)?.toDouble() ?? 75.0,
    skyFlatStopAtMaxAdu:
        (j['sky_flat_stop_at_max_adu'] as num?)?.toDouble() ?? 50000.0,
    skyFlatStopAtMinAdu:
        (j['sky_flat_stop_at_min_adu'] as num?)?.toDouble() ?? 5000.0,
    skyFlatSunAltitude:
        (j['sky_flat_sun_altitude'] as num?)?.toDouble() ?? -9.0,
    unmodeled: _safetyUnmodeled(j),
  );

  static Map<String, dynamic> safetyPoliciesToJson(SafetyPolicies v) => {
    ...v.unmodeled,
    'on_unsafe': _unsafeActionToString(v.onUnsafe),
    'auto_resume_when_safe': v.autoResumeWhenSafe,
    'resume_delay_min': v.resumeDelayMin,
    'meridian_flip_auto': v.meridianFlipAuto,
    'meridian_pause_min': v.meridianPauseMin,
    'meridian_recenter': v.meridianRecenter,
    'meridian_recal_guider': v.meridianRecalGuider,
    'meridian_refocus': _meridianRefocusToString(v.meridianRefocus),
    'meridian_refocus_temp_threshold_c': v.meridianRefocusTempThresholdC,
    'meridian_guider_recal': v.meridianGuiderRecal == null
        ? null
        : _guiderRecalToString(v.meridianGuiderRecal!),
    'unattended_window_start': v.unattendedWindowStart.isEmpty
        ? null
        : v.unattendedWindowStart,
    'unattended_window_end': v.unattendedWindowEnd.isEmpty
        ? null
        : v.unattendedWindowEnd,
    'on_altitude_limit': _altitudeLimitActionToString(v.onAltitudeLimit),
    'park_if_no_more_targets': v.parkIfNoMoreTargets,
    'on_guider_lost': _guiderLostActionToString(v.onGuiderLost),
    'guider_retry_timeout_sec': v.guiderRetryTimeoutSec,
    'skip_target_if_recovery_fails': v.skipTargetIfRecoveryFails,
    // enum.name is 'warn'/'pause'/'abort' — matches the server's OnDiskSpaceCritical values verbatim.
    'on_disk_space_critical': v.onDiskSpaceCritical.name,
    'flip_safety_enabled': v.flipSafetyEnabled,
    'expected_flip_slew_seconds': v.expectedFlipSlewSeconds,
    'first_flip_confirmed': v.firstFlipConfirmed,
    'first_flip_completed_utc': v.firstFlipCompletedUtc
        ?.toUtc()
        .toIso8601String(),
    'first_flip_pier_side_changed': v.firstFlipPierSideChanged,
    'unattended_escalation': v.unattendedEscalation,
    'unattended_shutdown_enabled': v.unattendedShutdownEnabled,
    'unattended_shutdown_wait_minutes': v.unattendedShutdownWaitMinutes,
    'calibration_capture_default': _calibrationCaptureDefaultToString(
      v.calibrationCaptureDefault,
    ),
    'weather_triggers_enabled': v.weatherTriggersEnabled,
    'max_wind_kmh': v.maxWindKmh,
    'max_humidity_pct': v.maxHumidityPct,
    'min_dew_delta_c': v.minDewDeltaC,
    'flat_target_adu': v.flatTargetAdu,
    'flat_target_percent': v.flatTargetPercent,
    'flat_target_adu_tolerance_pct': v.flatTargetAduTolerancePct,
    'flat_frames_per_filter': v.flatFramesPerFilter,
    'post_flat_park_mount': v.postFlatParkMount,
    'sky_flat_target_adu': v.skyFlatTargetAdu,
    'sky_flat_frames_per_filter': v.skyFlatFramesPerFilter,
    'sky_flat_target_azimuth': v.skyFlatTargetAzimuth,
    'sky_flat_target_altitude': v.skyFlatTargetAltitude,
    'sky_flat_stop_at_max_adu': v.skyFlatStopAtMaxAdu,
    'sky_flat_stop_at_min_adu': v.skyFlatStopAtMinAdu,
    'sky_flat_sun_altitude': v.skyFlatSunAltitude,
  };

  static CalibrationCaptureDefault _calibrationCaptureDefaultFromString(
    String? s,
  ) {
    switch (s) {
      case 'panel_at_end':
        return CalibrationCaptureDefault.panelAtEnd;
      case 'sky_at_twilight':
        return CalibrationCaptureDefault.skyAtTwilight;
      case 'never':
        return CalibrationCaptureDefault.never;
      case 'ask':
      default:
        return CalibrationCaptureDefault.ask;
    }
  }

  static String _calibrationCaptureDefaultToString(
    CalibrationCaptureDefault v,
  ) {
    switch (v) {
      case CalibrationCaptureDefault.ask:
        return 'ask';
      case CalibrationCaptureDefault.panelAtEnd:
        return 'panel_at_end';
      case CalibrationCaptureDefault.skyAtTwilight:
        return 'sky_at_twilight';
      case CalibrationCaptureDefault.never:
        return 'never';
    }
  }

  static MeridianRefocusMode _meridianRefocusFromString(String? s) {
    switch (s) {
      case 'always':
        return MeridianRefocusMode.always;
      case 'never':
        return MeridianRefocusMode.never;
      case 'if_temp_drifted':
      default:
        return MeridianRefocusMode.ifTempDrifted;
    }
  }

  static String _meridianRefocusToString(MeridianRefocusMode m) => switch (m) {
    MeridianRefocusMode.always => 'always',
    MeridianRefocusMode.never => 'never',
    MeridianRefocusMode.ifTempDrifted => 'if_temp_drifted',
  };

  static GuiderRecalMode _guiderRecalFromString(String? s) {
    switch (s) {
      case 'full':
        return GuiderRecalMode.full;
      case 'skip':
        return GuiderRecalMode.skip;
      case 'auto_restore':
      default:
        return GuiderRecalMode.autoRestore;
    }
  }

  static String _guiderRecalToString(GuiderRecalMode m) => switch (m) {
    GuiderRecalMode.full => 'full',
    GuiderRecalMode.skip => 'skip',
    GuiderRecalMode.autoRestore => 'auto_restore',
  };

  static UnsafeAction _unsafeActionFromString(String? s) {
    switch (s) {
      case 'park_only':
        return UnsafeAction.parkOnly;
      case 'abort_and_park':
        return UnsafeAction.abortAndPark;
      case 'ignore':
        return UnsafeAction.ignore;
      case 'pause_and_park':
      default:
        return UnsafeAction.pauseAndPark;
    }
  }

  static String _unsafeActionToString(UnsafeAction a) {
    switch (a) {
      case UnsafeAction.pauseAndPark:
        return 'pause_and_park';
      case UnsafeAction.parkOnly:
        return 'park_only';
      case UnsafeAction.abortAndPark:
        return 'abort_and_park';
      case UnsafeAction.ignore:
        return 'ignore';
    }
  }

  static AltitudeLimitAction _altitudeLimitActionFromString(String? s) {
    switch (s) {
      case 'pause_sequence':
        return AltitudeLimitAction.pauseSequence;
      case 'abort_sequence':
        return AltitudeLimitAction.abortSequence;
      case 'skip_target':
      default:
        return AltitudeLimitAction.skipTarget;
    }
  }

  static String _altitudeLimitActionToString(AltitudeLimitAction a) {
    switch (a) {
      case AltitudeLimitAction.skipTarget:
        return 'skip_target';
      case AltitudeLimitAction.pauseSequence:
        return 'pause_sequence';
      case AltitudeLimitAction.abortSequence:
        return 'abort_sequence';
    }
  }

  static GuiderLostAction _guiderLostActionFromString(String? s) {
    switch (s) {
      case 'skip_target':
        return GuiderLostAction.skipTarget;
      case 'abort_sequence':
        return GuiderLostAction.abortSequence;
      case 'pause_and_retry':
      default:
        return GuiderLostAction.pauseAndRetry;
    }
  }

  static String _guiderLostActionToString(GuiderLostAction a) {
    switch (a) {
      case GuiderLostAction.pauseAndRetry:
        return 'pause_and_retry';
      case GuiderLostAction.skipTarget:
        return 'skip_target';
      case GuiderLostAction.abortSequence:
        return 'abort_sequence';
    }
  }

  // ── Filenames settings JSON mapping ────────────────────────────────────

  static FilenamesSettings _filenamesSettingsFromJson(Map<String, dynamic> j) =>
      FilenamesSettings(
        dateSeparator: _dateSeparatorFromString(j['date_separator'] as String?),
        compressDarksAndBias: (j['compress_darks_and_bias'] as bool?) ?? true,
        headerIdentity: (j['header_identity'] as bool?) ?? true,
        headerSite: (j['header_site'] as bool?) ?? true,
        headerOptics: (j['header_optics'] as bool?) ?? true,
        headerTemperature: (j['header_temperature'] as bool?) ?? true,
        headerWeather: (j['header_weather'] as bool?) ?? true,
        headerEphemeris: (j['header_ephemeris'] as bool?) ?? true,
      );

  static Map<String, dynamic> _filenamesSettingsToJson(FilenamesSettings v) => {
    'date_separator': _dateSeparatorToString(v.dateSeparator),
    'compress_darks_and_bias': v.compressDarksAndBias,
    'header_identity': v.headerIdentity,
    'header_site': v.headerSite,
    'header_optics': v.headerOptics,
    'header_temperature': v.headerTemperature,
    'header_weather': v.headerWeather,
    'header_ephemeris': v.headerEphemeris,
  };

  static DateSeparator _dateSeparatorFromString(String? s) {
    switch (s) {
      case 'underscore':
        return DateSeparator.underscore;
      case 'dash':
        return DateSeparator.dash;
      case 'forward_slash':
      default:
        return DateSeparator.forwardSlash;
    }
  }

  static String _dateSeparatorToString(DateSeparator d) {
    switch (d) {
      case DateSeparator.forwardSlash:
        return 'forward_slash';
      case DateSeparator.underscore:
        return 'underscore';
      case DateSeparator.dash:
        return 'dash';
    }
  }

  // ── Site settings JSON mapping ─────────────────────────────────────────

  static SiteSettings siteSettingsFromJson(Map<String, dynamic> j) =>
      SiteSettings(
        siteName: (j['site_name'] as String?) ?? 'Backyard',
        latitudeDeg: (j['latitude_deg'] as num?)?.toDouble() ?? 0,
        longitudeDeg: (j['longitude_deg'] as num?)?.toDouble() ?? 0,
        elevationM: (j['elevation_m'] as num?)?.roundToDouble() ?? 0,
        timeZone: (j['time_zone'] as String?) ?? 'UTC',
        useCustomHorizon: (j['use_custom_horizon'] as bool?) ?? false,
        defaultHorizonAltitudeDeg:
            (j['default_horizon_altitude_deg'] as num?)?.toDouble() ?? 20,
        bortleClass: (j['bortle_class'] as num?)?.toInt() ?? 6,
        sqmMagPerArcsec2: (j['sqm_mag_per_arcsec2'] as num?)?.toDouble() ?? 0,
        typicalSeeingArcsec:
            (j['typical_seeing_arcsec'] as num?)?.toDouble() ?? 2.5,
        twilightDefinition: _twilightFromString(
          j['twilight_definition'] as String?,
        ),
        maxSequenceRuntimeMin:
            (j['max_sequence_runtime_min'] as num?)?.toInt() ?? 0,
        softWarningAltitudeDeg:
            (j['soft_warning_altitude_deg'] as num?)?.toDouble() ?? 30,
        observerName: (j['observer_name'] as String?) ?? '',
        permanentPier: (j['permanent_pier'] as bool?) ?? false,
      );

  static Map<String, dynamic> siteSettingsToJson(SiteSettings v) => {
    'site_name': v.siteName,
    'latitude_deg': v.latitudeDeg,
    'longitude_deg': v.longitudeDeg,
    'elevation_m': v.elevationM,
    'time_zone': v.timeZone,
    'use_custom_horizon': v.useCustomHorizon,
    'default_horizon_altitude_deg': v.defaultHorizonAltitudeDeg,
    'bortle_class': v.bortleClass,
    'sqm_mag_per_arcsec2': v.sqmMagPerArcsec2,
    'typical_seeing_arcsec': v.typicalSeeingArcsec,
    'twilight_definition': v.twilightDefinition.name,
    'max_sequence_runtime_min': v.maxSequenceRuntimeMin,
    'soft_warning_altitude_deg': v.softWarningAltitudeDeg,
    'observer_name': v.observerName,
    'permanent_pier': v.permanentPier,
  };

  static TwilightDefinition _twilightFromString(String? s) {
    switch (s) {
      case 'civil':
        return TwilightDefinition.civil;
      case 'nautical':
        return TwilightDefinition.nautical;
      case 'astronomical':
      default:
        return TwilightDefinition.astronomical;
    }
  }

  // ── Notifications settings JSON mapping ────────────────────────────────

  static NotificationsSettings _notificationsSettingsFromJson(
    Map<String, dynamic> j,
  ) => NotificationsSettings(
    inAppBanner: (j['in_app_banner'] as bool?) ?? true,
    osDesktop: (j['os_desktop'] as bool?) ?? true,
    soundAlert: (j['sound_alert'] as bool?) ?? true,
    pushoverToken: (j['pushover_token'] as String?) ?? '',
    telegramBotToken: (j['telegram_bot_token'] as String?) ?? '',
    pushoverUserKey: (j['pushover_user_key'] as String?) ?? '',
    telegramChatId: (j['telegram_chat_id'] as String?) ?? '',
    onSequenceComplete: (j['on_sequence_complete'] as bool?) ?? true,
    onSequencePaused: (j['on_sequence_paused'] as bool?) ?? true,
    onCriticalDiagnostic: (j['on_critical_diagnostic'] as bool?) ?? true,
    onSafetyEvent: (j['on_safety_event'] as bool?) ?? true,
    onAutofocusFailed: (j['on_autofocus_failed'] as bool?) ?? true,
    onPlateSolveFailed: (j['on_plate_solve_failed'] as bool?) ?? true,
    onDiskSpaceLow: (j['on_disk_space_low'] as bool?) ?? true,
  );

  static Map<String, dynamic> _notificationsSettingsToJson(
    NotificationsSettings v,
  ) => {
    'in_app_banner': v.inAppBanner,
    'os_desktop': v.osDesktop,
    'sound_alert': v.soundAlert,
    'pushover_token': v.pushoverToken,
    'telegram_bot_token': v.telegramBotToken,
    'pushover_user_key': v.pushoverUserKey,
    'telegram_chat_id': v.telegramChatId,
    'on_sequence_complete': v.onSequenceComplete,
    'on_sequence_paused': v.onSequencePaused,
    'on_critical_diagnostic': v.onCriticalDiagnostic,
    'on_safety_event': v.onSafetyEvent,
    'on_autofocus_failed': v.onAutofocusFailed,
    'on_plate_solve_failed': v.onPlateSolveFailed,
    'on_disk_space_low': v.onDiskSpaceLow,
  };
}

/// ADR 0015 — a section PUT was not sent: there was no `ETag` of a loaded copy
/// to send as `If-Match` (the copy was not loaded from this rig, or the rig is
/// from before section versions and sent none). Nothing was written.
class SectionVersionMissingException implements Exception {
  const SectionVersionMissingException(this.route);

  /// The section route under `/api/v1/profile/`.
  final String route;

  @override
  String toString() =>
      'The rig sent no version for the $route settings, so nothing was saved. '
      'Update the rig to save them.';
}
