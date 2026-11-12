import 'dart:math' as math;
import 'dart:ui';

import 'package:flutter/material.dart' show FilterChip, InkWell, MaterialApp, Scaffold, TargetPlatform, Text, ThemeData, VisualDensity;
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/simple/simple_shell.dart' show kSimpleGoStyle, simpleTheme;
import 'package:openastroara/theme/ara_colors.dart';

/// [fg] over [bg], WCAG ratio.
double _contrast(Color fg, Color bg) {
  final a = fg.computeLuminance(), b = bg.computeLuminance();
  return (math.max(a, b) + 0.05) / (math.min(a, b) + 0.05);
}

void main() {
  // Simple Mode's panels float over the frame, so their text has to read
  // over whatever is behind: a dark sky, or a flat, the Moon, daylight.
  for (final (name, frame) in const [
    ('a dark sky', Color(0xFF0A0A0A)),
    ('a mid-grey frame', Color(0xFF808080)),
    ('a white frame', Color(0xFFFFFFFF)),
  ]) {
    test('panel text reads at 4.5:1 over $name', () {
      final panel = Color.alphaBlend(AraColors.overlayScrim, frame);
      expect(_contrast(AraColors.textPrimary, panel), greaterThanOrEqualTo(4.5));
      expect(_contrast(AraColors.textSecondary, panel), greaterThanOrEqualTo(4.5));
      expect(_contrast(AraColors.errorText, panel), greaterThanOrEqualTo(4.5));
      // Fields, presets and the mode grid's tiles sit on tile-fill on a panel.
      final tile = Color.alphaBlend(AraColors.tileFill, panel);
      expect(_contrast(AraColors.textSecondary, tile), greaterThanOrEqualTo(4.5));
    });
  }

  // #1782: the status card's kind dots and outlines are non-text marks (3:1) on the same scrim.
  for (final (name, frame) in const [
    ('a dark sky', Color(0xFF0A0A0A)),
    ('a mid-grey frame', Color(0xFF808080)),
  ]) {
    test('the status card marks read at 3:1 over $name', () {
      final card = Color.alphaBlend(AraColors.overlayScrim, frame);
      for (final c in [AraColors.accentConnected, AraColors.accentWarning, AraColors.accentError, AraColors.accentBusy]) {
        expect(_contrast(c, card), greaterThanOrEqualTo(3), reason: '$c');
      }
    });
  }

  test('selected tiles (the mode grid, Tonight\'s best) read at 4.5:1: white on the blue', () {
    expect(_contrast(AraColors.selectionFg, AraColors.selectionBg), greaterThanOrEqualTo(4.5));
  });

  test('the green go buttons (Start Preflight, Done, Create and start) read at 4.5:1', () {
    final fg = kSimpleGoStyle.foregroundColor!.resolve({})!;
    final bg = kSimpleGoStyle.backgroundColor!.resolve({})!;
    expect(bg, AraColors.accentConnected);
    expect(_contrast(fg, bg), greaterThanOrEqualTo(4.5));
  });

  test('on desktop Simple Mode keeps standard density, so buttons stay 44 px', () {
    for (final p in [TargetPlatform.macOS, TargetPlatform.windows, TargetPlatform.linux]) {
      expect(simpleTheme(ThemeData(platform: p)).visualDensity, VisualDensity.standard, reason: '$p');
    }
  });

  // #1791: a chip's ink area, not only its outer box, is a 44 px target.
  for (final p in [TargetPlatform.macOS, TargetPlatform.windows, TargetPlatform.linux, TargetPlatform.iOS]) {
    testWidgets('a Simple Mode chip is at least 44 px tall, ink included, on $p', (tester) async {
      await tester.pumpWidget(
        MaterialApp(
          theme: simpleTheme(ThemeData(platform: p)),
          home: const Scaffold(body: FilterChip(label: Text('30 s'), selected: false, onSelected: null)),
        ),
      );
      expect(tester.getSize(find.byType(FilterChip)).height, greaterThanOrEqualTo(44));
      expect(tester.getSize(find.descendant(of: find.byType(FilterChip), matching: find.byType(InkWell))).height, greaterThanOrEqualTo(44));
    });
  }
}
