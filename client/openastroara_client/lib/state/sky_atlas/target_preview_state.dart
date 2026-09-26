import 'dart:typed_data';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/target_preview_service.dart';

final targetPreviewServiceProvider =
    Provider<TargetPreviewService>((ref) => TargetPreviewService());

/// Identity of one preview: the catalog id plus the coordinates and size the
/// cutout is framed on. A record so the family key compares by value.
typedef TargetPreviewKey = ({
  String id,
  double raDeg,
  double decDeg,
  double fieldDeg,
});

/// The DSS2 preview bytes for a target, cached on disk after the first
/// fetch; null = nothing cached and no network. Kept alive once resolved so
/// scrolling a row out and back doesn't refetch/re-read within a session.
final targetPreviewProvider =
    FutureProvider.family<Uint8List?, TargetPreviewKey>((ref, key) async {
  final svc = ref.watch(targetPreviewServiceProvider);
  return svc.load(
    id: key.id,
    raDeg: key.raDeg,
    decDeg: key.decDeg,
    fieldDeg: key.fieldDeg,
  );
});
