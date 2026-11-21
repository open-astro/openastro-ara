import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/profile_api.dart';

/// One write that reached the rig's store.
typedef LandedPut = ({String section, Object? body, String? ifMatch});

/// A rig at the HTTP level for any profile section (ADR 0015): it serves each
/// section under `/api/v1/profile/<section>` with an `ETag`, refuses a PUT
/// whose `If-Match` names another copy with a 412 (nothing written), and still
/// takes a PUT with no `If-Match`, as the daemon does until a version is
/// required. [puts] holds only the writes that landed, so a test can check the
/// rig's side whatever shape the client's section notifiers take.
class FakeSectionRig implements HttpClientAdapter {
  FakeSectionRig(Map<String, Object?> sections) : _sections = {...sections};

  static const _prefix = '/api/v1/profile/';

  final Map<String, Object?> _sections;
  final Map<String, int> _versions = {};

  /// Sections whose GET fails as an unreachable rig does.
  final Set<String> failGet = {};

  /// Each entry fails one PUT to its section with a 500 (nothing written).
  final List<String> failPut = [];

  /// Section -> a key its PUT reply leaves out (the write still lands).
  final Map<String, String> dropFromReply = {};

  /// Every write that landed, in order.
  final List<LandedPut> puts = [];

  /// Every `If-Match` sent with a PUT (null when none), landed or refused.
  final List<String?> ifMatches = [];

  /// Every section a GET asked for, answered or failed, in order.
  final List<String> gets = [];

  /// Section -> a completer its next GET waits on before answering (a GET
  /// that fails by [failGet] does not wait). The test completes it.
  final Map<String, Completer<void>> holdGet = {};

  /// Section -> a completer its next PUT waits on before it is checked and
  /// lands (a PUT still in flight). The test completes it.
  final Map<String, Completer<void>> holdPut = {};

  /// Path after `/api/v1/profile/` -> what a POST there does on the rig (a
  /// daemon-owned write such as `safety/first-flip/rearm`); returns the reply.
  final Map<String, Object? Function()> onPost = {};

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
    final post = onPost[section];
    if (post != null && options.method.toUpperCase() == 'POST') {
      return _json(post(), 200);
    }
    if (!_sections.containsKey(section)) {
      return _json({'title': 'no such section'}, 404);
    }
    switch (options.method.toUpperCase()) {
      case 'GET':
        gets.add(section);
        if (failGet.contains(section)) {
          throw DioException(
            requestOptions: options,
            type: DioExceptionType.connectionError,
            error: 'rig unreachable',
          );
        }
        final hold = holdGet.remove(section);
        if (hold != null) await hold.future;
        return _json(_sections[section], 200, etag: etagOf(section));
      case 'PUT':
        final held = holdPut.remove(section);
        if (held != null) await held.future;
        final ifMatch = options.headers['if-match'] as String?;
        ifMatches.add(ifMatch);
        if (failPut.remove(section)) return _json({'title': 'rig error'}, 500);
        if (ifMatch != null && ifMatch != '*' && ifMatch != etagOf(section)) {
          return _json({'title': 'Precondition Failed'}, 412);
        }
        puts.add((section: section, body: options.data, ifMatch: ifMatch));
        _sections[section] = options.data;
        savedElsewhere(section);
        final drop = dropFromReply[section];
        final reply = drop == null
            ? options.data
            : ({...options.data as Map}..remove(drop));
        return _json(reply, 200, etag: etagOf(section));
      default:
        return _json({'title': 'method not allowed'}, 405);
    }
  }

  static ResponseBody _json(Object? body, int status, {String? etag}) =>
      ResponseBody.fromString(
        jsonEncode(body),
        status,
        headers: {
          Headers.contentTypeHeader: [Headers.jsonContentType],
          if (etag != null) 'etag': [etag],
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
    'step_size_auto': true,
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
  'optics': {
    'focal_length_mm': 0,
    'reducer_factor': 1.0,
    'sensor_width_px': 0,
    'sensor_height_px': 0,
    'pixel_size_um': 0,
    'aperture_mm': 0,
    'telescope_name': '',
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

/// Wait (real time, at most 2 s) until [done]: the notifiers load on their
/// own, over a client that answers on later event-loop turns.
Future<void> settle(bool Function() done) async {
  for (var i = 0; i < 200 && !done(); i++) {
    await Future<void>.delayed(const Duration(milliseconds: 10));
  }
}

/// A section that loads itself starts its GET while a widget builds, inside
/// the test's fake clock, and Dio's request pipeline takes several timer turns
/// to finish it. Turn them, so none is pending when the test ends.
Future<void> flushLoads(WidgetTester tester) async {
  for (var i = 0; i < 10; i++) {
    await tester.pump(const Duration(milliseconds: 10));
  }
}
