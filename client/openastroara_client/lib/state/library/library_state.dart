import 'package:flutter_riverpod/flutter_riverpod.dart';

/// Library grouping state per playbook §40. The 12f.1/12g.2 in-memory demo
/// sessions that used to live here are gone — the Image Library renders the
/// live catalog (12f.2, `live_library_state.dart`) and the Stats CSVs stream
/// from `/api/v1/stats/export/csv` (§50).

enum LibraryGrouping { bySession, byTarget, byDate }

class LibraryGroupingNotifier extends Notifier<LibraryGrouping> {
  @override
  LibraryGrouping build() => LibraryGrouping.bySession;
  void set(LibraryGrouping g) => state = g;
}

final libraryGroupingProvider =
    NotifierProvider<LibraryGroupingNotifier, LibraryGrouping>(
        LibraryGroupingNotifier.new);
