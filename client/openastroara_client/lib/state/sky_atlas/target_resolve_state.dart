import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/server.dart';
import '../../services/target_resolve_api.dart';
import '../saved_server_state.dart';

/// Builds the resolver client for a server; tests override it with a fake.
final targetResolveApiFactoryProvider =
    Provider<TargetResolveClient Function(AraServer)>(
      (ref) => TargetResolveApi.new,
    );

/// [TargetResolveClient] bound to the active server, or null when none is
/// saved (the search then falls back to the planetarium's own engine).
final targetResolveApiProvider = Provider<TargetResolveClient?>((ref) {
  final server = ref.watch(activeServerProvider);
  if (server == null) return null;
  final api = ref.watch(targetResolveApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});
