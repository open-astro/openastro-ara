/// §69 help registry. Single source of truth for all in-app contextual help.
/// Parallel to §61 settings registry.

class Help {
  final String key;
  final String title;
  final String body;
  final String? learnMoreUrl;
  final List<String> relatedHelpKeys;
  final List<String> relatedSettings;

  const Help({
    required this.key,
    required this.title,
    required this.body,
    this.learnMoreUrl,
    this.relatedHelpKeys = const [],
    this.relatedSettings = const [],
  });
}

const Map<String, Help> helpRegistry = {
  'guider.dither_pixels': const Help(
    key: 'guider.dither_pixels',
    title: 'Dithering',
    body: 'Dithering is the process of slightly shifting the telescope position '
        'between exposures. This causes fixed-pattern noise (like hot pixels) '
        'to fall on different physical pixels in each frame, allowing them to '
        'be removed during stacking.',
    learnMoreUrl: 'https://openastro.net/wiki/guiding/dithering',
    relatedSettings: const ['guider.dither_pixels'],
  ),
  'safety.policies.on_unsafe': const Help(
    key: 'safety.policies.on_unsafe',
    title: 'Unsafe Weather Actions',
    body: 'Determines what the system does when the safety monitor reports '
        'unsafe conditions (rain, high wind, clouds). "Pause + Park" is the '
        'safest default for unattended imaging.',
    relatedSettings: const ['safety.policies.on_unsafe'],
  ),
  'safety.policies.auto_resume': const Help(
    key: 'safety.policies.auto_resume',
    title: 'Auto-resume',
    body: 'If enabled, the sequence will automatically resume as soon as the '
        'safety monitor reports "Safe" again. If disabled, the sequence '
        'stays paused until you manually resume it.',
    relatedSettings: const ['safety.policies.auto_resume'],
  ),
  'safety.policies.resume_delay': const Help(
    key: 'safety.policies.resume_delay',
    title: 'Resume Delay',
    body: 'The number of minutes to wait after a "Safe" signal before '
        'actually resuming. Useful to ensure that a passing cloud bank '
        'has fully cleared before starting the next exposure.',
    relatedSettings: const ['safety.policies.resume_delay'],
  ),
  'diagnostics.mode': const Help(
    key: 'diagnostics.mode',
    title: 'Diagnostics Mode',
    body: 'Controls how Ara responds to acquisition issues like star loss or focus drift.\n\n'
        '* **Notify only**: Logs the issue and notifies you, but takes no action.\n'
        '* **Balanced**: Performs low-risk recoveries (like auto-refocus on drift).\n'
        '* **Aggressive**: Takes corrective action for almost all issues (e.g. abort/restart on star loss).',
    relatedSettings: const ['diagnostics.mode'],
  ),
  'camera.cooler_warmup_on_session_end': const Help(
    key: 'camera.cooler_warmup_on_session_end',
    title: 'Cooler Warmup',
    body: 'Determines how the camera cooler is handled after the session.\n\n'
        '* **Off**: Disconnect immediately. Best for CMOS.\n'
        '* **Ramp**: Slowly return to ambient temperature. Best for CCDs to avoid thermal shock.\n'
        '* **Immediate**: Stop cooling directly before disconnect.',
    relatedSettings: const ['camera.cooler_warmup_on_session_end'],
  ),
};
