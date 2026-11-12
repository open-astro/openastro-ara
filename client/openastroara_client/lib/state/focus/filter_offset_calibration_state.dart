import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/filter_offset_calibration.dart';
import '../../models/server.dart';
import '../../services/filter_offset_calibration_api.dart';
import '../equipment/filter_wheel_state.dart';
import '../saved_server_state.dart';
import 'autofocus_live_state.dart';

/// Builds the #1675 offset calibration client. Overridable in tests.
final filterOffsetCalibrationApiFactoryProvider =
    Provider<FilterOffsetCalibrationApi Function(AraServer)>(
      (ref) =>
          (server) => DioFilterOffsetCalibrationApi(server),
    );

/// The offset calibration client bound to the **active** server, or `null`.
final filterOffsetCalibrationApiProvider = Provider<FilterOffsetCalibrationApi?>((ref) {
  final server = ref.watch(activeServerProvider);
  if (server == null) return null;
  final api = ref.watch(filterOffsetCalibrationApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// What the Offsets routine renders: the daemon's calibration (null when it
/// has none), the user's picks (null: the defaults), a request in flight, the
/// last request error, and whether this session just saved one.
class FilterOffsetLive {
  final FilterOffsetCalibration? calibration;
  final Set<int>? picks;
  final bool busy;
  final Object? error;
  final bool saved;

  const FilterOffsetLive({this.calibration, this.picks, this.busy = false, this.error, this.saved = false});

  static const idle = FilterOffsetLive();

  bool get isRunning => calibration?.isRunning ?? false;

  FilterOffsetLive copyWith({
    FilterOffsetCalibration? calibration,
    bool clearCalibration = false,
    Set<int>? picks,
    bool? busy,
    Object? error,
    bool clearError = false,
    bool? saved,
  }) => FilterOffsetLive(
    calibration: clearCalibration ? null : (calibration ?? this.calibration),
    picks: picks ?? this.picks,
    busy: busy ?? this.busy,
    error: clearError ? null : (error ?? this.error),
    saved: saved ?? this.saved,
  );
}

/// Drives the Offsets routine. The daemon owns the run: its record is read on
/// start-up, polled every 2 s while it runs (as the autofocus run tracker is),
/// and re-read when an autofocus run starts or ends (each measurement is one,
/// so a calibration started elsewhere shows up). NOT autoDispose — results
/// stay on view when the user comes back.
class FilterOffsetCalibrationNotifier extends Notifier<FilterOffsetLive> {
  Timer? _poll;
  bool _refreshing = false;
  int _generation = 0;
  static const _pollInterval = Duration(seconds: 2);

  @override
  FilterOffsetLive build() {
    _generation++;
    _poll?.cancel();
    _poll = null;
    ref.onDispose(() => _poll?.cancel());
    final api = ref.watch(filterOffsetCalibrationApiProvider);
    if (api == null) return FilterOffsetLive.idle;
    ref.listen(autofocusLiveProvider.select((l) => l.run.isRunning), (_, _) => unawaited(refresh()));
    Future<void>.microtask(refresh);
    return FilterOffsetLive.idle;
  }

  /// Re-read the daemon's calibration.
  Future<void> refresh() async {
    if (_refreshing) return;
    final api = ref.read(filterOffsetCalibrationApiProvider);
    if (api == null) return;
    final gen = _generation;
    _refreshing = true;
    try {
      final calibration = await api.calibration();
      if (!ref.mounted || gen != _generation) return;
      state = state.copyWith(calibration: calibration, clearCalibration: calibration == null, clearError: true);
      _armPoll(calibration?.isRunning ?? false);
    } catch (e) {
      if (!ref.mounted || gen != _generation) return;
      state = state.copyWith(error: e);
      _armPoll(state.isRunning);
    } finally {
      _refreshing = false;
    }
  }

  void _armPoll(bool running) {
    _poll?.cancel();
    _poll = null;
    if (running) _poll = Timer(_pollInterval, () => unawaited(refresh()));
  }

  /// The filters to measure this time (the panel passes the defaults first).
  void setPicks(Set<int> picks) => state = state.copyWith(picks: picks);

  Future<void> _act(Future<void> Function(FilterOffsetCalibrationApi api) action, {bool saved = false}) async {
    final api = ref.read(filterOffsetCalibrationApiProvider);
    if (api == null || state.busy) return;
    state = state.copyWith(busy: true, clearError: true, saved: false);
    try {
      await action(api);
      if (!ref.mounted) return;
      state = state.copyWith(busy: false, saved: saved);
      await refresh();
    } catch (e) {
      if (!ref.mounted) return;
      state = state.copyWith(busy: false, error: e);
    }
  }

  /// Start a calibration over [positions]; a refusal's reason lands in error.
  Future<void> start(List<int> positions) => _act((api) => api.start(positions));

  Future<void> cancel() => _act((api) => api.cancel());

  /// Keep the measured offsets; the wheel is re-read so its window shows them.
  Future<void> save() => _act((api) async {
    await api.save();
    unawaited(ref.read(filterWheelProvider.notifier).refresh());
  }, saved: true);

  Future<void> discard() => _act((api) => api.discard());
}

final filterOffsetCalibrationProvider =
    NotifierProvider<FilterOffsetCalibrationNotifier, FilterOffsetLive>(FilterOffsetCalibrationNotifier.new);
