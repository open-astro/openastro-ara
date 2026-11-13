import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/widgets/sky_atlas/planetarium_shown_listener.dart';

void main() {
  testWidgets(
    'tells the page when it shows and when it is put away, once each',
    (tester) async {
      final told = <bool>[];
      Future<void> pump(bool shown) => tester.pumpWidget(
        TickerMode(
          enabled: shown,
          child: PlanetariumShownListener(
            onChanged: told.add,
            child: const SizedBox(),
          ),
        ),
      );
      // Simple Mode loads the sky offstage, its tickers off.
      await pump(false);
      expect(told, [false]);
      await pump(true);
      await pump(true);
      expect(told, [false, true], reason: 'a rebuild says nothing new');
      await pump(false);
      expect(told, [false, true, false]);
    },
  );
}
