import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/simple/simple_mount_limits.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/settings/safety_policies_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/state/simple_mode_state.dart';

/// The rig's site and safety sections; a write can be made to fail.
class _Rig extends ProfileApi {
  _Rig({required this.site, required this.safety})
    : super(const AraServer(hostname: '192.0.2.10', port: 5000));
  SiteSettings site;
  SafetyPolicies safety;
  bool failPut = false;

  int _version = 1;
  String get _etag => '"v$_version"';

  // The sections load from the rig and save back with their tag (ADR 0015).
  @override
  Future<({SiteSettings value, String? etag})> getSiteSection() async =>
      (value: site, etag: _etag);
  @override
  Future<({SiteSettings value, String? etag})> putSiteSection(
    SiteSettings value, {
    String? ifMatch,
  }) async {
    if (failPut) throw StateError('rig unreachable');
    _version++;
    return (value: site = value, etag: _etag);
  }

  @override
  Future<({SafetyPolicies value, String? etag})> getSafetySection() async =>
      (value: safety, etag: _etag);
  @override
  Future<({SafetyPolicies value, String? etag})> putSafetySection(
    SafetyPolicies value, {
    String? ifMatch,
  }) async {
    if (failPut) throw StateError('rig unreachable');
    _version++;
    return (value: safety = value, etag: _etag);
  }
}

Future<(ProviderContainer, _Rig)> _pump(
  WidgetTester tester, {
  double lowest = 20,
  bool measured = false,
  bool flipAuto = true,
  int pause = 0,
  int after = 0,
  int stopBefore = 0,
}) async {
  final rig = _Rig(
    site: SiteSettings(
      latitudeDeg: -33.856784,
      longitudeDeg: 151.215297,
      defaultHorizonAltitudeDeg: lowest,
      useCustomHorizon: measured,
    ),
    safety: SafetyPolicies(
      meridianFlipAuto: flipAuto,
      meridianPauseMin: pause,
      minutesAfterMeridian: after,
      stopBeforeMeridianMin: stopBefore,
    ),
  );
  final c = ProviderContainer(
    overrides: [
      profileApiProvider.overrideWithValue(rig),
      simpleModeActiveProvider.overrideWithValue(true), // errors go to a pill
    ],
  );
  addTearDown(c.dispose);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: c,
      child: const MaterialApp(
        home: Scaffold(
          body: SingleChildScrollView(
            child: SizedBox(width: 400, child: SimpleMountLimits()),
          ),
        ),
      ),
    ),
  );
  await tester.pumpAndSettle();
  return (c, rig);
}

String _value(WidgetTester tester, String id) =>
    tester.widget<Text>(find.byKey(Key('$id-value'))).data!;

bool _enabled(WidgetTester tester, String key) =>
    tester.widget<IconButton>(find.byKey(Key(key))).onPressed != null;

