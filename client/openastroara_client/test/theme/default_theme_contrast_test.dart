import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/simple/simple_shell.dart' show simpleTheme;
import 'package:openastroara/theme/ara_colors.dart';
import 'package:openastroara/theme/ara_theme.dart';

/// [fg] over [bg], WCAG ratio.
double _contrast(Color fg, Color bg) {
  final a = fg.computeLuminance(), b = bg.computeLuminance();
  return (math.max(a, b) + 0.05) / (math.min(a, b) + 0.05);
}

const _surfaces = <(String, Color)>[
  ('bg-panel', AraColors.bgPanel),
  ('bg-panel-alt', AraColors.bgPanelAlt),
  ('bg-input', AraColors.bgInput),
];

/// The colour a [TextButton]'s label resolves to under [theme].
Future<Color?> _textButtonLabel(WidgetTester tester, ThemeData theme) async {
  await tester.pumpWidget(
    MaterialApp(
      theme: theme,
      home: Scaffold(
        body: TextButton(onPressed: () {}, child: const Text('Cancel')),
      ),
    ),
  );
  return DefaultTextStyle.of(tester.element(find.text('Cancel'))).style.color;
}

void main() {
  // #1843 A3: the default text button took the theme primary (Console Blue, 2.6:1).
  for (final (name, bg) in _surfaces) {
    test('the default theme text-button label reads at 4.5:1 on $name', () {
      final fg = buildAraTheme().textButtonTheme.style?.foregroundColor
          ?.resolve({});
      expect(
        fg,
        isNotNull,
        reason: 'the theme sets no text-button label colour',
      );
      expect(_contrast(fg!, bg), greaterThanOrEqualTo(4.5));
    });

    // #1843 A4: the theme error was Fault Red (3.6:1 on the panel).
    test('the default theme error colour reads at 4.5:1 as text on $name', () {
      expect(
        _contrast(buildAraTheme().colorScheme.error, bg),
        greaterThanOrEqualTo(4.5),
      );
    });
  }

  testWidgets('a text button under the default theme is drawn in link-text', (
    tester,
  ) async {
    expect(await _textButtonLabel(tester, buildAraTheme()), AraColors.linkText);
  });

  // Passes on current code: A3 must not repaint the primary filled button.
  test('the primary filled button stays Console Blue', () {
    expect(
      buildAraTheme().filledButtonTheme.style?.backgroundColor?.resolve({}),
      AraColors.buttonPrimary,
    );
  });

  // Simple Mode inherits A4 on purpose and keeps its own text-button style.
  for (final (name, frame) in const [
    ('a dark sky', Color(0xFF0A0A0A)),
    ('a white frame', Color(0xFFFFFFFF)),
  ]) {
    test(
      'Simple Mode error colour is error-text and reads at 4.5:1 over $name',
      () {
        final error = simpleTheme(buildAraTheme()).colorScheme.error;
        expect(error, AraColors.errorText);
        expect(
          _contrast(error, Color.alphaBlend(AraColors.overlayScrim, frame)),
          greaterThanOrEqualTo(4.5),
        );
      },
    );
  }

  // Passes on current code: Simple Mode's own override must not change.
  testWidgets('Simple Mode text-button label stays link-text', (tester) async {
    expect(
      await _textButtonLabel(tester, simpleTheme(buildAraTheme())),
      AraColors.linkText,
    );
  });
}
