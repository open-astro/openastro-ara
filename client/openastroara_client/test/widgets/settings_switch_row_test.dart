import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter/semantics.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/theme/ara_colors.dart';
import 'package:openastroara/theme/ara_theme.dart';
import 'package:openastroara/widgets/settings/editable_field.dart';

double _contrast(Color fg, Color bg) {
  final a = fg.computeLuminance(), b = bg.computeLuminance();
  return (math.max(a, b) + 0.05) / (math.min(a, b) + 0.05);
}

// A registry key whose entry has related settings (so the sheet has a caption).
const _helpKey = 'session.notifications.alarm_delay';

Future<void> _pump(WidgetTester t, {bool value = false}) async {
  await t.pumpWidget(
    ProviderScope(
      child: MaterialApp(
        theme: buildAraTheme(),
        home: Scaffold(
          backgroundColor: AraColors.bgPanel,
          body: SettingsSwitchRow(
            label: 'Alarm delay',
            hint: 'Wait before sounding',
            helpKey: _helpKey,
            value: value,
            onChanged: (_) {},
          ),
        ),
      ),
    ),
  );
}

Color? _textColor(WidgetTester t, String text) =>
    t.widget<Text>(find.text(text)).style?.color ??
    DefaultTextStyle.of(t.element(find.text(text))).style.color;

void main() {
  testWidgets(
    'the switch row hint, info icon and help caption read at 4.5:1 on the panel',
    (t) async {
      await _pump(t);
      expect(
        _contrast(_textColor(t, 'Wait before sounding')!, AraColors.bgPanel),
        greaterThanOrEqualTo(4.5),
      );
      final icon = t.widget<Icon>(find.byIcon(Icons.info_outline));
      expect(
        _contrast(icon.color!, AraColors.bgPanel),
        greaterThanOrEqualTo(4.5),
      );
      await t.tap(find.byIcon(Icons.info_outline));
      await t.pumpAndSettle();
      expect(
        _contrast(_textColor(t, 'Related settings:')!, AraColors.bgPanel),
        greaterThanOrEqualTo(4.5),
      );
    },
  );

  testWidgets(
    'the switch is named by the label and described by the hint, each read once',
    (t) async {
      final h = t.ensureSemantics();
      await _pump(t);
      final sw = t.getSemantics(find.byType(Switch));
      expect(sw.label, 'Alarm delay');
      expect(sw.hint, 'Wait before sounding');
      expect(find.bySemanticsLabel('Alarm delay'), findsOneWidget);
      expect(find.bySemanticsLabel('Wait before sounding'), findsNothing);
      h.dispose();
    },
  );

  testWidgets(
    'the help icon stays its own node and tapping it does not toggle the switch',
    (t) async {
      var toggled = false;
      final h = t.ensureSemantics();
      await t.pumpWidget(
        ProviderScope(
          child: MaterialApp(
            theme: buildAraTheme(),
            home: Scaffold(
              body: SettingsSwitchRow(
                label: 'Alarm delay',
                helpKey: _helpKey,
                value: false,
                onChanged: (_) => toggled = true,
              ),
            ),
          ),
        ),
      );
      final help = t.getSemantics(find.byIcon(Icons.info_outline));
      expect(help.tooltip, isNotEmpty);
      expect(help.getSemanticsData().hasAction(SemanticsAction.tap), isTrue);
      await t.tap(find.byIcon(Icons.info_outline));
      await t.pumpAndSettle();
      expect(toggled, isFalse);
      h.dispose();
    },
  );

  testWidgets('the help sheet close button is named', (t) async {
    final h = t.ensureSemantics();
    await _pump(t);
    await t.tap(find.byIcon(Icons.info_outline));
    await t.pumpAndSettle();
    final close = t.getSemantics(find.byIcon(Icons.close));
    expect(close.getSemanticsData().tooltip, 'Close');
    h.dispose();
  });
}
