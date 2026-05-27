import 'package:flutter/material.dart';

import '../../../widgets/settings/settings_row.dart';

/// §29.2 File saving + naming. 12h.2 read-only.
class SessionFilenamesPanel extends StatelessWidget {
  const SessionFilenamesPanel({super.key});

  @override
  Widget build(BuildContext context) {
    return ListView(
      padding: const EdgeInsets.all(24),
      children: const [
        SettingsSectionHeader('Format'),
        SettingsRow(
          label: 'File format',
          value: 'FITS',
          hint: 'FITS / XISF / FITS+RICE',
        ),
        SettingsRow(label: 'Compression', value: 'RICE'),
        SettingsRow(label: 'Compress bias/dark frames', value: 'On'),
        SettingsSectionHeader('Naming template'),
        SettingsRow(
          label: 'Template',
          value: r'$$DATEMINUS12$$/$$TARGETNAME$$/$$IMAGETYPE$$/'
              r'$$DATETIME$$_$$FILTER$$_$$EXPOSURETIME$$s_'
              r'g$$GAIN$$o$$OFFSET$$_$$FRAMENR$$',
        ),
        SettingsRow(
          label: 'Date separator',
          value: '/',
          hint: 'forward-slash → real directories; underscore → flat',
        ),
        SettingsSectionHeader('Available tokens'),
        SettingsRow(
          label: 'Date',
          value: r'$$DATE$$  $$TIME$$  $$DATETIME$$  $$DATEMINUS12$$',
        ),
        SettingsRow(
          label: 'Target',
          value: r'$$TARGETNAME$$  $$SEQUENCETITLE$$',
        ),
        SettingsRow(
          label: 'Frame',
          value: r'$$IMAGETYPE$$  $$FILTER$$  $$EXPOSURETIME$$  '
              r'$$GAIN$$  $$OFFSET$$  $$BINNING$$  $$FRAMENR$$',
        ),
        SettingsRow(
          label: 'Sensor',
          value: r'$$CAMERA$$  $$SENSORTEMP$$  $$FOCUSPOSITION$$',
        ),
      ],
    );
  }
}
