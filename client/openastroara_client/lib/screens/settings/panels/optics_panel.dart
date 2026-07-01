import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../services/camera_geometry_api.dart';
import '../../../services/profile_api.dart';
import '../../../state/saved_server_state.dart';
import '../../../state/settings/optics_settings_state.dart';
import '../../../theme/ara_colors.dart';
import '../../../widgets/settings/editable_field.dart';

/// §36/§25.5 Optics panel — the imaging-train geometry (focal length,
/// reducer/barlow, sensor size + pixel pitch) that drives the Planning tab's
/// Frame-mode FOV overlay. Values hydrate from the active server's `optics`
/// section on mount (#467) and persist back on Save; a live read-out shows the
/// resulting field of view so the user can sanity-check their rig. Mirrors the
/// Imaging Defaults panel's daemon round-trip.
class OpticsPanel extends ConsumerStatefulWidget {
  const OpticsPanel({super.key});

  @override
  ConsumerState<OpticsPanel> createState() => _OpticsPanelState();
}

class _OpticsPanelState extends ConsumerState<OpticsPanel> {
  bool _saving = false;
  bool _refreshing = false;
  String? _lastError;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) => _hydrate());
  }

  Future<void> _hydrate() async {
    final api = _api();
    if (api == null) return; // no active server — keep local defaults
    try {
      await ref.read(opticsSettingsProvider.notifier).hydrateFromServer(api);
    } catch (e) {
      if (mounted) setState(() => _lastError = 'Could not load saved values: $e');
    }
  }

  Future<void> _save() async {
    setState(() {
      _saving = true;
      _lastError = null;
    });
    final api = _api();
    final messenger = ScaffoldMessenger.of(context);
    if (api == null) {
      setState(() {
        _saving = false;
        _lastError = 'No active server — connect to a daemon first.';
      });
      messenger.showSnackBar(SnackBar(content: Text(_lastError!)));
      return;
    }
    try {
      await ref.read(opticsSettingsProvider.notifier).persistToServer(api);
      if (!mounted) return;
      messenger.showSnackBar(const SnackBar(content: Text('Optics saved to daemon.')));
    } catch (e) {
      if (!mounted) return;
      setState(() => _lastError = 'Save failed: $e');
      messenger.showSnackBar(SnackBar(content: Text(_lastError!)));
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  /// Pull the connected camera's sensor geometry into the form (and persist it).
  /// The daemon also caches this on connect; this is the on-demand re-pull.
  Future<void> _refreshFromCamera() async {
    final servers = ref.read(savedServersProvider).maybeWhen(
          data: (list) => list,
          orElse: () => const <dynamic>[],
        );
    final messenger = ScaffoldMessenger.of(context);
    if (servers.isEmpty) {
      messenger.showSnackBar(const SnackBar(content: Text('No active server — connect to a daemon first.')));
      return;
    }
    setState(() {
      _refreshing = true;
      _lastError = null;
    });
    try {
      final geometry = await CameraGeometryApi(servers.last).read();
      // ref.read on a disposed WidgetRef throws; bail if the panel went away mid-request.
      if (!mounted) return;
      if (geometry == null) {
        messenger.showSnackBar(const SnackBar(
            content: Text('Connect a camera that reports its sensor size first.')));
        return;
      }
      final n = ref.read(opticsSettingsProvider.notifier);
      n.setSensorWidthPx(geometry.sensorWidthPx);
      n.setSensorHeightPx(geometry.sensorHeightPx);
      n.setPixelSizeUm(geometry.pixelSizeUm);
      // Re-resolve the active server at the persist site (centralised in _api()) rather than reusing
      // the pre-await capture — the saved-server list may have changed during the camera read.
      final api = _api();
      if (api == null) {
        // The values are in the form, but the server vanished mid-read — be honest that nothing saved.
        messenger.showSnackBar(const SnackBar(
            content: Text('Read the camera, but no server is connected to save to.')));
        return;
      }
      await n.persistToServer(api);
      if (mounted) {
        messenger.showSnackBar(const SnackBar(content: Text('Filled sensor size from the connected camera.')));
      }
    } catch (e) {
      // Set the field without its own setState; the finally's setState rebuilds with it.
      _lastError = 'Could not read the camera: $e';
      if (mounted) messenger.showSnackBar(SnackBar(content: Text(_lastError!)));
    } finally {
      if (mounted) setState(() => _refreshing = false);
    }
  }

  ProfileApi? _api() {
    final servers = ref.read(savedServersProvider).maybeWhen(
          data: (list) => list,
          orElse: () => const [],
        );
    if (servers.isEmpty) return null;
    // Most-recently-saved server is the de-facto active one — same convention
    // as the other Settings panels (a dedicated active-server provider lands
    // with §55.1 multi-server switching).
    return ProfileApi(servers.last);
  }

  @override
  Widget build(BuildContext context) {
    final o = ref.watch(opticsSettingsProvider);
    final n = ref.read(opticsSettingsProvider.notifier);
    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        EditableNumberRow(
          label: 'Focal length (mm)',
          currentValue: _fmt(o.focalLengthMm),
          getCanonical: () => _fmt(ref.read(opticsSettingsProvider).focalLengthMm),
          parse: (s) {
            final v = double.tryParse(s);
            if (v != null) n.setFocalLengthMm(v);
          },
        ),
        EditableNumberRow(
          label: 'Reducer / barlow factor (×)',
          currentValue: _fmt(o.reducerFactor),
          getCanonical: () => _fmt(ref.read(opticsSettingsProvider).reducerFactor),
          parse: (s) {
            final v = double.tryParse(s);
            if (v != null) n.setReducerFactor(v);
          },
        ),
        EditableNumberRow(
          label: 'Aperture (mm)',
          currentValue: _fmt(o.apertureMm),
          getCanonical: () => _fmt(ref.read(opticsSettingsProvider).apertureMm),
          parse: (s) {
            final v = double.tryParse(s);
            if (v != null) n.setApertureMm(v);
          },
        ),
        EditableNumberRow(
          label: 'Sensor width (px)',
          currentValue: o.sensorWidthPx.toString(),
          getCanonical: () => ref.read(opticsSettingsProvider).sensorWidthPx.toString(),
          parse: (s) {
            final v = int.tryParse(s);
            if (v != null) n.setSensorWidthPx(v);
          },
        ),
        EditableNumberRow(
          label: 'Sensor height (px)',
          currentValue: o.sensorHeightPx.toString(),
          getCanonical: () => ref.read(opticsSettingsProvider).sensorHeightPx.toString(),
          parse: (s) {
            final v = int.tryParse(s);
            if (v != null) n.setSensorHeightPx(v);
          },
        ),
        EditableNumberRow(
          label: 'Pixel size (µm)',
          currentValue: _fmt(o.pixelSizeUm),
          getCanonical: () => _fmt(ref.read(opticsSettingsProvider).pixelSizeUm),
          parse: (s) {
            final v = double.tryParse(s);
            if (v != null) n.setPixelSizeUm(v);
          },
        ),
        const SizedBox(height: 8),
        Align(
          alignment: Alignment.centerLeft,
          child: TextButton.icon(
            onPressed: (_refreshing || _saving) ? null : () => _refreshFromCamera(),
            icon: _refreshing
                ? const SizedBox(width: 14, height: 14, child: CircularProgressIndicator(strokeWidth: 2))
                : const Icon(Icons.camera_alt_outlined, size: 16),
            label: Text(_refreshing ? 'Reading camera…' : 'Refresh from connected camera'),
          ),
        ),
        const SizedBox(height: 8),
        _FovReadout(optics: o),
        const SizedBox(height: 24),
        if (_lastError != null) ...[
          Text(_lastError!, style: TextStyle(color: Theme.of(context).colorScheme.error)),
          const SizedBox(height: 12),
        ],
        Row(
          mainAxisAlignment: MainAxisAlignment.end,
          children: [
            FilledButton.icon(
              onPressed: (_saving || _refreshing) ? null : _save,
              icon: _saving
                  ? const SizedBox(width: 14, height: 14, child: CircularProgressIndicator(strokeWidth: 2))
                  : const Icon(Icons.save, size: 16),
              label: Text(_saving ? 'Saving…' : 'Save'),
            ),
          ],
        ),
      ],
    );
  }

  // Trim a trailing '.0' so a whole number shows as "1000", not "1000.0".
  static String _fmt(double v) =>
      v == v.roundToDouble() ? v.toInt().toString() : v.toString();
}

