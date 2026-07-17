import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../services/profile_api.dart';
import '../../../state/saved_server_state.dart';
import '../../../state/settings/filenames_settings_state.dart';
import '../../../state/settings/panel_save_registry.dart';
import '../../../state/settings/storage_settings_state.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// §29.2 File saving + naming. Phase 12h.6f added the daemon round-trip
/// for the section's editable fields (date separator + dark/bias
/// compression). The read-only refs to storage-panel-owned template /
/// format / compression are unchanged — edit them in Storage to keep one
/// source of truth.
class SessionFilenamesPanel extends ConsumerStatefulWidget {
  const SessionFilenamesPanel({super.key});

  @override
  ConsumerState<SessionFilenamesPanel> createState() =>
      _SessionFilenamesPanelState();
}

class _SessionFilenamesPanelState extends ConsumerState<SessionFilenamesPanel>
    with PanelSaveRegistration {
  String? _lastError;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) => _hydrate());
  }

  Future<void> _hydrate() async {
    final api = _api();
    if (api == null) return;
    try {
      await ref
          .read(filenamesSettingsProvider.notifier)
          .hydrateFromServer(api);
    } catch (e) {
      if (mounted) setState(() => _lastError = 'Could not load saved values: $e');
    }
  }

  @override
  Future<void> panelSave() => _save();

  Future<void> _save() async {
    setState(() => _lastError = null);
    final api = _api();
    final messenger = ScaffoldMessenger.of(context);
    if (api == null) {
      setState(
          () => _lastError = 'No active server — connect to a daemon first.');
      messenger.showSnackBar(SnackBar(content: Text(_lastError!)));
      return;
    }
    try {
      await ref.read(filenamesSettingsProvider.notifier).persistToServer(api);
      if (!mounted) return;
      messenger.showSnackBar(
        const SnackBar(content: Text('File naming options saved to daemon.')),
      );
    } catch (e) {
      if (!mounted) return;
      setState(() => _lastError = 'Save failed: $e');
      messenger.showSnackBar(SnackBar(content: Text(_lastError!)));
    }
  }

  ProfileApi? _api() {
    final server = ref.read(activeServerProvider);
    return server == null ? null : ProfileApi(server);
  }

  @override
  Widget build(BuildContext context) {
    final fs = ref.watch(filenamesSettingsProvider);
    final fn = ref.read(filenamesSettingsProvider.notifier);
    final ss = ref.watch(storageSettingsProvider);

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('Format'),
        SettingsRow(
          label: 'File format',
          value: _formatLabel(ss.fileFormat),
          hint: 'Edit in Settings → Session → Storage',
        ),
        SettingsRow(
          label: 'Compression',
          value: _compressionLabel(ss.compression),
          hint: 'Edit in Settings → Session → Storage',
        ),
        SettingsSwitchRow(
          label: 'Compress bias/dark frames',
          helpKey: 'session.filenames.compress_darks_and_bias',
          value: fs.compressDarksAndBias,
          onChanged: fn.setCompressDarksAndBias,
          hint: 'RICE losslessly compresses dark frames very well',
        ),
        const SettingsSectionHeader('Naming template'),
        SettingsRow(
          label: 'Template',
          value: ss.filenameTemplate,
          hint: 'Edit in Settings → Session → Storage',
        ),
        SettingsDropdownRow<DateSeparator>(
          label: 'Date separator',
          helpKey: 'session.filenames.date_separator',
          value: fs.dateSeparator,
          items: const {
            DateSeparator.forwardSlash: '/  (forward slash — real directories)',
            DateSeparator.underscore: '_  (underscore — flat filenames)',
            DateSeparator.dash: '-  (dash — flat filenames, Windows-safe)',
          },
          onChanged: (v) {
            if (v != null) fn.setDateSeparator(v);
          },
        ),
        const SettingsSectionHeader('Available tokens'),
        const SettingsRow(
          label: 'Date',
          value: r'$$DATE$$  $$TIME$$  $$DATETIME$$  $$DATEMINUS12$$',
        ),
        const SettingsRow(
          label: 'Target',
          value: r'$$TARGETNAME$$  $$SEQUENCETITLE$$',
        ),
        const SettingsRow(
          label: 'Frame',
          value: r'$$IMAGETYPE$$  $$FILTER$$  $$EXPOSURETIME$$  '
              r'$$GAIN$$  $$OFFSET$$  $$BINNING$$  $$FRAMENR$$',
        ),
        const SettingsRow(
          label: 'Sensor',
          value: r'$$CAMERA$$  $$SENSORTEMP$$  $$FOCUSPOSITION$$',
        ),
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

  String _formatLabel(StorageFileFormat f) => switch (f) {
        StorageFileFormat.fits => 'FITS',
        StorageFileFormat.xisf => 'XISF',
        StorageFileFormat.fitsRice => 'FITS + RICE',
        StorageFileFormat.fitsGzip => 'FITS + gzip',
      };

  String _compressionLabel(StorageCompression c) => switch (c) {
        StorageCompression.off => 'Off',
        StorageCompression.rice => 'RICE',
        StorageCompression.gzip => 'gzip',
      };
}
