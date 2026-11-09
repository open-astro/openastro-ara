import 'package:flutter/material.dart';

/// Flutter's licenses page over LicenseRegistry: every bundled package's
/// LICENSE is registered by the build tooling automatically.
void showAppLicenses(BuildContext context, String? version) {
  showLicensePage(
    context: context,
    applicationName: 'OpenAstro Ara',
    applicationVersion: version,
    applicationLegalese:
        'Client: AGPL-3.0 · Ara: MPL-2.0\nForked from N.I.N.A. (MPL-2.0)',
  );
}
