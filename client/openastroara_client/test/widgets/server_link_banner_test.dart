import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/ws/ws_providers.dart';
import 'package:openastroara/widgets/server_link_banner.dart';

void main() {
  Future<void> pump(WidgetTester tester, bool linkUp) => tester.pumpWidget(
        ProviderScope(
          overrides: [serverLinkUpProvider.overrideWithValue(linkUp)],
          child: const MaterialApp(home: Scaffold(body: ServerLinkBanner())),
        ),
      );

  testWidgets('hidden while the link is up', (tester) async {
    await pump(tester, true);
    expect(find.byKey(const ValueKey('server-link-banner')), findsNothing);
  });

  testWidgets('one line says the link is lost and status is stale', (tester) async {
    await pump(tester, false);
    expect(find.byKey(const ValueKey('server-link-banner')), findsOneWidget);
    expect(find.textContaining('Server link lost'), findsOneWidget);
    expect(find.textContaining('stale'), findsOneWidget);
  });
}
