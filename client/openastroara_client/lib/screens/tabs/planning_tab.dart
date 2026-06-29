import 'package:flutter/material.dart';

import '../../widgets/sky_atlas/stellarium_view.dart';

/// Planning tab (§25.5 + §36) — the embedded **Stellarium Web Engine**
/// planetarium where the user finds a target, frames it, and sends it to the
/// sequence.
///
/// Most planning UI — the time controls and the framing overlay — lives *inside*
/// the self-driven planetarium page (`assets/stellarium/index.html`), not in
/// Flutter chrome: the native webview has no working Dart→page JS bridge, so the
/// page drives the engine in context and talks to the daemon's REST API itself.
/// [StellariumView] wraps that renderer with the Flutter chrome that needs the
/// keyboard or the daemon: a universal **search** bar, and the docked
/// **Tonight's Sky** panel (§36.8) that toggles in beside the planetarium. Those
/// reach the page over the `StellariumServer` loopback (search + a `goto`
/// recentre command), not a JS bridge. (The old Aladin-era bars — survey picker,
/// Data Manager, "no sky imagery" banner, the Flutter time/frame bars — were
/// removed when the planetarium moved to Stellarium.)
class PlanningTab extends StatelessWidget {
  const PlanningTab({super.key});

  @override
  Widget build(BuildContext context) => const StellariumView();
}
