import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

/// The Run toolbar folds utilities into a "More" menu when narrow; give it a
/// desktop-width surface so every button is inline for tests that tap them
/// by label. Forgetting this reads as a silent "button not found".
Future<void> wideSurface(WidgetTester tester) async {
  await tester.binding.setSurfaceSize(const Size(2000, 800));
  addTearDown(() => tester.binding.setSurfaceSize(null));
}
