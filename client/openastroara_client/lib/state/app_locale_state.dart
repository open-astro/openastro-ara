import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// The language picked in Settings › Rig, or null for the device's own.
// shortcut: held for the session only (a testing aid while pt-BR is drafted);
// persist it like the night-mode pref if it stays.
class AppLocaleController extends Notifier<Locale?> {
  @override
  Locale? build() => null;

  void set(Locale? locale) => state = locale;
}

final appLocaleProvider = NotifierProvider<AppLocaleController, Locale?>(
  AppLocaleController.new,
);
