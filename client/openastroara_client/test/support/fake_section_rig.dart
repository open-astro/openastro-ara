import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/profile_api.dart';

/// One write that reached the rig's store.
typedef LandedPut = ({String section, Object? body, String? ifMatch});

/// A rig at the HTTP level for any profile section (ADR 0015): it serves each
/// section under `/api/v1/profile/<section>` with an `ETag`, refuses a PUT
/// with no or an empty `If-Match` with a 428 and one whose `If-Match` names
/// another copy with a 412 (nothing written either way), as the daemon does.
/// [puts] holds only the writes that landed, so a test can check the rig's
/// side whatever shape the client's section notifiers take.
class FakeSectionRig implements HttpClientAdapter {
  FakeSectionRig(Map<String, Object?> sections) : _sections = {...sections};

  static const _prefix = '/api/v1/profile/';

  final Map<String, Object?> _sections;
  final Map<String, int> _versions = {};

  /// Sections whose GET fails as an unreachable rig does.
  final Set<String> failGet = {};

  /// Each entry fails one PUT to its section with a 500 (nothing written).
  final List<String> failPut = [];

  /// Each entry answers one PUT to its section with a 428, as if it carried
  /// no `If-Match` (nothing written).
  final List<String> requireVersion = [];

  /// False: a rig from before section versions, which answers no `ETag`.
  bool sendsTags = true;

  /// Section -> a key its PUT reply leaves out (the write still lands).
  final Map<String, String> dropFromReply = {};

  /// Every write that landed, in order.
  final List<LandedPut> puts = [];

  /// A POST path under the profile -> the section it writes and the change it
  /// makes there (the safety first-flip re-arm). The copy and its tag move,
  /// and the answer carries the new copy with its tag, as the daemon's does.
  final Map<String, ({String section, Object? Function(Object? copy) change})>
  actions = {};

  /// Every `If-Match` sent with a PUT (null when none), landed or refused.
  final List<String?> ifMatches = [];

  String etagOf(String section) => '"$section-v${_versions[section] ?? 1}"';

  /// Another device saved [section]: the rig's copy, and so its tag, moved on.
  void savedElsewhere(String section) =>
      _versions[section] = (_versions[section] ?? 1) + 1;

  /// The daemon itself wrote [section] outside the profile routes (the wheel's
  /// slot names mirrored into the labels): the copy and its tag move, no
  /// client PUT is recorded and no `profile.updated` reaches the client.
  void daemonWrote(String section, Object? body) {
    _sections[section] = body;
    savedElsewhere(section);
  }

  /// The rig's current copy of [section].
  Object? sectionOf(String section) => _sections[section];

  /// A [ProfileApi] whose requests reach this rig and nothing else.
  ProfileApi api({String hostname = 'rig.test'}) => ProfileApi(
    AraServer(hostname: hostname, port: 1),
    dio: Dio(BaseOptions(baseUrl: 'http://$hostname:1'))
      ..httpClientAdapter = this,
  );

