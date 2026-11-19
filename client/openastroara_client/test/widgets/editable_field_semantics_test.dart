import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/theme/ara_theme.dart';
import 'package:openastroara/widgets/settings/editable_field.dart';

Future<void> _pump(WidgetTester t, Widget row) => t.pumpWidget(
  MaterialApp(
    theme: buildAraTheme(),
    home: Scaffold(body: row),
  ),
);

void main() {
  testWidgets('a text row names its field by the row label', (t) async {
    final handle = t.ensureSemantics();
    await _pump(
      t,
      EditableTextRow(
        label: 'Observer name',
        currentValue: 'Ada',
        getCanonical: () => 'Ada',
        parse: (_) {},
      ),
    );
    expect(find.bySemanticsLabel('Observer name'), findsOneWidget);
    final node = t.getSemantics(find.bySemanticsLabel('Observer name'));
    expect(node.flagsCollection.isTextField, isTrue);
    expect(node.value, 'Ada');
    handle.dispose();
  });

  testWidgets('a number row names its field by the row label', (t) async {
    final handle = t.ensureSemantics();
    await _pump(
      t,
      EditableNumberRow(
        label: 'Alarm delay (s)',
        currentValue: '60',
        getCanonical: () => '60',
        parse: (_) {},
        helpKey: null,
      ),
    );
    expect(find.bySemanticsLabel('Alarm delay (s)'), findsOneWidget);
    final node = t.getSemantics(find.bySemanticsLabel('Alarm delay (s)'));
    expect(node.flagsCollection.isTextField, isTrue);
    expect(node.value, '60');
    handle.dispose();
  });

  testWidgets('a dropdown row names its button by the row label', (t) async {
    final handle = t.ensureSemantics();
    await _pump(
      t,
      SettingsDropdownRow<int>(
        label: 'Temperature unit',
        value: 1,
        items: const {1: 'Celsius', 2: 'Fahrenheit'},
        onChanged: (_) {},
      ),
    );
    final finder = find.bySemanticsLabel(RegExp('^Temperature unit'));
    expect(finder, findsOneWidget);
    expect(t.getSemantics(finder).flagsCollection.isButton, isTrue);
    handle.dispose();
  });
}
