import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/polar_align.dart';
import '../../models/server.dart';
import '../../services/polar_align_api.dart';
import '../saved_server_state.dart';
import 'polar_align_state.dart';

/// Builds a [PolarAlignMotorDirectionClient] for a server. Overridable in tests.
final polarAlignMotorDirectionApiFactoryProvider =
    Provider<PolarAlignMotorDirectionClient Function(AraServer)>(
      (ref) => PolarAlignMotorDirectionApi.new,
    );

/// #2081 the RA motor direction Polar Align saved for this profile's mount,
/// for Settings › Imaging › Polar align. Null with no rig, or a rig that
/// predates it. Read when the panel opens (autoDispose) and again when a run
/// ends (its 0.5° test may have saved one); [retest] forgets it, so the next
/// start tests the direction again.
class PolarAlignMotorDirectionNotifier
    extends AsyncNotifier<PolarAlignMotorDirectionState?> {
  @override
  Future<PolarAlignMotorDirectionState?> build() async {
    final server = ref.watch(activeServerProvider);
    if (server == null) return null;
    ref.listen(polarAlignLiveProvider.select((l) => l.isActive), (prev, next) {
      if (prev == true && !next) unawaited(_reload());
    });
    return _read(server);
  }

  Future<PolarAlignMotorDirectionState?> _read(AraServer server) async {
    final api = ref.read(polarAlignMotorDirectionApiFactoryProvider)(server);
    try {
      return await api.get();
    } finally {
      api.close();
    }
  }

  /// A failed read keeps what was shown.
  Future<void> _reload() async {
    final server = ref.read(activeServerProvider);
    if (server == null) return;
    try {
      final next = await _read(server);
      if (ref.mounted) state = AsyncData(next);
    } catch (_) {}
  }

  /// "Test direction again": forget the saved direction on the rig. Throws
  /// when the rig refuses or cannot be reached; nothing is shown as forgotten
  /// then.
  Future<void> retest() async {
    final server = ref.read(activeServerProvider);
    if (server == null) throw StateError('No active server');
    final api = ref.read(polarAlignMotorDirectionApiFactoryProvider)(server);
    try {
      await api.retest();
    } finally {
      api.close();
    }
    if (!ref.mounted) return;
    final matches = state.value?.matchesConnectedMount;
    state = AsyncData(
      PolarAlignMotorDirectionState(
        matchesConnectedMount: matches == null ? null : false,
      ),
    );
  }
}

final polarAlignMotorDirectionProvider =
    AsyncNotifierProvider.autoDispose<
      PolarAlignMotorDirectionNotifier,
      PolarAlignMotorDirectionState?
    >(PolarAlignMotorDirectionNotifier.new);
