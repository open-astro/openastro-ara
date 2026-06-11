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

/// The stretched preview JPEG bytes for a given frame id. autoDispose so the
/// bytes are released when the viewer moves on to the next frame.
final framePreviewProvider =
    FutureProvider.autoDispose.family<Uint8List, String>((ref, id) async {
  final servers = ref.watch(savedServersProvider).maybeWhen(
        data: (list) => list,
        orElse: () => const <AraServer>[],
      );
  if (servers.isEmpty) {
    throw StateError('Not connected to a server.');
  }
  return FramesApi(servers.last).preview(id);
});
