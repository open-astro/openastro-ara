import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/tabs/planning_tab.dart';
import 'package:openastroara/widgets/sky_atlas/stellarium_view.dart';

// Widget test for the Planning tab (PORT_DECISIONS §36/§25.5). The tab is now a
// thin, full-bleed wrapper around the embedded Stellarium Web Engine: all
// planning UI (universal search, Tonight's Sky, time controls, framing overlay)
// lives inside the self-driven page (`assets/stellarium/index.html`), not in
// Flutter chrome — so the old Aladin-era header/survey/frame tests no longer
// apply. We assert the tab's one job: that it builds the planetarium renderer.
//
// We call `build()` directly rather than mounting the tab, because mounting
// StellariumView runs its initState — which spins up a real loopback HTTP server
// and a CEF/WKWebView platform view that aren't available (and leak timers) in
// the headless test env. Calling build() exercises the tab's contract without
// that I/O.
void main() {
  testWidgets('builds the full-bleed Stellarium planetarium renderer',
      (tester) async {
    late Widget built;
    await tester.pumpWidget(
      MaterialApp(
        home: Builder(
          builder: (context) {
            built = const PlanningTab().build(context);
            return const SizedBox.shrink();
          },
        ),
      ),
    );
    expect(built, isA<StellariumView>());
  });
}
