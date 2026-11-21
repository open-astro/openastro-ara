import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/server.dart';
import '../../services/bug_report_api.dart';
import '../saved_server_state.dart';

/// Factory for a [BugReportClient] over a given server. Overridden in widget
/// tests with a fake.
final bugReportApiFactoryProvider = Provider<BugReportClient Function(AraServer)>(
  (ref) => BugReportApi.new,
);

/// A [BugReportClient] bound to the active server, or null when none is
/// connected. autoDispose so the Dio is released when nothing watches it.
final bugReportApiProvider = Provider.autoDispose<BugReportClient?>((ref) {
  final server = ref.watch(activeServerProvider);
  if (server == null) return null;
  final api = ref.watch(bugReportApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});
