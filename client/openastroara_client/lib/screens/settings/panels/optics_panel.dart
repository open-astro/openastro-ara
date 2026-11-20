import 'package:flutter/material.dart';
import '../../../util/friendly_error.dart';
import '../../../l10n/l10n.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../services/camera_geometry_api.dart';
import '../../../state/saved_server_state.dart';
import '../../../state/settings/loaded_section.dart';
import '../../../state/settings/optics_settings_state.dart';
import '../../../state/settings/panel_save_registry.dart';
import '../../../theme/ara_colors.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/section_panel.dart';
import '../../../widgets/adaptive_controls.dart';

/// §36/§25.5 Optics panel — the imaging-train geometry (focal length,
/// reducer/barlow, sensor size + pixel pitch) that drives the Planning tab's
/// Frame-mode FOV overlay. The fields show the rig's copy of the `optics`
/// section (#467, ADR 0015: loaded before it is saved; not loaded, they are
/// not shown); a live read-out shows the resulting field of view so the user
/// can sanity-check their rig.
class OpticsPanel extends ConsumerStatefulWidget {
  const OpticsPanel({super.key});

  @override
  ConsumerState<OpticsPanel> createState() => _OpticsPanelState();
}

class _OpticsPanelState extends ConsumerState<OpticsPanel>
    with PanelSaveRegistration, SectionPanel {
  // Kept (not header-owned): also disables the "Refresh from connected
  // camera" button while a save is in flight.
  bool _saving = false;
  bool _refreshing = false;
  String? _lastError;

  @override
  List<PanelSection> get sections => [opticsSettingsProvider];

  @override
  Future<void> panelSave() async {
    setState(() {
      _saving = true;
      _lastError = null;
    });
    // A field still being edited commits on focus loss; drop focus first so
    // the typed value is what gets sent.
    FocusManager.instance.primaryFocus?.unfocus();
    final error = await saveSections();
    if (mounted) {
      setState(() {
        _saving = false;
        _lastError = error;
      });
    }
  }

  /// Pull the connected camera's sensor geometry into the rig's copy and save
  /// it (ADR 0015: refused, nothing sent, when the section is not loaded from
  /// the rig). The daemon also caches this on connect; this is the on-demand
  /// re-pull.
  Future<void> _refreshFromCamera() async {
    final server = ref.read(activeServerProvider);
    final messenger = ScaffoldMessenger.of(context);
    final l10n = context.l10n;
    if (server == null) {
      messenger.showSnackBar(
        SnackBar(content: Text(l10n.deviceChooserNotConnected)),
      );
      return;
    }
    setState(() {
      _refreshing = true;
      _lastError = null;
    });
    try {
      final geometry = await CameraGeometryApi(server).read();
      // ref.read on a disposed WidgetRef throws; bail if the panel went away mid-request.
      if (!mounted) return;
      if (geometry == null) {
        messenger.showSnackBar(
          const SnackBar(
            content: Text(
              'Connect a camera that reports its sensor size first.',
            ),
          ),
        );
        return;
      }
      await ref
          .read(opticsSettingsProvider.notifier)
          .editAndSave(
            (c) => c.copyWith(
              sensorWidthPx: geometry.sensorWidthPx,
              sensorHeightPx: geometry.sensorHeightPx,
              pixelSizeUm: geometry.pixelSizeUm,
            ),
          );
      if (mounted) {
        messenger.showSnackBar(
          const SnackBar(
            content: Text('Filled sensor size from the connected camera.'),
          ),
        );
      }
    } catch (e) {
      // Set the field without its own setState; the finally's setState rebuilds with it.
      _lastError =
          e is SectionNotLoadedException || e is SectionConflictException
          ? e.toString()
          : friendlyError(l10n, e, action: l10n.actionReadCamera);
      if (mounted) messenger.showSnackBar(SnackBar(content: Text(_lastError!)));
    } finally {
      if (mounted) setState(() => _refreshing = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final gate = sectionGate();
    if (gate != null) return gate;
    final o = ref.watch(opticsSettingsProvider).value!;
    final n = ref.read(opticsSettingsProvider.notifier);
    OpticsSettings now() => ref.read(opticsSettingsProvider).value!;
    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        ?sectionConflict(),
        EditableNumberRow(
          label: 'Focal length (mm)',
          helpKey: 'imaging.optics.focal_length',
          currentValue: _fmt(o.focalLengthMm),
          getCanonical: () => _fmt(now().focalLengthMm),
          parse: (s) {
            final v = double.tryParse(s);
            if (v != null) n.setFocalLengthMm(v);
          },
        ),
        EditableNumberRow(
          label: 'Reducer / barlow factor (×)',
          helpKey: 'imaging.optics.reducer',
          currentValue: _fmt(o.reducerFactor),
          getCanonical: () => _fmt(now().reducerFactor),
          parse: (s) {
            final v = double.tryParse(s);
            if (v != null) n.setReducerFactor(v);
          },
        ),
        EditableNumberRow(
          label: 'Aperture (mm)',
          helpKey: 'imaging.optics.aperture',
          currentValue: _fmt(o.apertureMm),
          getCanonical: () => _fmt(now().apertureMm),
          parse: (s) {
            final v = double.tryParse(s);
            if (v != null) n.setApertureMm(v);
          },
        ),
        EditableNumberRow(
          label: 'Sensor width (px)',
          helpKey: 'imaging.optics.sensor_width',
          currentValue: o.sensorWidthPx.toString(),
          getCanonical: () => now().sensorWidthPx.toString(),
          parse: (s) {
            final v = int.tryParse(s);
            if (v != null) n.setSensorWidthPx(v);
          },
        ),
        EditableNumberRow(
          label: 'Sensor height (px)',
          helpKey: 'imaging.optics.sensor_height',
          currentValue: o.sensorHeightPx.toString(),
          getCanonical: () => now().sensorHeightPx.toString(),
          parse: (s) {
            final v = int.tryParse(s);
            if (v != null) n.setSensorHeightPx(v);
          },
        ),
        EditableNumberRow(
          label: 'Pixel size (µm)',
          helpKey: 'imaging.optics.pixel_size',
          currentValue: _fmt(o.pixelSizeUm),
          getCanonical: () => _fmt(now().pixelSizeUm),
          parse: (s) {
            final v = double.tryParse(s);
            if (v != null) n.setPixelSizeUm(v);
          },
        ),
        const SizedBox(height: 8),
        Align(
          alignment: Alignment.centerLeft,
          child: TextButton.icon(
            onPressed: (_refreshing || _saving)
                ? null
                : () => _refreshFromCamera(),
            icon: _refreshing
                ? const SizedBox(
                    width: 14,
                    height: 14,
                    child: AdaptiveProgressIndicator(strokeWidth: 2),
                  )
                : const Icon(Icons.camera_alt_outlined, size: 16),
            label: Text(
              _refreshing ? 'Reading camera…' : 'Refresh from connected camera',
            ),
          ),
        ),
        const SizedBox(height: 8),
        _FovReadout(optics: o),
        const SizedBox(height: 24),
        if (_lastError != null) ...[
          Text(
            _lastError!,
            style: TextStyle(color: Theme.of(context).colorScheme.error),
          ),
          const SizedBox(height: 12),
        ],
        // Save lives in the settings-shell header (PanelSaveRegistration) —
        // fixed chrome, always visible, no scrolling to find it.
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
