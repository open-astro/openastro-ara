// ignore: unused_import
import 'package:intl/intl.dart' as intl;

import 'app_localizations.dart';

// ignore_for_file: type=lint

/// The translations for English (`en`).
class AppLocalizationsEn extends AppLocalizations {
  AppLocalizationsEn([String locale = 'en']) : super(locale);

  @override
  String pillsMore(int count) {
    return '+$count more';
  }

  @override
  String get pillsShowFewer => 'Show fewer';

  @override
  String pillsMoreShort(int count) {
    return '+$count';
  }

  @override
  String pillsMoreSemantics(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count more messages',
      one: '1 more message',
    );
    return '$_temp0';
  }

  @override
  String get pillsDismiss => 'Dismiss';

  @override
  String get safetyAlertSemantics => 'Safety alert';

  @override
  String get safetyAlertTitle => 'SAFETY ALERT';

  @override
  String get safetyUnsafe => 'Conditions are UNSAFE.';

  @override
  String get safetyRinging => 'Alarm ringing';

  @override
  String get safetySilenceBefore => 'Silence it before it sounds';

  @override
  String get safetySilence => 'Silence alarm';

  @override
  String get deviceCamera => 'Camera';

  @override
  String get deviceMount => 'Mount';

  @override
  String get deviceFocuser => 'Focuser';

  @override
  String get deviceGuider => 'Guider';

  @override
  String get deviceConnecting => 'Connecting…';

  @override
  String get deviceNotConnected => 'Not connected';

  @override
  String get deviceCurrentTemperature => 'Temperature';

  @override
  String get deviceCooler => 'Cooler';

  @override
  String get deviceNoCooler => 'No cooler';

  @override
  String get deviceTarget => 'Target temperature';

  @override
  String get deviceCouldntSaveTarget => 'Couldn\'t save the target';

  @override
  String get deviceCouldntChangeCooler => 'Couldn\'t change the cooler';

  @override
  String get deviceSdk => 'SDK';

  @override
  String get deviceDriver => 'Driver';

  @override
  String get deviceFirmware => 'Firmware';

  @override
  String get deviceSerialNumber => 'Serial number';

  @override
  String get deviceLess => 'Less';

  @override
  String get deviceMore => 'More';

  @override
  String get deviceOn => 'On';

  @override
  String get deviceOff => 'Off';

  @override
  String get deviceVersions => 'Versions';

  @override
  String get deviceTracking => 'Tracking';

  @override
  String get devicePark => 'Park';

  @override
  String get deviceParking => 'Parking…';

  @override
  String get deviceUnparking => 'Unparking…';

  @override
  String get deviceParked => 'Parked';

  @override
  String get deviceUnparked => 'Unparked';

  @override
  String get devicePlanRunningParkAnyway =>
      'A plan is running. Park the mount anyway?';

  @override
  String get deviceCancel => 'Cancel';

  @override
  String get deviceParkAnyway => 'Park anyway';

  @override
  String get deviceMeridianFlip => 'Meridian flip';

  @override
  String get deviceAuto => 'Auto';

  @override
  String get deviceCouldntSaveMeridianFlip =>
      'Couldn\'t save the meridian flip';

  @override
  String get deviceCouldntMoveMount => 'Couldn\'t move the mount';

  @override
  String get deviceGain => 'Gain';

  @override
  String get deviceCouldntMoveFocuser => 'Couldn\'t move the focuser';

  @override
  String get deviceCouldntSaveThat => 'Couldn\'t save that';

  @override
  String get deviceNever => 'Never';

  @override
  String get deviceRefocusTemp => 'Refocus temp';

  @override
  String get devicePosition => 'Position';

  @override
  String get deviceTemperature => 'Temperature';

  @override
  String get deviceMoving => 'Moving…';

  @override
  String get deviceFocusPosition => 'Focus position';

  @override
  String get deviceStep => 'Step';

  @override
  String get deviceAutofocus => 'Autofocus';

  @override
  String get deviceCancelAutofocus => 'Cancel autofocus';

  @override
  String get deviceState => 'State';

  @override
  String get deviceRms => 'RMS';

  @override
  String get deviceDither => 'Dither';

  @override
  String get deviceDitherBy => 'Dither by';

  @override
  String devicePowerPercent(int percent) {
    return '$percent % power';
  }

  @override
  String deviceLetGoToMoveTo(String position) {
    return 'Let go to move to $position';
  }

  @override
  String deviceMovingTo(String position) {
    return 'Moving to $position…';
  }

  @override
  String deviceRmsRaDec(String ra, String dec) {
    return 'RA $ra · Dec $dec';
  }

  @override
  String deviceDitherEveryFrames(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count frames',
      one: 'Every frame',
    );
    return '$_temp0';
  }

  @override
  String previewFree(String size) {
    return '$size GB free';
  }

  @override
  String get previewUnplugged => 'Unplugged';

  @override
  String get previewStorage => 'Storage';

  @override
  String get previewCpu => 'CPU';

  @override
  String get previewMemory => 'Memory';

  @override
  String get previewUp => 'Up';

  @override
  String get previewHfr => 'HFR';

  @override
  String get previewStars => 'Stars';

  @override
  String get previewExposure => 'Exposure';

  @override
  String get previewGainOffset => 'Gain · offset';

  @override
  String get previewClipped => 'Clipped';

  @override
  String get previewSettings => 'Preview settings';

  @override
  String get previewLoop => 'Loop';

  @override
  String get previewSingle => 'Single';

  @override
  String get previewDone => 'Done';

  @override
  String get previewStop => 'Stop';

  @override
  String get previewTakeFrame => 'Take a frame';

  @override
  String previewStartsAt(String time) {
    return 'Starts at $time';
  }

  @override
  String get previewStopping => 'Stopping…';

  @override
  String get previewTakingFrame => 'Taking a frame';

  @override
  String get previewStarting => 'Starting…';

  @override
  String previewSecondsLeft(int seconds) {
    return '$seconds s left';
  }

  @override
  String get previewDownloading => 'Downloading…';

  @override
  String get previewFrameReady => 'Frame ready';

  @override
  String get previewCaptureFailed => 'Capture failed';

  @override
  String get previewLooping => 'Looping';

  @override
  String get previewPlanPaused => 'Plan paused';

  @override
  String get previewPlan => 'Plan';

  @override
  String get previewParking => 'Parking';

  @override
  String get previewUnparking => 'Unparking';

  @override
  String get previewCooling => 'Cooling';

  @override
  String previewStatusSpoken(String label, String detail) {
    return '$label, $detail';
  }

  @override
  String get focusAuto => 'Auto';

  @override
  String get focusManual => 'Manual';

  @override
  String get focusAutofocus => 'Autofocus';

  @override
  String get focusCancelAutofocus => 'Cancel autofocus';

  @override
  String focusHfr(String value) {
    return 'HFR $value';
  }

  @override
  String get focusNoFocuser => 'No focuser connected';

  @override
  String get polarLoop => 'Loop';

  @override
  String get polarSingle => 'Single';

  @override
  String get polarStart => 'Start polar alignment';

  @override
  String get polarStop => 'Stop';

  @override
  String get polarActionStart => 'start polar alignment';

  @override
  String get polarActionStop => 'stop polar alignment';

  @override
  String get polarActionSave => 'save the polar-align settings';

  @override
  String get polarActionCapture => 'take a frame';

  @override
  String get polarActionComplete => 'complete polar alignment';

  @override
  String get polarPhaseSeeding => 'Finding the pole…';

  @override
  String get polarPhaseAdjusting => 'Adjusting';

  @override
  String get polarPhasePaused => 'Paused';

  @override
  String get polarPhaseFailed => 'Polar alignment failed';

  @override
  String get polarPhaseReady => 'Ready';

  @override
  String get polarAltitude => 'Altitude';

  @override
  String get polarAzimuth => 'Azimuth';

  @override
  String polarBoltWorse(String axis) {
    return '$axis got worse. Turn that bolt back past where you started.';
  }

  @override
  String polarNoSolve(int count) {
    return 'No solve ($count in a row). Check the sky.';
  }

  @override
  String get polarExposure => 'Exposure';

  @override
  String get polarTakeFrame => 'Take frame';

  @override
  String get polarDone => 'Done';

  @override
  String get polarDoneNextPlan => 'Done · Next: Plan';

  @override
  String get searchHint => 'Objects, settings, modes, images';

  @override
  String get searchGroupSky => 'Sky';

  @override
  String get searchGroupSettings => 'Settings';

  @override
  String get searchGroupModes => 'Modes';

  @override
  String get searchGroupImages => 'Images';

  @override
  String get searchImageLibrary => 'Image Library';

  @override
  String searchNoMatch(String query) {
    return 'Nothing in Simple Mode matches \"$query\".';
  }

  @override
  String get searchSectionRig => 'Rig';

  @override
  String get searchSectionDevices => 'Devices';

  @override
  String get searchSectionCamera => 'Camera';

  @override
  String get searchSectionMount => 'Mount';

  @override
  String get searchSectionFocuser => 'Focuser';

  @override
  String get searchSectionGuider => 'Guider';

  @override
  String get searchSettingAperture => 'Aperture';

  @override
  String get searchSettingFocalLength => 'Focal length';

  @override
  String get searchSettingLatitude => 'Latitude';

  @override
  String get searchSettingLongitude => 'Longitude';

  @override
  String get searchSettingElevation => 'Elevation';

  @override
  String get searchSettingLocationTime => 'Location and time';

  @override
  String get searchSettingPermanentPier => 'Permanent pier';

  @override
  String get searchSettingSimpleMode => 'Simple Mode';

  @override
  String get searchSettingChangeProfile => 'Change profile';

  @override
  String get searchSettingChangeRig => 'Change rig';

  @override
  String get searchSettingVersions => 'Versions';

  @override
  String get searchSettingLogs => 'Logs & bug report';

  @override
  String get searchSettingConnect => 'Connect';

  @override
  String get searchSettingDisconnect => 'Disconnect';

  @override
  String get searchSettingReconnect => 'Reconnect';

  @override
  String get searchSettingCooler => 'Cooler';

  @override
  String get searchSettingCoolerTarget => 'Cooler target';

  @override
  String get searchSettingGain => 'Gain';

  @override
  String get searchSettingTracking => 'Tracking';

  @override
  String get searchSettingPark => 'Park';

  @override
  String get searchSettingMeridianFlip => 'Meridian flip';

  @override
  String get searchSettingFocuserStep => 'Focuser step';

  @override
  String get searchSettingDither => 'Dither';

  @override
  String get searchWordsRig => 'system profile';

  @override
  String get searchWordsAperture => 'telescope diameter';

  @override
  String get searchWordsFocalLength => 'telescope';

  @override
  String get searchWordsSite => 'site location coordinates';

  @override
  String get searchWordsElevation => 'site altitude height';

  @override
  String get searchWordsLocationTime => 'gps clock time zone site';

  @override
  String get searchWordsPermanentPier => 'polar alignment observatory';

  @override
  String get searchWordsSimpleMode => 'full ui advanced';

  @override
  String get searchWordsChangeProfile => 'profile';

  @override
  String get searchWordsChangeRig => 'server';

  @override
  String get searchWordsVersions => 'version about firmware driver';

  @override
  String get searchWordsLogs => 'support logs';

  @override
  String get searchWordsDevices => 'equipment gear';

  @override
  String get searchWordsCooler => 'camera temperature cooling';

  @override
  String get searchWordsGain => 'camera iso hcg';

  @override
  String get searchWordsTracking => 'mount sidereal';

  @override
  String get searchWordsPark => 'mount unpark';

  @override
  String get searchWordsMeridianFlip => 'mount pier side';

  @override
  String get searchWordsFocuser => 'focus position knob move';

  @override
  String get searchWordsFocuserStep => 'focus move';

  @override
  String get searchWordsGuider => 'guiding rms phd2';

  @override
  String get searchWordsDither => 'guiding dithering';

  @override
  String get autopilotStepFocus => 'Focus';

  @override
  String get autopilotStepPolarAlign => 'Polar align';

  @override
  String get autopilotStepPlan => 'Plan';

  @override
  String autopilotStepDone(String step) {
    return '$step, done';
  }

  @override
  String autopilotStepSkipped(String step) {
    return '$step, skipped';
  }

  @override
  String autopilotStepCurrent(String step) {
    return '$step, current step';
  }

  @override
  String autopilotStepNext(String step) {
    return '$step, next';
  }

  @override
  String get autopilotBack => 'Back';

  @override
  String get autopilotSkip => 'Skip';

  @override
  String get autopilotNothingDone => 'Nothing done yet tonight';

  @override
  String get autopilotStart => 'Start Autopilot';

  @override
  String get autopilotNotNow => 'Not now';

  @override
  String get shellModeAutopilot => 'Autopilot';

  @override
  String get shellModePreview => 'Preview';

  @override
  String get shellModePolarAlign => 'Polar align';

  @override
  String get shellModeFocus => 'Focus';

  @override
  String get shellModePlan => 'Plan';

  @override
  String get shellRailPolarAlign => 'P.A.';

  @override
  String get shellRailAutopilot => 'Auto';

  @override
  String shellStopAgain(String shortcut) {
    return 'Press $shortcut again to stop the run';
  }

  @override
  String get shellShowControls => 'Show controls';

  @override
  String get shellHideControls => 'Hide controls';

  @override
  String get shellTapShutter => 'Tap the shutter to take a frame';

  @override
  String get shellImageThis => 'Image this';

  @override
  String get shellSearch => 'Search';

  @override
  String get shellNightModeOn => 'Night mode on';

  @override
  String get shellNightMode => 'Night mode';

  @override
  String get shellSettings => 'Settings';

  @override
  String get shellSky => 'Sky';

  @override
  String get shellInfo => 'Info';

  @override
  String get shellLibrary => 'Library';

  @override
  String shellModeSemantics(String mode) {
    return 'Mode: $mode';
  }

  @override
  String get shellGuiding => 'Guiding';

  @override
  String shellGuidingRms(String rms) {
    return 'RMS $rms';
  }

  @override
  String get shellRa => 'RA';

  @override
  String get shellDec => 'Dec';

  @override
  String get shellHistogram => 'Histogram';

  @override
  String shellHistogramMedian(int median) {
    return 'Median $median';
  }

  @override
  String get shellSectionRig => 'Rig';

  @override
  String get shellSectionDevices => 'Devices';

  @override
  String get shellSectionCamera => 'Camera';

  @override
  String get shellSectionMount => 'Mount';

  @override
  String get shellSectionFocuser => 'Focuser';

  @override
  String get shellSectionGuider => 'Guider';

  @override
  String get shellRigNotConnected => 'Rig not connected';

  @override
  String get shellFilterWheel => 'Filter wheel';

  @override
  String get shellRotator => 'Rotator';

  @override
  String get shellFlatPanel => 'Flat panel';

  @override
  String get shellDome => 'Dome';

  @override
  String get shellWeather => 'Weather';

  @override
  String shellGearTitle(String type, String name) {
    return '$type · $name';
  }

  @override
  String get shellConnected => 'Connected';

  @override
  String get shellConnecting => 'Connecting…';

  @override
  String get shellFailed => 'Failed';

  @override
  String get shellNotConnected => 'Not connected';

  @override
  String get shellDisconnect => 'Disconnect';

  @override
  String get shellReconnect => 'Reconnect';

  @override
  String get shellConnect => 'Connect';

  @override
  String get shellCouldntSave => 'Couldn\'t save that';

  @override
  String get shellCouldntSetClock => 'Couldn\'t set the rig\'s clock';

  @override
  String get shellCouldntGetLocation => 'Couldn\'t get a location';

  @override
  String get shellCouldntSaveSite => 'Couldn\'t save the site';

  @override
  String shellSiteFrom(String source) {
    return 'Site from $source';
  }

  @override
  String get shellChange => 'Change';

  @override
  String get shellName => 'Name';

  @override
  String get shellChangeRig => 'Change rig';

  @override
  String get shellProfile => 'Profile';

  @override
  String get shellChangeProfile => 'Change profile';

  @override
  String get shellPlanRunning =>
      'A plan is running. It keeps running on the rig; pick this profile again to come back to it.';

  @override
  String get shellAperture => 'Aperture';

  @override
  String get shellFocalLength => 'Focal length';

  @override
  String get shellLatitude => 'Latitude';

  @override
  String get shellLongitude => 'Longitude';

  @override
  String get shellElevation => 'Elevation';

  @override
  String shellUseLocation(String device) {
    return 'Use $device\'s location and time';
  }

  @override
  String get shellPermanentPier => 'Permanent pier';

  @override
  String get shellPermanentPierHint => 'Skips polar alignment';

  @override
  String get shellSimpleMode => 'Simple Mode';

  @override
  String get shellThisApp => 'This app';

  @override
  String get shellAraServer => 'OpenAstro Ara server';

  @override
  String get shellStarDatabase => 'Star database';

  @override
  String get shellSystem => 'System';

  @override
  String get shellLogs => 'Logs & bug report';

  @override
  String get shellNotSet => 'Not set';

  @override
  String get shellHemiNorth => 'N';

  @override
  String get shellHemiSouth => 'S';

  @override
  String get shellHemiEast => 'E';

  @override
  String get shellHemiWest => 'W';

  @override
  String shellWheelSemantics(String label, int index, int count) {
    return '$label, wheel $index of $count';
  }

  @override
  String get shellRaise => 'Raise';

  @override
  String get shellLower => 'Lower';

  @override
  String get planNeverRises => 'Never rises here';

  @override
  String get planBelowHorizon => 'Below your horizon';

  @override
  String planBelowHorizonRises(String time) {
    return 'Below your horizon · rises $time';
  }

  @override
  String planAltitudeUpMax(int alt, int max) {
    return '$alt° up · max $max°';
  }

  @override
  String planAltitudeUp(int alt) {
    return '$alt° up';
  }

  @override
  String get planBelowTheHorizon => 'below the horizon';

  @override
  String planRisesAt(String time) {
    return 'rises $time';
  }

  @override
  String planRisesAtCap(String time) {
    return 'Rises $time';
  }

  @override
  String get planPausing => 'Pausing…';

  @override
  String get planPaused => 'Paused';

  @override
  String get planDone => 'Done';

  @override
  String planMoreInQueue(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count more in the queue',
    );
    return '$_temp0';
  }

  @override
  String get planQueued => 'queued';

  @override
  String planSquareLabel(String name) {
    return 'Plan: $name';
  }

  @override
  String get planPickTargetFirst => 'Pick a target first';

  @override
  String get planStartImaging => 'Start imaging';

  @override
  String get planPauseAfterFrame => 'Pause after this frame';

  @override
  String get planPausingLabel => 'Pausing';

  @override
  String get planResume => 'Resume';

  @override
  String get planStopTheRun => 'Stop the run';

  @override
  String get planHoldToStop => 'Hold to stop';

  @override
  String get planTonightsBest => 'TONIGHT\'S BEST';

  @override
  String get planTonightsPlan => 'Tonight\'s plan';

  @override
  String planEditTarget(String name) {
    return 'Edit $name';
  }

  @override
  String get planSearchObjects => 'Search objects';

  @override
  String get planSearch => 'Search';

  @override
  String get planSearchOrPick => 'Search, or pick in the sky.';

  @override
  String get planPickInSky => 'Pick in Sky';

  @override
  String get planExposure => 'Exposure';

  @override
  String planExposureSuggested(String exposure) {
    return '$exposure · suggested';
  }

  @override
  String get planFrames => 'Frames';

  @override
  String planSuggested(String hint) {
    return 'Suggested: $hint';
  }

  @override
  String get planUseAll => 'Use all';

  @override
  String planFrameCount(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count frames',
    );
    return '$_temp0';
  }

  @override
  String get planNotPolarAligned => 'Not polar aligned tonight';

  @override
  String get planStartsNow => 'Starts now';

  @override
  String planStartsAtDark(String time) {
    return 'Starts at dark ($time)';
  }

  @override
  String planStartsAt(String time) {
    return 'Starts at $time';
  }

  @override
  String get planStartNowInstead => 'Start now instead';

  @override
  String planWaitForDark(String time) {
    return 'Wait for dark ($time)';
  }

  @override
  String get planPickATime => 'Pick a time';

  @override
  String get planCancel => 'Cancel';

  @override
  String get planSaving => 'Saving…';

  @override
  String get planSave => 'Save';

  @override
  String planTargetAdded(String name, int count) {
    return '$name added · $count in the queue';
  }

  @override
  String get planAddToQueue => 'Add to queue';

  @override
  String get planStarting => 'Starting…';

  @override
  String planStartAt(String time) {
    return 'Start at $time';
  }

  @override
  String get planStartNow => 'Start now';

  @override
  String get planCreateAndStart => 'Create and start';

  @override
  String planTargetRemoved(String name) {
    return '$name removed';
  }

  @override
  String get planUndo => 'Undo';

  @override
  String planMoveTarget(String name) {
    return 'Move $name';
  }

  @override
  String planRemoveFromQueue(String name) {
    return 'Remove $name from the queue';
  }

  @override
  String get planRemove => 'Remove';

  @override
  String planQueueHeader(int count, String duration) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: 'targets',
      one: 'target',
    );
    return 'QUEUE · $count $_temp0 · $duration';
  }

  @override
  String get planOpenStorage => 'Open Storage';

  @override
  String get planNightDone => 'Night done';

  @override
  String get planStopped => 'stopped';

  @override
  String get planFailed => 'failed';

  @override
  String planStoppedAt(String step) {
    return 'Stopped at: $step';
  }

  @override
  String get planImageLibrary => 'Image Library';

  @override
  String get planNewPlan => 'New plan';

  @override
  String planImagingTarget(String name) {
    return 'IMAGING $name';
  }

  @override
  String get planMountStopped => 'Mount stopped';

  @override
  String planEndsLeft(String time, String duration) {
    return 'Ends $time · $duration left';
  }

  @override
  String get planHintResume =>
      'Tap the shutter to resume and re-center; hold it to stop.';

  @override
  String get planResumeWithoutRecenter => 'Resume without re-centering';

  @override
  String get planRecenterAndRefocus => 'Re-center and refocus';

  @override
  String get planHintPause =>
      'Tap the shutter to pause after this frame; hold it to stop.';

  @override
  String get planAddTarget => 'Add target';

  @override
  String planMagnitude(String value) {
    return 'mag $value';
  }

  @override
  String get shellLanguage => 'Language';

  @override
  String get shellLanguageSystem => 'System';
}