/// Live field-of-view read-out from the current optics, so the user can confirm
/// their rig before saving. Shows a prompt instead of numbers when unconfigured.
class _FovReadout extends StatelessWidget {
  final OpticsSettings optics;
  const _FovReadout({required this.optics});

  @override
  Widget build(BuildContext context) {
    final w = optics.fovWidthArcmin;
    final h = optics.fovHeightArcmin;
    final scale = optics.pixelScaleArcsecPerPx;
    final style = Theme.of(context).textTheme.bodyMedium;
    final dim = style?.copyWith(color: AraColors.textSecondary);

    if (w == null || h == null || scale == null) {
      return Text(
        'Field of view: set a focal length, sensor size and pixel size to see it.',
        style: dim,
      );
    }
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text('Field of view: ${_arcmin(w)} × ${_arcmin(h)}', style: style),
        const SizedBox(height: 4),
        Text('Pixel scale: ${scale.toStringAsFixed(2)}″/px', style: dim),
      ],
    );
  }

  // Arcminutes, or degrees once it's wider than 1° (deg-and-arcmin reads better
  // for short-focal-length / wide-field rigs).
  static String _arcmin(double arcmin) => arcmin >= 60
      ? '${(arcmin / 60).toStringAsFixed(2)}°'
      : '${arcmin.toStringAsFixed(1)}′';
}
