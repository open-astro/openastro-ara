import 'dart:typed_data';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/frames_api.dart';
import '../saved_server_state.dart';
import '../ws/ws_providers.dart';

/// The id of the most-recently-captured frame, set by the Imaging tab's
/// "Take One" once the daemon has finished writing it. The [FrameViewer]
/// watches this to render the latest preview.
class LastCapturedFrameId extends Notifier<String?> {
  @override
  String? build() {
    // Every `frame.complete` the daemon announces — a sequence's lights
    // included — so the viewer follows a running sequence instead of sitting
    // on the last manual shot (it showed nothing while the first real run
    // imaged, 2026-10-03). "Take One" still sets it directly below.
    ref.listen(wsEventsProvider, (prev, next) {
      final event = next.asData?.value;
      if (event == null || event.type != 'frame.complete') return;
      final frameId = event.payload['frame_id'];
      if (frameId is String && frameId.isNotEmpty) state = frameId;
    });
    return null;
  }

  void set(String id) => state = id;
}

final lastCapturedFrameIdProvider =
    NotifierProvider<LastCapturedFrameId, String?>(LastCapturedFrameId.new);

/// The stretched preview JPEG bytes for a given frame id. autoDispose so the
/// bytes are released when the viewer moves on to the next frame.
final framePreviewProvider =
    FutureProvider.autoDispose.family<Uint8List, String>((ref, id) async {
  // Snapshot the server at fetch time (read, not watch): a later server-list
  // change shouldn't re-fetch and blank the currently-displayed frame.
  final server = ref.read(activeServerProvider);
  if (server == null) {
    throw StateError('Not connected to a server.');
  }
  return FramesApi(server).preview(id);
});
