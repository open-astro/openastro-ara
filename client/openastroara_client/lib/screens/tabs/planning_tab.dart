import 'package:flutter/material.dart';

import '../../widgets/sky_atlas/stellarium_view.dart';

/// Planning tab (§25.5 + §36) — the embedded **Stellarium Web Engine**
/// planetarium where the user finds a target, frames it, and sends it to the
/// sequence.
///
/// All planning UI — universal search, Tonight's Sky, the time controls, and the
/// framing overlay — lives *inside* the self-driven planetarium page
/// (`assets/stellarium/index.html`), not in Flutter chrome. The multi-process CEF
/// webview has no working Dart→page JS bridge, so the page drives the engine in
/// context and talks to the daemon's REST API itself; this tab is just the
/// full-bleed renderer. (The old Aladin-era bars — survey picker, Data Manager,
/// "no sky imagery" banner, Flutter search/time/frame bars — were removed when
/// the planetarium moved to Stellarium.)
class PlanningTab extends StatelessWidget {
  const PlanningTab({super.key});

  @override
  Widget build(BuildContext context) => const StellariumView();
}
