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
  String statusMoreSemantics(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count more statuses',
      one: '1 more status',
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
  String get deviceCamera => 'Main Camera';

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
  String get deviceCurrentTemperature => 'Sensor temperature';

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
  String get deviceParkUnfinished => 'The mount didn\'t finish parking';

  @override
  String get deviceUnparkUnfinished => 'The mount didn\'t finish unparking';

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
  String get deviceBusy => 'Still busy';

  @override
  String get deviceBusyDetail =>
      'The last command hasn\'t finished. Try again in a moment.';

  @override
  String get deviceCouldntMoveFocuser => 'Couldn\'t move the focuser';

  @override
  String get deviceCouldntSaveThat => 'Couldn\'t save that';

  @override
  String get deviceNever => 'Never';

  @override
  String get deviceRefocusTemp => 'Refocus every';

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
  String searchMatches(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count matches',
      one: '1 match',
    );
    return '$_temp0';
  }

  @override
  String get searchSectionRig => 'Rig';

  @override
  String get searchSectionDevices => 'Devices';

  @override
  String get searchSectionCamera => 'Main Camera';

  @override
  String get searchSectionMount => 'Mount';

  @override
  String get searchSectionFocuser => 'Focuser';

  @override
  String get searchSectionGuider => 'Guider Camera';

  @override
  String get searchSettingAperture => 'Main scope aperture';

  @override
  String get searchSettingFocalLength => 'Main scope focal length';

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
  String get nightSetupStepFocus => 'Focus';

  @override
  String get nightSetupStepPolarAlign => 'Polar align';

  @override
  String get nightSetupStepPlan => 'Plan';

  @override
  String get nightSetupStepGuide => 'Guide';

  @override
  String nightSetupStepDone(String step) {
    return '$step, done';
  }

  @override
  String nightSetupStepSkipped(String step) {
    return '$step, skipped';
  }

  @override
  String nightSetupStepCurrent(String step) {
    return '$step, current step';
  }

  @override
  String nightSetupStepNext(String step) {
    return '$step, next';
  }

  @override
  String get nightSetupBack => 'Back';

  @override
  String get nightSetupSkip => 'Skip';

  @override
  String get shellModeNightSetup => 'Night Setup';

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
  String get shellRailNightSetup => 'Setup';

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
  String get shellSectionCamera => 'Main Camera';

  @override
  String get shellSectionMount => 'Mount';

  @override
  String get shellSectionFocuser => 'Focuser';

  @override
  String get shellSectionGuider => 'Guider Camera';

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
  String shellGearActionFailed(String action, String device) {
    return '$action failed · $device';
  }

  @override
  String get shellOk => 'OK';

  @override
  String get shellBedtimeTitle => 'Before you sleep';

  @override
  String get shellBedtimeAllGood => 'Everything is ready for the night.';

  @override
  String get shellBedtimeAnswer => 'Got it — let me go to bed';

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
  String get shellAperture => 'Main scope aperture';

  @override
  String get shellFocalLength => 'Main scope focal length';

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
  String get shellAraServer => 'Ara server';

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
  String planAlreadyQueued(String name) {
    return '$name is already in the queue.';
  }

  @override
  String get planRigUnreachable =>
      'Couldn\'t reach the rig. Check the connection and try again.';

  @override
  String get planActionCreate => 'create the plan';

  @override
  String get planActionSaveEdit => 'save the edit';

  @override
  String get planActionPutBack => 'put it back';

  @override
  String get planActionReorder => 'reorder the queue';

  @override
  String get planActionRemove => 'remove it from the queue';

  @override
  String get planActionSchedule => 'schedule the start';

  @override
  String get planActionStart => 'start imaging';

  @override
  String get planActionPause => 'pause';

  @override
  String get planActionResume => 'resume';

  @override
  String get planActionStop => 'stop the run';

  @override
  String get planRunEnding => 'The run is ending; this target can\'t join it.';

  @override
  String get planQueueBusy =>
      'The queue is still updating on the rig. Try again in a moment.';

  @override
  String get shellLanguage => 'Language';

  @override
  String get shellLanguageSystem => 'System';

  @override
  String get estopCompactStop => 'STOP';

  @override
  String get estopCompactHold => 'HOLD';

  @override
  String get estopCompactPressAgain => 'PRESS\nAGAIN';

  @override
  String get estopStopping => 'Stopping…';

  @override
  String get estopHoldToConfirm => 'Hold to confirm';

  @override
  String get estopPressAgain => 'Press again';

  @override
  String get estopLabel => 'Emergency Stop';

  @override
  String get estopPressAgainAnnouncement =>
      'Press again within 3 seconds to stop everything';

  @override
  String get estopConfirmTitle => 'Stop everything now?';

  @override
  String get estopConfirmBody =>
      'This aborts the running sequence and the in-flight exposure, stops guiding, parks the mount, and switches the flat panel light and the camera cooler off. It cannot be undone — the sequence does not resume.';

  @override
  String get estopCancel => 'Cancel';

  @override
  String get estopConfirm => 'EMERGENCY STOP';

  @override
  String get estopNoServer => 'No server connected — nothing to stop.';

  @override
  String estopFailed(String error) {
    return 'EMERGENCY STOP FAILED — $error Check your rig manually.';
  }

  @override
  String get estopActionReach => 'reach the rig';

  @override
  String get estopAlreadyRunning =>
      'An emergency stop is already running — no second volley sent.';

  @override
  String estopRunsAborted(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count sequence run(s) aborted',
    );
    return '$_temp0';
  }

  @override
  String get estopNoSequence => 'no sequence was running';

  @override
  String get estopExposureAborted => 'exposure aborted';

  @override
  String get estopGuidingStopped => 'guiding stopped';

  @override
  String get estopParkRequested => 'mount told to park';

  @override
  String get estopFlatPanelOff => 'flat panel light off';

  @override
  String get estopCoolerOff => 'camera cooler off';

  @override
  String estopSummary(String parts) {
    return 'Emergency stop executed: $parts.';
  }

  @override
  String estopCheckRig(String failures) {
    return 'CHECK THE RIG: $failures.';
  }

  @override
  String get estopFailAbortRuns =>
      'SEQUENCE ABORT FAILED (the run may still be executing)';

  @override
  String get estopFailAbortExposure => 'EXPOSURE ABORT FAILED';

  @override
  String get estopFailStopGuiding =>
      'GUIDING MAY STILL BE ACTIVE (stop failed)';

  @override
  String get estopFailPark => 'MOUNT PARK FAILED — verify it manually';

  @override
  String get estopFailFlatPanel => 'FLAT PANEL LIGHT-OFF FAILED';

  @override
  String get estopFailCooler =>
      'CAMERA COOLER STILL ON (switching it off failed)';

  @override
  String estopFailOther(String step) {
    return '$step FAILED';
  }

  @override
  String get focusCardInFocus => 'In focus';

  @override
  String focusCardInFocusHfr(String hfr) {
    return 'In focus · HFR $hfr';
  }

  @override
  String get focusCardFailed => 'Autofocus failed';

  @override
  String get focusCardCancelled => 'Canceled';

  @override
  String get focusCardNotFocused => 'Not focused yet';

  @override
  String get focusCardFailedSeeLogs => 'Autofocus failed — see Support → Logs';

  @override
  String focusCardBackAt(String position) {
    return 'The focuser is back at $position.';
  }

  @override
  String focusCardSmart(String shot, String total) {
    return 'Smart Focus — shot $shot of $total';
  }

  @override
  String get focusCardCoarse => 'Finding rough focus…';

  @override
  String focusCardSweeping(String done, String total) {
    return 'Sweeping — probe $done of $total';
  }

  @override
  String focusCardPass(String pass) {
    return '(pass $pass)';
  }

  @override
  String get focusCardFitting => 'Fitting the curve…';

  @override
  String get focusCardMoving => 'Moving to best focus…';

  @override
  String get focusCardConfirming => 'Confirming focus…';

  @override
  String get focusCardBacklash => 'Measuring focuser backlash…';

  @override
  String get focusCardRunning => 'Autofocus running…';

  @override
  String focusCardInFocusAt(String position) {
    return 'In focus at $position';
  }

  @override
  String focusCardTook(String duration) {
    return 'in $duration';
  }

  @override
  String focusCardFailedReason(String reason) {
    return 'Failed: $reason';
  }

  @override
  String get focusCardSeeLogs => 'see Support → Logs';

  @override
  String focusCardRestoredPosition(String position) {
    return 'focuser restored to $position';
  }

  @override
  String get focusCardNoRunYet => 'No autofocus yet this session.';

  @override
  String get filterDropdownLabel => 'Filter';

  @override
  String get filterDropdownDidntMove =>
      'Couldn\'t change the filter - the wheel didn\'t start moving.';

  @override
  String filterDropdownNotASlot(String name) {
    return 'Filter \'$name\' isn\'t a slot on the connected wheel.';
  }

  @override
  String get filterDropdownBusy => 'Another action is still in progress.';

  @override
  String get filterDropdownActionChange => 'change the filter';

  @override
  String get polarHintOnTarget => 'On target';

  @override
  String get polarHintLower => 'Lower';

  @override
  String get polarHintRaise => 'Raise';

  @override
  String get polarHintMoveWest => 'Move west';

  @override
  String get polarHintMoveEast => 'Move east';

  @override
  String get polarHintAltitudeWorse =>
      'Altitude got worse after that turn — the altitude bolt moves the axis the other way on this mount. Turn it back past where you started.';

  @override
  String get polarHintAzimuthWorse =>
      'Azimuth got worse after that turn — the azimuth bolt moves the axis the other way on this mount. Turn it back past where you started.';

  @override
  String get guiderStateNotGuiding => 'Not guiding';

  @override
  String get guiderStateLooping => 'Looping';

  @override
  String get guiderStateStarSelected => 'Star selected';

  @override
  String get guiderStateCalibrating => 'Calibrating';

  @override
  String get guiderStateGuiding => 'Guiding';

  @override
  String get guiderStateSettling => 'Settling';

  @override
  String get guiderStatePaused => 'Paused';

  @override
  String get guiderStateStarLost => 'Star lost';

  @override
  String get guiderStateDithering => 'Dithering';

  @override
  String get guiderStateAssistant => 'Guiding Assistant';

  @override
  String get guiderStateUnknown => 'Unknown';

  @override
  String get guiderStateNotConnected => 'Not connected';

  @override
  String guiderStateCalibrationStep(String direction, String step) {
    return '$direction step $step';
  }

  @override
  String errorCouldnt(String action) {
    return 'Couldn\'t $action';
  }

  @override
  String get errorActionDefault => 'do that';

  @override
  String errorNoAnswer(String device) {
    return 'your rig didn\'t answer. Check it\'s powered on and on the same network as $device.';
  }

  @override
  String get errorTooSlow =>
      'your rig took too long to answer. It may be busy; try again in a moment.';

  @override
  String get errorUntrusted => 'the connection wasn\'t trusted.';

  @override
  String get errorCancelled => 'the request was canceled.';

  @override
  String get errorNotFound =>
      'your rig doesn\'t have that. It may be running an older version of Ara.';

  @override
  String get errorBusy => 'something else is using it right now.';

  @override
  String get errorRefusedValues => 'your rig refused those values.';

  @override
  String get errorServer =>
      'your rig hit an error. Support → Logs has the details.';

  @override
  String get errorRefused => 'your rig refused the request.';

  @override
  String get errorConnectionFailed =>
      'the connection failed. Check your rig is reachable.';

  @override
  String get errorFile => 'a file problem got in the way';

  @override
  String get errorUnreadable =>
      'your rig sent something Ara didn\'t understand. It may be running a different version.';

  @override
  String focusCardWeakFit(String r2) {
    return 'The curve fit was weak (R² $r2) — passing cloud, a slew during the sweep, or probes that straddle the V unevenly. The focus here comes from the measured frame, not the fit; run again if the sky has settled.';
  }

  @override
  String focusCardWeakFitPlateau(String r2) {
    return 'The curve fit was weak (R² $r2): the outer probes sit on a plateau, so the sweep is wider than the V. A smaller step size would put more probes on the slope. The focus here comes from the measured frame, not the fit.';
  }

  @override
  String focusCardWeakFitPlateauAuto(String r2) {
    return 'The curve fit was weak (R² $r2): the outer probes sit on a plateau, so this sweep was wider than the V. With automatic step size on, Ara sizes the next sweep from the V\'s width where a run lets it be measured, so running again should put more probes on the slope. The focus here comes from the measured frame, not the fit.';
  }

  @override
  String get focusBestPosition => 'Best position';

  @override
  String get focusStars => 'Stars';

  @override
  String get focusFitR2 => 'Fit R²';

  @override
  String focusFocuserAt(String position) {
    return 'Focuser at $position';
  }

  @override
  String get focusMeasuredStars => 'Measured stars';

  @override
  String get bahtinovInFocusMaskOff => 'In focus · mask off';

  @override
  String get bahtinovMaskOff => 'Mask off';

  @override
  String get bahtinovCameraError => 'Stopped by a camera error';

  @override
  String get bahtinovWaitingFirst => 'Waiting for the first frame';

  @override
  String bahtinovInFocusPx(String px) {
    return 'In focus · $px px';
  }

  @override
  String bahtinovHintPx(String hint, String px) {
    return '$hint · $px px from focus';
  }

  @override
  String bahtinovStoppedLast(String px) {
    return 'Stopped · last $px px from focus';
  }

  @override
  String get bahtinovFitMask => 'Fit the mask, then Start';

  @override
  String get bahtinovFitMaskDetail =>
      'Put the Bahtinov mask over the front of the telescope and point at a bright star. Ara uses the brightest star in the frame.';

  @override
  String get bahtinovWaiting => 'Waiting';

  @override
  String get bahtinovWaitingDetail => 'The first frame is on its way.';

  @override
  String get bahtinovNoStar => 'No star';

  @override
  String get bahtinovNoStarDetail =>
      'Point at a bright star, or lengthen the exposure.';

  @override
  String get bahtinovNearEdge => 'Star at the edge';

  @override
  String get bahtinovNearEdgeDetail =>
      'Center the star so its spikes fit in the frame.';

  @override
  String get bahtinovNoSpikes => 'No spikes';

  @override
  String get bahtinovNoSpikesDetail =>
      'Fit the mask over the front of the telescope. Far out of focus? Turn until the star shrinks and spikes appear.';

  @override
  String get bahtinovInFocus => 'In focus';

  @override
  String bahtinovInFocusZoneDetail(String um) {
    return 'The middle spike is centered, inside the ±$um µm focus zone. Lock the focuser, then Finish.';
  }

  @override
  String get bahtinovInFocusDetail =>
      'The middle spike is centered. Lock the focuser, then Finish.';

  @override
  String get bahtinovMakeMove => 'Make a move';

  @override
  String get bahtinovMakeMoveDetail =>
      'Turn the focuser a little either way, then watch the number.';

  @override
  String get bahtinovGoBack => 'Go back';

  @override
  String bahtinovPassedDetail(String size) {
    return 'You passed focus. Turn back about $size your last move.';
  }

  @override
  String get bahtinovKeepGoing => 'Keep going';

  @override
  String bahtinovKeepGoingDetail(String size) {
    return 'Same way — about $size your last move again.';
  }

  @override
  String get bahtinovWrongWayDetail =>
      'Wrong way — turn back past where you started.';

  @override
  String get bahtinovMoveQuarter => 'a quarter of';

  @override
  String get bahtinovMoveThird => 'a third of';

  @override
  String get bahtinovMoveHalf => 'half';

  @override
  String get bahtinovMoveThreeQuarters => 'three quarters of';

  @override
  String get bahtinovMoveSame => 'the same as';

  @override
  String get bahtinovMoveOneAndHalf => '1½×';

  @override
  String get bahtinovMoveTwice => 'twice';

  @override
  String get bahtinovMoveSeveral => 'several times';

  @override
  String get bahtinovPxFromFocus => 'px from focus';

  @override
  String get bahtinovFinish => 'Finish';

  @override
  String get bahtinovMaskQuestion => 'Take the Bahtinov mask off';

  @override
  String get bahtinovMaskQuestionDetail =>
      'Take the mask off the front of the telescope before you image. Left on, it puts spikes on every star and costs light.';

  @override
  String get bahtinovNotYet => 'Not yet';

  @override
  String get bahtinovMaskRemoved => 'Mask removed';

  @override
  String get bahtinovExposure => 'Exposure';

  @override
  String get focusStartBahtinov => 'Start the Bahtinov readout';

  @override
  String get focusStopBahtinov => 'Stop the Bahtinov readout';

  @override
  String focusWeakFitShort(String r2) {
    return 'Weak curve fit (R² $r2): the focus comes from the measured frame. Run again if the sky has settled.';
  }

  @override
  String get guideFocusStarting => 'Starting…';

  @override
  String get guideFocusNoStarsMeasured => 'Live · no stars measured';

  @override
  String guideFocusLive(String hfr) {
    return 'Live · HFR $hfr';
  }

  @override
  String guideFocusLiveInFocus(String hfr) {
    return 'Live · HFR $hfr — in focus';
  }

  @override
  String guideFocusLiveBest(String hfr) {
    return 'Live · HFR $hfr — best so far';
  }

  @override
  String get guideFocusStoppedError => 'Stopped on an error';

  @override
  String get guideFocusHeld => 'In focus — held, stopped';

  @override
  String guideFocusHeldBest(String hfr) {
    return 'In focus — held, stopped · best HFR $hfr';
  }

  @override
  String guideFocusInFocusBest(String hfr) {
    return 'In focus · best HFR $hfr';
  }

  @override
  String get guideFocusStopped => 'Stopped';

  @override
  String guideFocusStoppedBest(String hfr) {
    return 'Stopped · best HFR $hfr';
  }

  @override
  String get guideFocusWaitingMain => 'Waiting for the main telescope';

  @override
  String get guideFocusNotReady => 'Not ready';

  @override
  String get guideFocusReady => 'Ready to focus';

  @override
  String get guideFocusConnectGuider =>
      'Connect the guider first — the frames come through it.';

  @override
  String get guideFocusStopGuider =>
      'Stop the guider first; it owns its camera while it is looping, guiding, calibrating or paused.';

  @override
  String get guideFocusNoStars => 'No stars';

  @override
  String get guideFocusNoStarsDetail =>
      'Point at a star field or lengthen the exposure. Far out of focus? Make big moves until stars appear.';

  @override
  String get guideFocusBigMoves => 'big moves';

  @override
  String get guideFocusSmallMoves => 'small moves';

  @override
  String guideFocusInFocusDetail(String target) {
    return 'Stars are as tight as this camera resolves (in focus ≤ $target px). Lock the focuser.';
  }

  @override
  String get guideFocusSharpest => 'Sharpest so far';

  @override
  String get guideFocusSharpestDetail =>
      'Hold here, or nudge a touch either way to confirm.';

  @override
  String guideFocusMakeMoveDetail(String size) {
    return 'In or out, $size — then watch which way the number goes.';
  }

  @override
  String guideFocusKeepGoingDetail(String size) {
    return 'Same way you just moved — $size.';
  }

  @override
  String guideFocusGoBackDetail(String size) {
    return 'The other way from your last move — $size.';
  }

  @override
  String get guideFocusHolding => 'Holding';

  @override
  String get guideFocusHoldingFar =>
      'Still well off focus — make a bigger move and watch the number.';

  @override
  String get guideFocusHoldingNear => 'Try a small move either way.';

  @override
  String get focusGuide => 'Guide';

  @override
  String get focusGuideScope =>
      'Guide scope: turn its focuser, or slide the camera in the draw tube, in small moves and watch the number fall.';

  @override
  String get focusGuideOag =>
      'Off-axis guider: it shares the main scope\'s focus. Turn the OAG\'s helical focuser in small moves and watch the number fall.';

  @override
  String get focusGuideMainFirst =>
      'Focus the main telescope first: an off-axis guider sees the same focal plane, so its HFR only means something once the main scope is in focus.';

  @override
  String get focusAlreadyInFocus => 'Already in focus';

  @override
  String get focusFrames => 'Frames';

  @override
  String get focusStartGuide => 'Start the guide camera\'s live focus';

  @override
  String get focusStopGuide => 'Stop the guide camera\'s live focus';

  @override
  String get focusScopeMain => 'Main';

  @override
  String get focusScopeGuide => 'Guide';

  @override
  String get guidedNextFocus => 'Next: Focus';

  @override
  String get guidedNextPolarAlign => 'Next: Polar align';

  @override
  String get guidedStartImaging => 'Start imaging';

  @override
  String get guidedImagingStartsAfter =>
      'Imaging starts after focus and polar alignment.';

  @override
  String get guidedDoneStartImaging => 'Done · Start imaging';

  @override
  String get shellGuideFocalLength => 'Guide scope focal length';

  @override
  String get shellGuidePixelSize => 'Guide camera pixel size';

  @override
  String get searchWordsGuideScope =>
      'guide scope guider camera pixel focal polar';

  @override
  String get shellModeGuide => 'Guide';

  @override
  String get guidedNextGuide => 'Next: Guide';

  @override
  String get guideStart => 'Start guiding';

  @override
  String get guideStop => 'Stop guiding';

  @override
  String get guideActionStart => 'start guiding';

  @override
  String get guideActionStop => 'stop guiding';

  @override
  String get guideIdle => 'Idle';

  @override
  String get guideNoGuider => 'Guider not connected';

  @override
  String get guideTapShutter =>
      'Tap the shutter to start guiding. It calibrates first when needed.';

  @override
  String get guideConnectFirst => 'Connect the guider in Settings › Devices.';

  @override
  String get deviceGuiderCamera => 'Guider Camera';

  @override
  String get shellNoDevices => 'No devices connected';

  @override
  String get guideStarting => 'Starting…';

  @override
  String get guideStopping => 'Stopping…';

  @override
  String get guideExposureOwn => 'The guider\'s own';

  @override
  String get focusNeedsFocuser =>
      'Connect a focuser to autofocus, or focus by hand on Manual.';

  @override
  String get shellMainCameraBusy =>
      'The main camera is busy: wait for its frame, or stop it.';
}
