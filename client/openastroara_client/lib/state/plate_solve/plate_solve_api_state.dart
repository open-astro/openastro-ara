import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/server.dart';
import '../../services/plate_solve_api.dart';
import '../saved_server_state.dart';

/// Test seam: how a [PlateSolveApi] is built for the active server.
final plateSolveApiFactoryProvider =
    Provider<PlateSolveApi Function(AraServer)>((ref) => PlateSolveApi.new);

/// The active server's plate-solve API, or null with no server.
final plateSolveApiProvider = Provider<PlateSolveApi?>((ref) {
  final server = ref.watch(activeServerProvider);
  if (server == null) return null;
  return ref.watch(plateSolveApiFactoryProvider)(server);
});
