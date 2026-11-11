import '../l10n/l10n.dart';

/// The mount's drive rates in the order both presentations list them, by
/// their ASCOM names (#1649).
const kTrackingRateOrder = ['Sidereal', 'Lunar', 'Solar', 'King'];

/// A drive rate's name in the reader's language.
String trackingRateWord(AppLocalizations l10n, String rate) => switch (rate) {
  'Lunar' => l10n.trackingRateLunar,
  'Solar' => l10n.trackingRateSolar,
  'King' => l10n.trackingRateKing,
  _ => l10n.trackingRateSidereal,
};
