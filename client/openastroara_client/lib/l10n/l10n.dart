import 'package:flutter/widgets.dart';

import 'app_localizations.dart';

export 'app_localizations.dart';

/// The app's strings in the device's language. Falls back to English where no
/// localizations are set up above [context] (widget tests' plain MaterialApp).
/// The app's strings outside a widget (a notifier's message): the language
/// picked in Settings, else the device's, else English.
AppLocalizations l10nFor(Locale? picked) {
  try {
    return lookupAppLocalizations(picked ?? WidgetsBinding.instance.platformDispatcher.locale);
  } catch (_) {
    return lookupAppLocalizations(const Locale('en'));
  }
}

extension L10nContext on BuildContext {
  AppLocalizations get l10n =>
      AppLocalizations.of(this) ?? lookupAppLocalizations(const Locale('en'));
}
