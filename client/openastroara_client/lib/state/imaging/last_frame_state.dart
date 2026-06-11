import 'dart:typed_data';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/server.dart';
import '../../services/frames_api.dart';
import '../saved_server_state.dart';

/// The id of the most-recently-captured frame, set by the Imaging tab's
/// "Take One" once the daemon has finished writing it. The [FrameViewer]
/// watches this to render the latest preview.
class LastCapturedFrameId extends Notifier<String?> {
  @override
  String? build() => null;

  void set(String id) => state = id;
}

final lastCapturedFrameIdProvider =
    NotifierProvider<LastCapturedFrameId, String?>(LastCapturedFrameId.new);

/// True while a "Take One" exposure + catalog-poll is in flight. The Imaging
/// tab disables the capture button on this so a double-tap can't start two
/// concurrent captures.
class CaptureInProgress extends Notifier<bool> {
  @override
  bool build() => false;

  void set(bool value) => state = value;
}

final captureInProgressProvider =
    NotifierProvider<CaptureInProgress, bool>(CaptureInProgress.new);

/// The stretched preview JPEG bytes for a given frame id. autoDispose so the
/// bytes are released when the viewer moves on to the next frame.
final framePreviewProvider =
    FutureProvider.autoDispose.family<Uint8List, String>((ref, id) async {
  // Snapshot the server at fetch time (read, not watch): a later server-list
  // change shouldn't re-fetch and blank the currently-displayed frame.
  final servers = ref.read(savedServersProvider).maybeWhen(
        data: (list) => list,
        orElse: () => const <AraServer>[],
      );
  if (servers.isEmpty) {
    throw StateError('Not connected to a server.');
  }
  return FramesApi(servers.last).preview(id);
});
