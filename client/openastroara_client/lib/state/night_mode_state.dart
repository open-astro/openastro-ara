import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';

/// Build-time selected night-mode rendering: `'overlay'` (a red colour filter
/// laid over the normal dark UI) or `'theme'` (a full red Material 3 theme).
/// Choose at build time with `--dart-define=NIGHT_MODE_KIND=overlay|theme`;
/// both share the same toggle, hotkey and persistence. Defaults to `overlay`.
const String nightModeKind =
    String.fromEnvironment('NIGHT_MODE_KIND', defaultValue: 'overlay');

/// Whether the active build renders night mode as a red overlay (vs a theme).
bool get isNightModeOverlay => nightModeKind == 'overlay';

/// Persisted "night mode" UI preference (off by default). Stored via
/// flutter_secure_storage so it survives relaunches; the rendering approach is
/// the build-time [nightModeKind], so toggling just flips the flag.
class NightModeController extends AsyncNotifier<bool> {
  static const String _key = 'night_mode_enabled';
  static const FlutterSecureStorage _storage = FlutterSecureStorage();

  @override
  Future<bool> build() async {
    try {
      final v = await _storage.read(key: _key);
      return v == '1';
    } catch (_) {
      // Keyring unavailable (e.g. Linux without a secret service) — degrade to
      // off rather than failing the whole app.
      return false;
    }
  }

  Future<void> set(bool on) async {
    state = AsyncData(on);
    try {
      await _storage.write(key: _key, value: on ? '1' : '0');
    } catch (_) {
      // Non-persistent session is fine; the in-memory state is already set.
    }
  }

  Future<void> toggle() =>
      set(!(switch (state) { AsyncData(:final value) => value, _ => false }));
}

final nightModeProvider =
    AsyncNotifierProvider<NightModeController, bool>(NightModeController.new);
