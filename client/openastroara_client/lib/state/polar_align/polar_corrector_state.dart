import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/polar_align.dart';
import '../../models/server.dart';
import '../../services/polar_align_api.dart';
import '../equipment/equipment_device_state.dart' show EquipmentDeviceNotifier;
import '../saved_server_state.dart';
import 'polar_align_state.dart';

/// Builds a [PolarCorrectorClient] for a server. Overridable in tests.
final polarCorrectorApiFactoryProvider =
    Provider<PolarCorrectorClient Function(AraServer)>((ref) => PolarCorrectorApi.new);

/// [PolarCorrectorClient] bound to the active server, or null when none is saved.
final polarCorrectorApiProvider = Provider<PolarCorrectorClient?>((ref) {
  final server = ref.watch(activeServerProvider);
  if (server == null) return null;
  final api = ref.watch(polarCorrectorApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// #1971 the rig's Polar Corrector as the daemon derives it; null with no rig
/// or a daemon that predates it. Read when a screen that shows it opens
/// (autoDispose), after each of its commands, and when the routine's corrector
/// stops, converges or measures (the moves changed the position). The run
/// itself comes from [polarAlignLiveProvider], never from this read.
class PolarCorrectorNotifier extends AsyncNotifier<PolarCorrector?> {
  Timer? _settle;
  int _settlePolls = 0;

  @override
  Future<PolarCorrector?> build() async {
    final api = ref.watch(polarCorrectorApiProvider);
    ref.onDispose(() => _settle?.cancel());
    if (api == null) return null;
    ref.listen(polarAlignLiveProvider.select((l) => l.corrector?.state), (prev, next) {
      if (prev != next &&
          (next == PolarCorrectorStates.stopped ||
              next == PolarCorrectorStates.converged ||
              next == PolarCorrectorStates.measuring)) {
        unawaited(refresh());
      }
    });
    return api.get();
  }

  /// A failed read keeps what was shown.
  Future<void> refresh() async {
    final api = ref.read(polarCorrectorApiProvider);
    if (api == null) return;
    try {
      final next = await api.get();
      if (ref.mounted) state = AsyncData(next);
    } catch (_) {}
  }

  /// Home, then follow it as the device panels follow a move: a read every
  /// [EquipmentDeviceNotifier.settleInterval] until it stops, capped.
  Future<void> home() async {
    await ref.read(polarCorrectorApiProvider)?.home();
    _settlePolls = 0;
    await _follow();
  }

  Future<void> stop() async {
    await ref.read(polarCorrectorApiProvider)?.stop();
    await refresh();
  }

  Future<void> stopInRoutine() async => ref.read(polarCorrectorApiProvider)?.stopInRoutine();

  Future<void> resumeInRoutine() async => ref.read(polarCorrectorApiProvider)?.resumeInRoutine();

  Future<void> _follow() async {
    _settle?.cancel();
    await refresh();
    if (!ref.mounted || state.value?.moving != true) return;
    if (++_settlePolls >= EquipmentDeviceNotifier.maxSettlePolls) return;
    _settle = Timer(EquipmentDeviceNotifier.settleInterval, () {
      if (ref.mounted) unawaited(_follow());
    });
  }
}

final polarCorrectorProvider =
    AsyncNotifierProvider.autoDispose<PolarCorrectorNotifier, PolarCorrector?>(
      PolarCorrectorNotifier.new,
    );
