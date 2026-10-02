import 'dart:async';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/ws_event_stream.dart';

void main() {
  // #1129, seen on a Pixel: a dial to a rig that isn't there failed through
  // the stream (handled) AND through WebSocketChannel.ready (unhandled), so
  // every reconnect logged "Uncaught error".
  test('a failed dial surfaces no uncaught error', () async {
    // A port nothing listens on: bind, note it, close.
    final probe = await ServerSocket.bind(InternetAddress.loopbackIPv4, 0);
    final port = probe.port;
    await probe.close();

    final uncaught = <Object>[];
    final streamErrors = <Object>[];
    final done = Completer<void>();
    runZonedGuarded(() {
      final socket = defaultWsConnect(Uri.parse('ws://127.0.0.1:$port/api/v1/ws'), const {});
      void finish() {
        if (!done.isCompleted) done.complete();
      }
      socket.stream.listen((_) {}, onError: (Object e) {
        streamErrors.add(e);
        finish();
      }, onDone: finish, cancelOnError: true);
    }, (error, _) => uncaught.add(error));

    await done.future.timeout(const Duration(seconds: 5), onTimeout: () {});
    // Let a late `ready` failure reach the zone if it is going to.
    await Future<void>.delayed(const Duration(milliseconds: 300));
    expect(streamErrors, isNotEmpty, reason: 'the dial did fail');
    expect(uncaught, isEmpty);
  });
}
