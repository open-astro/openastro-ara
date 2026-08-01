import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/guiding_autotune.dart';
import '../../models/server.dart';
import '../../services/guiding_autotune_api.dart';
import '../saved_server_state.dart';

final guidingAutoTuneApiFactoryProvider =
    Provider<GuidingAutoTuneClient Function(AraServer)>((ref) => GuidingAutoTuneApi.new);

final guidingAutoTuneApiProvider = Provider<GuidingAutoTuneClient?>((ref) {
  final server = ref.watch(activeServerProvider);
  if (server is! AraServer) return null;
  final api = ref.watch(guidingAutoTuneApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

final guidingAutoTuneCapabilitiesProvider =
    FutureProvider<GuidingAutoTuneCapabilities?>((ref) async {
  final api = ref.watch(guidingAutoTuneApiProvider);
    return api?.getCapabilities();
});

class GuidingAutoTuneNotifier extends AsyncNotifier<GuidingAutoTuneStatus?> {
  Timer? _pollTimer;
  bool _pollInFlight = false;

  @override
  Future<GuidingAutoTuneStatus?> build() async {
    ref.onDispose(() => _pollTimer?.cancel());
    final api = ref.watch(guidingAutoTuneApiProvider);
    final status = await api?.getLatest();
    if (status != null && _isActive(status.state)) _beginPolling();
    return status;
  }

  Future<void> refresh() async {
    final api = ref.read(guidingAutoTuneApiProvider);
    if (api == null) return;
    final result = await AsyncValue.guard(api.getLatest);
    if (ref.mounted) state = result;
  }

  Future<void> start({
    String depth = 'standard',
    bool dryRun = true,
    bool useMainCameraValidation = false,
  }) async {
    final api = ref.read(guidingAutoTuneApiProvider);
    if (api == null) return;
    state = const AsyncLoading();
    state = await AsyncValue.guard(() => api.start(
          depth: depth,
          dryRun: dryRun,
          useMainCameraValidation: useMainCameraValidation,
        ));
    if (!dryRun && state.asData?.value?.state != null) _beginPolling();
  }

  Future<void> apply() async => _run((api) => api.apply());
  Future<void> cancel() async => _run((api) => api.cancel());
  Future<void> rollback() async => _run((api) => api.rollback());

  Future<GuidingAutoTuneReport?> report() async {
    final api = ref.read(guidingAutoTuneApiProvider);
    return api?.getReport();
  }

  Future<void> _run(Future<GuidingAutoTuneStatus> Function(GuidingAutoTuneClient) action) async {
    final api = ref.read(guidingAutoTuneApiProvider);
    if (api == null) return;
    state = await AsyncValue.guard(() => action(api));
    if (state.asData?.value?.state == 'Proposed' || state.asData?.value?.state == 'Completed' ||
        state.asData?.value?.state == 'RolledBack' || state.asData?.value?.state == 'Failed') {
      _pollTimer?.cancel();
    }
  }

  void _beginPolling() {
    _pollTimer?.cancel();
    _pollTimer = Timer.periodic(const Duration(seconds: 2), (_) async {
      if (_pollInFlight) return;
      final api = ref.read(guidingAutoTuneApiProvider);
      if (api == null) return;
      _pollInFlight = true;
      try {
        final result = await AsyncValue.guard(api.getLatest);
        if (!ref.mounted) return;
        state = result;
        final terminal = result.asData?.value.state;
        if (terminal == 'Proposed' || terminal == 'Completed' || terminal == 'RolledBack' || terminal == 'Failed') {
          _pollTimer?.cancel();
        }
      } finally {
        _pollInFlight = false;
      }
    });
  }

  static bool _isActive(String state) =>
      !const {'idle', 'Proposed', 'Completed', 'RolledBack', 'Failed'}
          .contains(state);
}

final guidingAutoTuneProvider =
    AsyncNotifierProvider<GuidingAutoTuneNotifier, GuidingAutoTuneStatus?>(
        GuidingAutoTuneNotifier.new);
