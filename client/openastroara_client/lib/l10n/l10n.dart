import 'package:flutter/widgets.dart';

import 'app_localizations.dart';

export 'app_localizations.dart';

/// The app's strings in the device's language. Falls back to English where no
/// localizations are set up above [context] (widget tests' plain MaterialApp).
extension L10nContext on BuildContext {
  AppLocalizations get l10n =>
      AppLocalizations.of(this) ?? lookupAppLocalizations(const Locale('en'));
}
