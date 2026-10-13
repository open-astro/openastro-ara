import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/library/mosaic.dart';
import '../../models/server.dart';
import '../../services/mosaic_api.dart';
import '../saved_server_state.dart';

/// Builds a [MosaicClient] for a server. Overridable in tests.
final mosaicApiFactoryProvider = Provider<MosaicClient Function(AraServer)>(
  (ref) => MosaicApi.new,
);

/// [MosaicClient] bound to the active server ([activeServerProvider]), or
/// `null` when no server is saved. Not autoDispose: the run creator reads it
/// once at the end of a long create and must not find a closed Dio (#1272).
final mosaicApiProvider = Provider<MosaicClient?>((ref) {
  final server = ref.watch(activeServerProvider);
  if (server == null) return null;
  final api = ref.watch(mosaicApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// The active server's mosaic projects with per-panel progress (§47, #1146).
/// `null` data = no server saved; an empty list = none framed yet. The Library
/// hides its Mosaics section in both cases.
class MosaicProjectsNotifier extends AsyncNotifier<List<MosaicProject>?> {
  @override
  Future<List<MosaicProject>?> build() async {
    final api = ref.watch(mosaicApiProvider);
    if (api == null) return null;
    return api.fetchProjects();
  }

  /// Re-reads the projects (after a frame lands, a project is created, or a
  /// pull-to-refresh). Keeps the old list on a failed read.
  Future<void> refresh() async {
    final api = ref.read(mosaicApiProvider);
    if (api == null) return;
    try {
      state = AsyncData(await api.fetchProjects());
    } catch (_) {
      // stale list stays; the next refresh tries again
    }
  }

  Future<void> delete(String id) async {
    final api = ref.read(mosaicApiProvider);
    if (api == null) return;
    await api.delete(id);
    await refresh();
  }
}

final mosaicProjectsProvider =
    AsyncNotifierProvider<MosaicProjectsNotifier, List<MosaicProject>?>(
      MosaicProjectsNotifier.new,
    );
