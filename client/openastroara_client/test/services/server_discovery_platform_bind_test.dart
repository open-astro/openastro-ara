import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/server_discovery_service.dart';

void main() {
  // #1129: multicast_dns binds with reusePort: true, which dart:io rejects on
  // Android, so the browse died in start() every pass there.
  group('platformDatagramBind', () {
    late List<Map<String, Object?>> calls;

    Future<RawDatagramSocket> fakeBind(dynamic host, int port,
        {bool reuseAddress = true, bool reusePort = false, int ttl = 1}) {
      calls.add({'host': host, 'port': port, 'reuseAddress': reuseAddress, 'reusePort': reusePort, 'ttl': ttl});
      return Future.error(const SocketException('fake'));
    }

    setUp(() => calls = []);

    Future<void> bindLikeMulticastDns(bool isAndroid) async {
      final bind = ServerDiscoveryService.platformDatagramBind(isAndroid, bind: fakeBind);
      await expectLater(
        bind(InternetAddress.anyIPv4, 5353, reuseAddress: true, reusePort: true, ttl: 255),
        throwsA(isA<SocketException>()),
      );
    }

    test('drops reusePort on Android and keeps everything else', () async {
      await bindLikeMulticastDns(true);
      expect(calls.single['reusePort'], isFalse);
      expect(calls.single['reuseAddress'], isTrue);
      expect(calls.single['port'], 5353);
      expect(calls.single['ttl'], 255);
    });

    test('passes reusePort through elsewhere', () async {
      await bindLikeMulticastDns(false);
      expect(calls.single['reusePort'], isTrue);
    });
  });

  // The production wiring, not just the helper: a hard-coded flag here once
  // passed the tests above while Android kept the refused bind (#1129 review).
  group('productionDatagramBind', () {
    late bool? sawReusePort;
    Future<RawDatagramSocket> record(dynamic host, int port,
        {bool reuseAddress = true, bool reusePort = false, int ttl = 1}) {
      sawReusePort = reusePort;
      return Future.error(const SocketException('fake'));
    }

    tearDown(() => debugDefaultTargetPlatformOverride = null);

    for (final (platform, expected) in [
      (TargetPlatform.android, false),
      (TargetPlatform.iOS, true),
      (TargetPlatform.macOS, true),
    ]) {
      test('$platform binds with reusePort: $expected', () async {
        debugDefaultTargetPlatformOverride = platform;
        sawReusePort = null;
        final bind = ServerDiscoveryService.productionDatagramBind(bind: record);
        await expectLater(
          bind(InternetAddress.anyIPv4, 5353, reusePort: true),
          throwsA(isA<SocketException>()),
        );
        expect(sawReusePort, expected);
      });
    }
  });
}
