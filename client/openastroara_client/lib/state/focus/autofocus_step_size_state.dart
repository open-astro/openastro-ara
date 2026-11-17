import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/autofocus_api.dart';
import '../../l10n/app_localizations.dart';
import '../equipment/focuser_state.dart';

/// #1544 — the step size the daemon would sweep with next and where it comes
/// from, or null with no server. The daemon works from the SAVED profile, so a
/// panel invalidates this after Save.
final autofocusStepSizeProvider =
    FutureProvider.autoDispose<AutofocusStepSize?>((ref) async {
      final api = ref.watch(autofocusApiProvider);
      return api?.stepSize();
    });

/// "Derived: 26 steps, from half the critical focus zone".
String derivedStepSizeText(AppLocalizations l10n, AutofocusStepSize d) =>
    l10n.autofocusStepSizeDerived(d.stepSize, switch (d.source) {
      'manual' => l10n.autofocusStepSourceManual,
      'measured' => l10n.autofocusStepSourceMeasured,
      'cfz' => l10n.autofocusStepSourceCfz,
      _ => l10n.autofocusStepSourceDefault,
    });