  @override
  void close({bool force = false}) {}

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    final section = options.path.startsWith(_prefix)
        ? options.path.substring(_prefix.length)
        : options.path;
    if (options.method.toUpperCase() == 'POST' && actions[section] != null) {
      final a = actions[section]!;
      _sections[a.section] = a.change(_sections[a.section]);
      savedElsewhere(a.section);
      return _json(_sections[a.section], 200, etag: etagOf(a.section));
    }
    if (!_sections.containsKey(section)) {
      return _json({'title': 'no such section'}, 404);
    }
    switch (options.method.toUpperCase()) {
      case 'GET':
        if (failGet.contains(section)) {
          throw DioException(
            requestOptions: options,
            type: DioExceptionType.connectionError,
            error: 'rig unreachable',
          );
        }
        return _json(_sections[section], 200, etag: etagOf(section));
      case 'PUT':
        final ifMatch = options.headers['if-match'] as String?;
        ifMatches.add(ifMatch);
        if (failPut.remove(section)) return _json({'title': 'rig error'}, 500);
        if (ifMatch == null ||
            ifMatch.trim().isEmpty ||
            requireVersion.remove(section)) {
          return _json({'title': 'Precondition Required'}, 428);
        }
        if (ifMatch != '*' && ifMatch != etagOf(section)) {
          return _json({'title': 'Precondition Failed'}, 412);
        }
        puts.add((section: section, body: options.data, ifMatch: ifMatch));
        final before = _sections[section];
        _sections[section] = options.data;
        savedElsewhere(section);
        if (section == 'optics' && !_sameJson(before, options.data)) {
          _opticsChanged();
        }
        final drop = dropFromReply[section];
        final reply = drop == null
            ? options.data
            : ({...options.data as Map}..remove(drop));
        return _json(reply, 200, etag: etagOf(section));
      default:
        return _json({'title': 'method not allowed'}, 405);
    }
  }

  /// The daemon's optics write (`FileProfileStore.WithOptics`, mirrored by
  /// `InMemoryProfileStore.SetOpticsLocked`): an optics copy that differs from
  /// the stored one clears the first-flip state in safety
  /// (`ProfileSections.WithoutFirstFlip`), so the safety copy, and its tag,
  /// move when that state was set. An identical re-save keeps both.
  void _opticsChanged() {
    final safety = _sections['safety'];
    if (safety is! Map) return;
    final cleared = {
      ...safety,
      'first_flip_confirmed': false,
      'first_flip_completed_utc': null,
      'first_flip_pier_side_changed': false,
    };
    if (_sameJson(safety, cleared)) return;
    _sections['safety'] = cleared;
    savedElsewhere('safety');
  }

  /// Two JSON values hold the same data (a number compares by value, so 1 and
  /// 1.0 match, as the daemon's DTO equality does).
  static bool _sameJson(Object? a, Object? b) {
    if (a is Map && b is Map) {
      return a.length == b.length &&
          a.keys.every((k) => b.containsKey(k) && _sameJson(a[k], b[k]));
    }
    if (a is List && b is List) {
      if (a.length != b.length) return false;
      for (var i = 0; i < a.length; i++) {
        if (!_sameJson(a[i], b[i])) return false;
      }
      return true;
    }
    return a == b;
  }

  ResponseBody _json(Object? body, int status, {String? etag}) =>
      ResponseBody.fromString(
        jsonEncode(body),
        status,
        headers: {
          Headers.contentTypeHeader: [Headers.jsonContentType],
          if (etag != null && sendsTags) 'etag': [etag],
        },
      );
}

