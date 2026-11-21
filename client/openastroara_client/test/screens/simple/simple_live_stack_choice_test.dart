import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/l10n/app_localizations_en.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/simple/simple_live_stack_row.dart';
import 'package:openastroara/services/simple_mode_prefs_service.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/simple_live_stack_state.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/theme/ara_colors.dart';

import '../../helpers/fake_session_live_stack_client.dart';

class _MemPrefs extends SimpleModePrefsService {
  bool value = false;
  @override
  Future<bool> load() async => value;
  @override
  Future<void> save(bool enabled) async => value = enabled;
}

Future<ProviderContainer> _pump(
  WidgetTester tester,
  FakeSessionLiveStackClient daemon,
) async {
  final c = ProviderContainer(
    overrides: [
      activeServerProvider.overrideWithValue(
        const AraServer(hostname: '192.0.2.10', port: 5000),
      ),
      sessionLiveStackApiFactoryProvider.overrideWithValue((_) => daemon),
      simpleLiveStackViewPrefsProvider.overrideWithValue(_MemPrefs()),
    ],
  );
  addTearDown(c.dispose);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: c,
      child: MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: const Scaffold(
          body: Center(
            child: SizedBox(width: 248, child: SimpleLiveStackRow()),
          ),
        ),
      ),
    ),
  );
  await tester.pump();
  await tester.pump();
  return c;
}

Color? _fill(WidgetTester tester, String id) => tester
    .widget<Material>(
      find
          .ancestor(
            of: find.byKey(Key('simple-live-stack-view-$id')),
            matching: find.byType(Material),
          )
          .first,
    )
    .color;

void main() {
  testWidgets('the chosen view is filled with the selection tint and the '
      'other with the tile fill, not Material chips', (tester) async {
    await _pump(tester, FakeSessionLiveStackClient()..enabled = true);
    expect(find.byType(ChoiceChip), findsNothing);
    expect(
      (_fill(tester, 'stack'), _fill(tester, 'last')),
      (AraColors.selectionTint, AraColors.tileFill),
    );
    await tester.tap(find.byKey(const Key('simple-live-stack-view-last')));
    await tester.pump();
    expect(
      (_fill(tester, 'stack'), _fill(tester, 'last')),
      (AraColors.tileFill, AraColors.selectionTint),
    );
  });

  testWidgets('a failed Live Stack start shows one message pill', (
    tester,
  ) async {
    final c = await _pump(
      tester,
      FakeSessionLiveStackClient()..startError = StateError('refused'),
    );
    expect(c.read(simpleMessagesProvider), isEmpty);
    await tester.tap(find.byKey(const Key('simple-live-stack-switch')));
    await tester.pump();
    final pills = c.read(simpleMessagesProvider);
    expect(pills, hasLength(1));
    expect(
      (pills.single.kind, pills.single.title),
      (
        SimpleMessageKind.error,
        AppLocalizationsEn().simpleLiveStackCouldntStart,
      ),
    );
  });
}
