import 'dart:async';

import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/server_relocator.dart';

// #1129: a saved rig that stopped answering at its saved address is found
// again anywhere on the network, by its server_uuid.
void main() {
  const saved = AraServer(
      hostname: '192.168.1.234', port: 5555, mdnsName: 'openastro', serverUuid: 'uuid-pi4');

  ServerRelocator relocator(
    List<AraServer> candidates, {
    Map<String, String?> uuids = const {},
    bool neverCloses = false,
    Duration timeout = const Duration(seconds: 5),
    List<String>? asked,
  }) =>
      ServerRelocator(
        discoverEverything: () {
          final c = StreamController<AraServer>();
          for (final s in candidates) {
            c.add(s);
          }
          if (!neverCloses) c.close();
          return c.stream;
        },
        uuidOf: (s) async {
          asked?.add(s.hostname);
          return uuids[s.hostname];
        },
        timeout: timeout,
      );

  test('finds the rig at its new address by uuid, ignoring a same-name rig', () async {
    final asked = <String>[];
    final found = await relocator(
      const [
        AraServer(hostname: '192.168.1.123', port: 5555, mdnsName: 'openastro'), // the other Pi
        AraServer(hostname: '192.168.1.235', port: 5555, mdnsName: 'openastro'),
      ],
      uuids: {'192.168.1.123': 'uuid-pi5', '192.168.1.235': 'uuid-pi4'},
      asked: asked,
    ).relocate(saved);
    expect(found?.hostname, '192.168.1.235');
    expect(found?.serverUuid, 'uuid-pi4');
    expect(found?.mdnsName, 'openastro');
  });

  test('never probes the saved address itself, and uses a uuid the sweep already read', () async {
    final asked = <String>[];
    final found = await relocator(
      const [
        AraServer(hostname: '192.168.1.234', port: 5555),
        AraServer(hostname: '192.168.1.40', port: 5555, serverUuid: 'uuid-pi4'),
      ],
      asked: asked,
    ).relocate(saved);
    expect(found?.hostname, '192.168.1.40');
    expect(asked, isEmpty);
  });

  test('a rig that is nowhere on the network is null, not a guess', () async {
    final found = await relocator(
      const [AraServer(hostname: '192.168.1.123', port: 5555, mdnsName: 'openastro')],
      uuids: {'192.168.1.123': 'uuid-pi5'},
    ).relocate(saved);
    expect(found, isNull);
  });

  test('an entry saved without a uuid matches by name only when one rig has it', () async {
    const legacy = AraServer(hostname: '192.168.1.234', port: 5555, mdnsName: 'openastro');
    final one = await relocator(
      const [AraServer(hostname: '192.168.1.235', port: 5555, mdnsName: 'openastro')],
      uuids: {'192.168.1.235': 'uuid-pi4'},
    ).relocate(legacy);
    expect(one?.hostname, '192.168.1.235');
    expect(one?.serverUuid, 'uuid-pi4', reason: 'the match records the uuid for next time');

    final two = await relocator(
      const [
        AraServer(hostname: '192.168.1.235', port: 5555, mdnsName: 'openastro'),
        AraServer(hostname: '192.168.1.123', port: 5555, mdnsName: 'openastro'),
      ],
      uuids: {'192.168.1.235': 'uuid-pi4', '192.168.1.123': 'uuid-pi5'},
    ).relocate(legacy);
    expect(two, isNull, reason: 'two rigs named openastro: never pick one blindly');
  });

  test('a search that never ends gives up at the timeout', () async {
    final sw = Stopwatch()..start();
    final found = await relocator(
      const [AraServer(hostname: '192.168.1.9', port: 5555)],
      neverCloses: true,
      timeout: const Duration(milliseconds: 300),
    ).relocate(saved);
    expect(found, isNull);
    expect(sw.elapsed, lessThan(const Duration(seconds: 5)));
  });
}
