import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/client_error_log.dart';
import 'package:openastroara/widgets/support/client_log_card.dart';

/// Records the same error twice from inside its own build — what a widget
/// that throws the same assertion on consecutive rebuilds does through
/// FlutterError.onError — while the card (a status listener) is mounted.
class _ThrowsTwiceInBuild extends StatelessWidget {
  const _ThrowsTwiceInBuild(this.log);
  final ClientErrorLog log;

  @override
  Widget build(BuildContext context) {
    log.record('flutter_error', 'same assertion');
    log.record('flutter_error', 'same assertion');
    return const SizedBox.shrink();
  }
}

void main() {
  testWidgets('a repeated error recorded mid-build does not break the frame',
      (tester) async {
    final log = ClientErrorLog(
      supportDir: () async => throw const FileSystemException('no disk'),
      appVersion: () async => 'v',
    );
    await tester.pumpWidget(ProviderScope(
      overrides: [clientErrorLogProvider.overrideWithValue(log)],
      child: MaterialApp(
        home: Scaffold(
          body: Column(children: [
            const ClientLogCard(savePathPicker: null),
            _ThrowsTwiceInBuild(log),
          ]),
        ),
      ),
    ));
    // A synchronous status write from the second record() would surface
    // here as "setState() or markNeedsBuild() called during build".
    expect(tester.takeException(), isNull);
    await tester.pump();
    expect(tester.takeException(), isNull);
    expect(find.byType(ClientLogCard), findsOneWidget);
  });
}