void main() {
  testWidgets(
    'a measured horizon makes Lowest altitude read-only and says where to change it',
    (tester) async {
      await _pump(tester, measured: true);
      expect(find.text('Your measured horizon'), findsOneWidget);
      expect(
        find.text('Change it in Advanced Mode › Settings › Site.'),
        findsOneWidget,
      );
      expect(find.byKey(const Key('simple-mount-lowest-up')), findsNothing);
      expect(find.byKey(const Key('simple-mount-lowest-down')), findsNothing);
    },
  );

  testWidgets(
    'the flip switch swaps At the meridian for Stop before meridian',
    (tester) async {
      final (c, _) = await _pump(tester, flipAuto: true, after: 5);
      expect(find.text('At the meridian'), findsOneWidget);
      expect(find.text('Stop before meridian'), findsNothing);
      expect(_value(tester, 'simple-mount-meridian'), '5 min after');
      c.read(safetyPoliciesProvider.notifier).setMeridianFlipAuto(false);
      await tester.pump();
      expect(find.text('At the meridian'), findsNothing);
      expect(find.text('Stop before meridian'), findsOneWidget);
      expect(_value(tester, 'simple-mount-stop-before'), 'Off');
    },
  );

  testWidgets('a tap saves at once to the rig with a sentence pill', (
    tester,
  ) async {
    final (c, rig) = await _pump(tester, lowest: 20, after: 5);
    await tester.tap(find.byKey(const Key('simple-mount-lowest-up')));
    await tester.pumpAndSettle();
    expect(rig.site.defaultHorizonAltitudeDeg, 25);
    expect(
      rig.site.latitudeDeg,
      -33.856784,
      reason: 'the rest of the site is kept',
    );
    expect(_value(tester, 'simple-mount-lowest'), '25°');
    await tester.tap(find.byKey(const Key('simple-mount-meridian-down')));
    await tester.pumpAndSettle();
    expect(
      (rig.safety.meridianPauseMin, rig.safety.minutesAfterMeridian),
      (0, 0),
    );
    expect(c.read(simpleMessagesProvider).map((m) => m.title), [
      'Flips at the meridian',
      'Lowest altitude 25°',
    ]);
    await tester.pump(kSimpleResultLife); // the pills fade
  });

  testWidgets('the steppers stop at their ends', (tester) async {
    await _pump(tester, lowest: 60, flipAuto: true, pause: 30);
    expect(_enabled(tester, 'simple-mount-lowest-up'), isFalse);
    expect(_enabled(tester, 'simple-mount-lowest-down'), isTrue);
    expect(_value(tester, 'simple-mount-meridian'), '30 min before');
    expect(_enabled(tester, 'simple-mount-meridian-down'), isFalse);
    expect(_enabled(tester, 'simple-mount-meridian-up'), isTrue);
  });

  testWidgets(
    'the other ends: lowest 0, flip after 30, stop-before Off and 30',
    (tester) async {
      final (c, _) = await _pump(tester, lowest: 0, after: 30);
      expect(_enabled(tester, 'simple-mount-lowest-down'), isFalse);
      expect(_enabled(tester, 'simple-mount-meridian-up'), isFalse);
      c.read(safetyPoliciesProvider.notifier).setMeridianFlipAuto(false);
      await tester.pump();
      expect(
        _enabled(tester, 'simple-mount-stop-before-down'),
        isFalse,
        reason: 'Off is the lowest',
      );
      for (var i = 0; i < 6; i++) {
        await tester.tap(find.byKey(const Key('simple-mount-stop-before-up')));
        await tester.pumpAndSettle();
      }
      expect(_value(tester, 'simple-mount-stop-before'), '30 min');
      expect(_enabled(tester, 'simple-mount-stop-before-up'), isFalse);
      await tester.pump(kSimpleResultLife); // the pills fade
    },
  );

  testWidgets(
    'a failed save puts the old value back and says it could not save',
    (tester) async {
      final (c, rig) = await _pump(tester, lowest: 20);
      rig.failPut = true;
      await tester.tap(find.byKey(const Key('simple-mount-lowest-up')));
      await tester.pumpAndSettle();
      expect(_value(tester, 'simple-mount-lowest'), '20°');
      expect(c.read(siteSettingsProvider).value!.defaultHorizonAltitudeDeg, 20);
      final pill = c.read(simpleMessagesProvider).single;
      expect(
        (pill.kind, pill.title),
        (SimpleMessageKind.error, "Couldn't save the limit"),
      );
    },
  );

  testWidgets('each stepper button says what it changes', (tester) async {
    await _pump(tester);
    expect(find.bySemanticsLabel('Raise lowest altitude'), findsOneWidget);
    expect(find.bySemanticsLabel('Lower lowest altitude'), findsOneWidget);
    expect(find.bySemanticsLabel('Later at the meridian'), findsOneWidget);
    expect(find.bySemanticsLabel('Earlier at the meridian'), findsOneWidget);
    final up = tester.getSize(find.byKey(const Key('simple-mount-lowest-up')));
    expect(up.width >= 44 && up.height >= 44, isTrue, reason: '44 pt targets');
  });
}