/// The daemon's copy of each section on a fresh profile (its section
/// defaults), for the sections whose reader takes no client default: every
/// key is there, so the load succeeds (ADR 0015 rule 3).
const rigDefaults = <String, Map<String, Object?>>{
  'autofocus': {
    'method': 'hfr_v_curve',
    'steps': 7,
    'step_size': 50,
    'exposure_seconds': 5,
    'binning': 1,
    'af_filter': 'L',
    'run_after_filter_change': true,
    'trigger_temp_delta_c': 2.0,
    'trigger_hfr_drift_pct': 15.0,
    'every_n_hours': 2,
    'abort_sequence_on_af_failure': true,
    'restore_position_on_failure': true,
    'telescope_type': 'other',
    'step_size_auto': true,
    'backlash_in_steps': 0,
    'backlash_out_steps': 0,
  },
  'camera-electronics': {
    'sensor_name': '',
    'read_noise_e': 0,
    'full_well_e': 0,
    'electrons_per_adu': 0,
    'gain': -1,
    'quantum_efficiency_peak': 0,
    'auto_captured': false,
  },
  'cooling-fan': {'device_id': null, 'port_id': null},
  'custom-horizon': {'points': []},
  'dome': {
    'dome_radius_mm': 0,
    'scope_position_north_south_mm': 0,
    'scope_position_east_west_mm': 0,
    'scope_position_up_down_mm': 0,
    'mount_type': 'equatorial',
    'gem_axis_mm': 0,
    'lateral_axis_mm': 0,
    'dec_offset_horizontal_mm': 0,
    'azimuth_tolerance_degrees': 2,
    'synchronize_during_mount_slew': false,
    'dome_sync_timeout_seconds': 120,
  },
  'diagnostics-mode': {'mode': 'notify_only'},
  'equipment-connection': {
    'camera': true,
    'mount': true,
    'focuser': true,
    'filter_wheel': true,
    'rotator': true,
    'guider': true,
    'flat_panel': true,
    'dome': true,
    'weather': true,
    'safety_monitor': true,
    'switch': true,
  },
  'filenames': {
    'date_separator': 'forward_slash',
    'compress_darks_and_bias': true,
    'header_site': true,
    'header_optics': true,
    'header_temperature': true,
    'header_weather': true,
    'header_identity': true,
    'header_ephemeris': true,
  },
  'filter-set': {'filters': []},
  'filter-wheel/labels': {
    'labels': ['L', 'R', 'G', 'B', 'Hα', 'OIII', 'SII', ''],
  },
  'filter-wheel/policy': {
    'home_on_first_connect': true,
    'use_filter_wheel_offsets': false,
  },
  'imaging-defaults': {
    'exposure_seconds': 5,
    'gain': 100,
    'offset': 50,
    'bin': 1,
    'frame_kind': 'light',
    'cooler_target_c': -10.0,
    'cooler_ramp_c_per_min': 1.0,
    'warmup_at_session_end': false,
  },
  'notifications': {
    'in_app_banner': true,
    'os_desktop': true,
    'sound_alert': true,
    'pushover_token': '',
    'telegram_bot_token': '',
    'on_sequence_complete': true,
    'on_sequence_paused': true,
    'on_critical_diagnostic': true,
    'on_safety_event': true,
    'on_autofocus_failed': true,
    'on_plate_solve_failed': true,
    'on_disk_space_low': true,
    'pushover_user_key': '',
    'telegram_chat_id': '',
  },
  'optics': {
    'focal_length_mm': 0,
    'reducer_factor': 1.0,
    'sensor_width_px': 0,
    'sensor_height_px': 0,
    'pixel_size_um': 0,
    'aperture_mm': 0,
    'telescope_name': '',
  },
  // The daemon sends null for a profile's Dec minimum move from before the
  // split and for an Automatic guider profile; the keys are there.
  'phd2': {
    'host': 'localhost',
    'port': 4400,
    'phd2_profile': 'Default',
    'dither_enabled': true,
    'dither_every_n_frames': 1,
    'dither_pixels': 5.0,
    'settle_pixels': 1.5,
    'settle_time_sec': 10,
    'settle_timeout_sec': 60,
    'force_calibration_each_session': false,
    'guide_focal_length': 0,
    'guide_pixel_size': 0.0,
    'ra_aggressiveness': 0.7,
    'dec_aggressiveness': 0.7,
    'minimum_move': 0.15,
    'dec_guide_mode': 'auto',
    'guider_camera': '',
    'guider_camera_id': '',
    'guider_camera_alpaca_unique_id': '',
    'guider_mount': '',
    'guider_aux_mount': '',
    'guider_rotator': '',
    'guider_alpaca_host': '',
    'guider_alpaca_port': 0,
    'guider_setup_type': 'guide_scope',
    'guide_exposure_min_ms': 1000,
    'guide_exposure_max_ms': 6000,
    'guide_exposure_ms': 0,
    'dec_minimum_move': null,
    'guide_camera_gain': 50,
    'phd2_profile_id': null,
  },
  'plate-solve': {
    'engine': 'astap',
    'path_or_endpoint': '/opt/openastroara/astap_cli',
    'index_download_path': '/var/lib/astap',
    'search_radius_deg': 30,
    'downsample_factor': 0,
    'timeout_seconds': 60,
    'use_blind_fallback': true,
    'center_after_slew': true,
    'sync_to_coordinates': true,
    'max_iterations': 5,
    'convergence_tolerance_arcsec': 60,
  },
  'polar-align': {
    'exposure_seconds': 1.0,
    'binning': 0,
    'target_tolerance_arcmin': 1.0,
    'seed_rotation_deg': 10.0,
    'loop_cadence_ms': 1000,
    'settle_seconds': 2.0,
    'loop_mode': 'loop',
    'check_step_deg': 10.0,
    'coarse_exposure_seconds': 3.0,
    'polar_aligner_auto': true,
    'stop_tracking_when_done': true,
    'camera': 'guide',
    'frames_per_point': 3,
  },
  // The daemon sends null for a profile's unset guider mode and a blank
  // unattended window; the keys are there.
  'safety': {
    'on_unsafe': 'pause_and_park',
    'auto_resume_when_safe': true,
    'resume_delay_min': 10,
    'meridian_flip_auto': true,
    'meridian_pause_min': 5,
    'meridian_recenter': true,
    'meridian_recal_guider': true,
    'meridian_refocus': 'if_temp_drifted',
    'meridian_refocus_temp_threshold_c': 2.0,
    'meridian_guider_recal': null,
    'unattended_window_start': null,
    'unattended_window_end': null,
    'on_altitude_limit': 'skip_target',
    'park_if_no_more_targets': true,
    'on_guider_lost': 'pause_and_retry',
    'guider_retry_timeout_sec': 60,
    'skip_target_if_recovery_fails': true,
    'on_disk_space_critical': 'warn',
    'flip_safety_enabled': true,
    'expected_flip_slew_seconds': 90,
    'first_flip_confirmed': false,
    'first_flip_completed_utc': null,
    'first_flip_pier_side_changed': false,
    'clearance_limit_enabled': false,
    'clearance_max_altitude_deg': 80.0,
    'unattended_escalation': true,
    'unattended_shutdown_enabled': true,
    'unattended_shutdown_wait_minutes': 30,
    'calibration_capture_default': 'ask',
    'weather_triggers_enabled': false,
    'max_wind_kmh': 36,
    'max_humidity_pct': 85,
    'min_dew_delta_c': 2.0,
    'flat_target_adu': 30000,
    'flat_target_percent': 50.0,
    'flat_target_adu_tolerance_pct': 5.0,
    'flat_frames_per_filter': 30,
    'post_flat_park_mount': false,
    'sky_flat_target_adu': 25000,
    'sky_flat_frames_per_filter': 20,
    'sky_flat_target_azimuth': 90.0,
    'sky_flat_target_altitude': 75.0,
    'sky_flat_stop_at_max_adu': 50000.0,
    'sky_flat_stop_at_min_adu': 5000.0,
    'sky_flat_sun_altitude': -9.0,
    // The daemon's fault policies, which the client neither reads nor edits
    // (kept as served and sent back on a Save).
    'hot_reconnect_enabled': true,
    'on_camera_lost': 'reconnect_then_pause',
    'on_mount_lost': 'reconnect_then_abort_park',
    'on_tracking_lost': 'reenable_then_pause',
    'switch_value_tolerance_pct': 5.0,
  },
  'site': {
    'site_name': 'Backyard',
    'latitude_deg': 0.0,
    'longitude_deg': 0.0,
    'elevation_m': 0.0,
    'time_zone': 'UTC',
    'use_custom_horizon': false,
    'default_horizon_altitude_deg': 20.0,
    'bortle_class': 6,
    'typical_seeing_arcsec': 2.5,
    'twilight_definition': 'astronomical',
    'max_sequence_runtime_min': 0,
    'soft_warning_altitude_deg': 30.0,
    'observer_name': '',
    'sqm_mag_per_arcsec2': 0.0,
    'permanent_pier': false,
  },
  'unguided': {
    'guard_enabled': true,
    'eccentricity_tolerance': 0.5,
    'good_eccentricity': 0.3,
    'step_down_after': 2,
    'step_up_after': 10,
    'probe_rungs_seconds': null, // the daemon's own ladder
    'recentre_drift_arcmin': 10,
    'recentre_solve_every_n': 1,
    'prior_tolerance_basis': 'max',
  },
};

/// A whole storage section as the rig serves it, for a test that writes it
/// (the wizard): every key is there, so the strict read succeeds.
const rigStorage = <String, Object?>{
  'save_directory': '/media/openastroara',
  'file_format': 'fits',
  'compression': 'zstd',
  'filename_template':
      r'$$TARGETNAME$$\\$$DATEMINUS12$$\\$$IMAGETYPE$$\\$$DATETIME$$_$$FILTER$$_$$EXPOSURETIME$$s',
  'min_free_disk_warn_gb': 10,
  'min_free_disk_critical_gb': 2,
  'backup_retention_count': 20,
  'fault_log_retention_days': 90,
};

/// Wait (real time, at most 2 s) until [done]: the notifiers load on their
/// own, over a client that answers on later event-loop turns.
Future<void> settle(bool Function() done) async {
  for (var i = 0; i < 200 && !done(); i++) {
    await Future<void>.delayed(const Duration(milliseconds: 10));
  }
}
