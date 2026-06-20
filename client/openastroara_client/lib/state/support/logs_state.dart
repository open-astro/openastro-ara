import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/server.dart';
import '../../services/logs_api.dart';
import '../saved_server_state.dart';

/// Factory for a [LogsClient] over a given server. Overridden in widget tests
/// with a fake so the Support tab can be driven without a daemon.
final logsApiFactoryProvider = Provider<LogsClient Function(AraServer)>(
  (ref) => LogsApi.new,
);

/// A [LogsClient] bound to the active server, or null when none is connected.
final logsApiProvider = Provider<LogsClient?>((ref) {
  final server = ref.watch(activeServerProvider);
  if (server == null) return null;
  final api = ref.watch(logsApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});
