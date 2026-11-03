import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/l10n.dart';

void main() {
  final en = lookupAppLocalizations(const Locale('en'));

  test('one hidden message is one message to a screen reader', () {
    expect(en.pillsMoreSemantics(1), '1 more message');
    expect(en.pillsMoreSemantics(3), '3 more messages');
  });
}
