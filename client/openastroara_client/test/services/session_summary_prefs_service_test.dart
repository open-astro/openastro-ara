import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/session_summary_prefs_service.dart';

void main() {
  late Directory tmp;
  setUp(
    () async =>
        tmp = await Directory.systemTemp.createTemp('session_summaries'),
  );
  tearDown(() => tmp.delete(recursive: true));

  test(
    'round-trips acknowledged run ids, caps the list, survives garbage',
    () async {
      final svc = SessionSummaryPrefsService(supportDir: () async => tmp);
      expect(await svc.loadAcknowledged(), isEmpty);
      await svc.saveAcknowledged({'a', 'b'});
      expect(await svc.loadAcknowledged(), {'a', 'b'});
      await svc.saveAcknowledged({for (var i = 0; i < 70; i++) 'r$i'});
      final kept = await svc.loadAcknowledged();
      expect(kept, hasLength(SessionSummaryPrefsService.cap));
      expect(kept, contains('r69'));
      expect(kept, isNot(contains('r0')));
      await File('${tmp.path}/session_summaries.json').writeAsString('nope');
      expect(await svc.loadAcknowledged(), isEmpty);
    },
  );
}
