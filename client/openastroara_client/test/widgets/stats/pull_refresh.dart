import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/widgets/stats/stats_pull_refresh.dart';

/// What the Stats page's pull down does: runs every section's refresh. The
/// caller pumps after, as it did after a tap.
void pullRefresh(WidgetTester tester) {
  final container = ProviderScope.containerOf(tester.element(find.byType(Scaffold).first));
  for (final refresh in [...container.read(statsPullRefreshersProvider)]) {
    unawaited(refresh());
  }
}
