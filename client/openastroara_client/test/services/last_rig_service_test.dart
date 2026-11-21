import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/last_rig_service.dart';

void main() {
  test('remembers the last rig name across instances; nothing or junk reads as null', () async {
    final dir = await Directory.systemTemp.createTemp('last_rig');
    addTearDown(() => dir.delete(recursive: true));
    LastRigService svc() => LastRigService(supportDir: () async => dir);
    expect(await svc().load(), isNull);
    await svc().save(const LastRig('astro.lan', uuid: 'u1'));
    expect(await svc().load(), const LastRig('astro.lan', uuid: 'u1'));
    // A file from before uuids were kept: the name alone.
    await File('${dir.path}/last_rig.json').writeAsString('{"name":"astro.lan"}');
    expect(await svc().load(), const LastRig('astro.lan'));
    await File('${dir.path}/last_rig.json').writeAsString('not json');
    expect(await svc().load(), isNull);
  });
}
