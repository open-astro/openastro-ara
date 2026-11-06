// ignore: unused_import
import 'package:intl/intl.dart' as intl;

import 'app_localizations.dart';

// ignore_for_file: type=lint

/// The translations for Portuguese (`pt`).
class AppLocalizationsPt extends AppLocalizations {
  AppLocalizationsPt([String locale = 'pt']) : super(locale);

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
  String get previewPlanSkipped => 'Skipped: conditions not met';

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
  String get polarStarLost => 'The guider cannot see the star. Check the sky.';

  @override
  String get polarExposure => 'Exposure';

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
  String get searchSectionGuider => 'Guide Camera';

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
  String shellDiscardEditQuestion(String name) {
    return 'Discard changes to $name?';
  }

  @override
  String get shellKeepEditing => 'Keep editing';

  @override
  String get shellDiscard => 'Discard';

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
  String get shellSectionGuider => 'Guide Camera';

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
  String planStopsAtDawn(String time) {
    return 'Stops at dawn · $time';
  }

  @override
  String get planSquareParks => 'Parks';

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
  String get planStoppedAtDawn => 'stopped at dawn';

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
  String get shellModeGuide => 'Guider';

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
  String get deviceGuiderCamera => 'Guide Camera';

  @override
  String get shellNoDevices => 'No devices connected';

  @override
  String get guideStarting => 'Starting…';

  @override
  String get guideStopping => 'Stopping…';

  @override
  String guideStopRefused(String state) {
    return 'The guider didn\'t stop: it is still $state. Try Stop again.';
  }

  @override
  String get guideExposureOwn => 'The guider\'s own';

  @override
  String get focusNeedsFocuser =>
      'Connect a focuser to autofocus, or focus by hand on Manual.';

  @override
  String get shellMainCameraBusy =>
      'The main camera is busy: wait for its frame, or stop it.';

  @override
  String get albumTitle => 'Sky Album';

  @override
  String get albumBookMessier => 'Messier';

  @override
  String get albumBookCaldwell => 'Caldwell';

  @override
  String get dsoTypeGalaxy => 'Galaxy';

  @override
  String get dsoTypeGalaxyPair => 'Galaxy pair';

  @override
  String get dsoTypeGalaxyTriplet => 'Galaxy triplet';

  @override
  String get dsoTypeGalaxyGroup => 'Galaxy group';

  @override
  String get dsoTypeNebula => 'Nebula';

  @override
  String get dsoTypeEmissionNebula => 'Emission nebula';

  @override
  String get dsoTypeReflectionNebula => 'Reflection nebula';

  @override
  String get dsoTypePlanetaryNebula => 'Planetary nebula';

  @override
  String get dsoTypeSupernovaRemnant => 'Supernova remnant';

  @override
  String get dsoTypeCluster => 'Cluster';

  @override
  String get dsoTypeOpenCluster => 'Open cluster';

  @override
  String get dsoTypeGlobularCluster => 'Globular cluster';

  @override
  String get dsoTypeClusterNebula => 'Cluster + nebula';

  @override
  String get dsoTypeObject => 'Object';

  @override
  String get albumCatalogs => 'Catalogs';

  @override
  String albumBookCount(String book, int collected) {
    return '$book · $collected';
  }

  @override
  String albumCollected(int collected, int total) {
    return '$collected / $total collected';
  }

  @override
  String get albumTierBronze => 'Bronze';

  @override
  String get albumTierSilver => 'Silver';

  @override
  String get albumTierGold => 'Gold';

  @override
  String albumTierCount(String tier, int count) {
    return '$tier $count';
  }

  @override
  String get albumEmpty => 'Image a target to collect your first card';

  @override
  String albumShelfTitle(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count missing cards well placed tonight',
      one: '1 missing card well placed tonight',
    );
    return '$_temp0';
  }

  @override
  String get albumNotCollected => 'Not collected yet';

  @override
  String albumSetNumber(int number, int total) {
    return '$number / $total';
  }

  @override
  String get albumNight => 'Night';

  @override
  String get albumLights => 'Lights';

  @override
  String get albumSub => 'Sub';

  @override
  String get albumHours => 'Hours';

  @override
  String albumNightsTotal(int nights) {
    String _temp0 = intl.Intl.pluralLogic(
      nights,
      locale: localeName,
      other: '$nights nights',
      one: '1 night',
    );
    return 'Total · $_temp0';
  }

  @override
  String get albumDidYouKnow => 'Did you know?';

  @override
  String albumArtCredit(String credit) {
    return 'Art (cropped): $credit';
  }

  @override
  String get shellAlbum => 'Sky Album';

  @override
  String get albumImageThis => 'Image this target';

  @override
  String get albumAddTime => 'Add more time';

  @override
  String albumLoadFailed(String reason) {
    return 'Couldn\'t load the album: $reason';
  }

  @override
  String get albumNoServer => 'Connect to a rig to fill the album';

  @override
  String albumCardSemantics(String label, String name, String state) {
    return '$label $name, $state';
  }

  @override
  String get albumFilterAll => 'All';

  @override
  String get albumFilterCollected => 'Collected';

  @override
  String get albumFilterMissing => 'Missing';

  @override
  String albumNewCards(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count new cards',
      one: '1 new card',
    );
    return '$_temp0';
  }

  @override
  String albumTiersUp(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count tiers up',
      one: '1 tier up',
    );
    return '$_temp0';
  }

  @override
  String albumSinceLastVisit(String changes) {
    return '$changes since your last visit';
  }
}

/// The translations for Portuguese, as used in Brazil (`pt_BR`).
class AppLocalizationsPtBr extends AppLocalizationsPt {
  AppLocalizationsPtBr() : super('pt_BR');

  @override
  String pillsMore(int count) {
    return '+$count mais';
  }

  @override
  String get pillsShowFewer => 'Mostrar menos';

  @override
  String pillsMoreShort(int count) {
    return '+$count';
  }

  @override
  String pillsMoreSemantics(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: 'Mais $count mensagens',
      one: 'Mais 1 mensagem',
    );
    return '$_temp0';
  }

  @override
  String statusMoreSemantics(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: 'Mais $count status',
      one: 'Mais 1 status',
    );
    return '$_temp0';
  }

  @override
  String get pillsDismiss => 'Dispensar';

  @override
  String get safetyAlertSemantics => 'Alerta de segurança';

  @override
  String get safetyAlertTitle => 'ALERTA DE SEGURANÇA';

  @override
  String get safetyUnsafe => 'Condições INSEGURAS.';

  @override
  String get safetyRinging => 'Alarme tocando';

  @override
  String get safetySilenceBefore => 'Silencie antes que toque';

  @override
  String get safetySilence => 'Silenciar alarme';

  @override
  String get deviceCamera => 'Câmera principal';

  @override
  String get deviceMount => 'Montagem';

  @override
  String get deviceFocuser => 'Focalizador';

  @override
  String get deviceGuider => 'Guia';

  @override
  String get deviceConnecting => 'Conectando…';

  @override
  String get deviceNotConnected => 'Não conectado';

  @override
  String get deviceCurrentTemperature => 'Temperatura do sensor';

  @override
  String get deviceCooler => 'Refrigeração';

  @override
  String get deviceNoCooler => 'Sem refrigeração';

  @override
  String get deviceTarget => 'Temperatura alvo';

  @override
  String get deviceCouldntSaveTarget => 'Não foi possível salvar o alvo';

  @override
  String get deviceCouldntChangeCooler =>
      'Não foi possível alterar a refrigeração';

  @override
  String get deviceSdk => 'SDK';

  @override
  String get deviceDriver => 'Driver';

  @override
  String get deviceFirmware => 'Firmware';

  @override
  String get deviceSerialNumber => 'Número de série';

  @override
  String get deviceLess => 'Menos';

  @override
  String get deviceMore => 'Mais';

  @override
  String get deviceOn => 'Ligado';

  @override
  String get deviceOff => 'Desligado';

  @override
  String get deviceVersions => 'Versões';

  @override
  String get deviceTracking => 'Rastreamento';

  @override
  String get devicePark => 'Estacionar';

  @override
  String get deviceParking => 'Estacionando…';

  @override
  String get deviceUnparking => 'Desestacionando…';

  @override
  String get deviceParkUnfinished => 'A montagem não terminou de estacionar';

  @override
  String get deviceUnparkUnfinished =>
      'A montagem não terminou de desestacionar';

  @override
  String get deviceParked => 'Estacionada';

  @override
  String get deviceUnparked => 'Livre';

  @override
  String get devicePlanRunningParkAnyway =>
      'Há um plano em andamento. Estacionar a montagem mesmo assim?';

  @override
  String get deviceCancel => 'Cancelar';

  @override
  String get deviceParkAnyway => 'Estacionar mesmo assim';

  @override
  String get deviceMeridianFlip => 'Virada de meridiano';

  @override
  String get deviceAuto => 'Auto';

  @override
  String get deviceCouldntSaveMeridianFlip =>
      'Não foi possível salvar a virada de meridiano';

  @override
  String get deviceCouldntMoveMount => 'Não foi possível mover a montagem';

  @override
  String get deviceGain => 'Ganho';

  @override
  String get deviceBusy => 'Ainda ocupado';

  @override
  String get deviceBusyDetail =>
      'O último comando ainda não terminou. Tente de novo em instantes.';

  @override
  String get deviceCouldntMoveFocuser => 'Não foi possível mover o focalizador';

  @override
  String get deviceCouldntSaveThat => 'Não foi possível salvar';

  @override
  String get deviceNever => 'Nunca';

  @override
  String get deviceRefocusTemp => 'Refocar a cada';

  @override
  String get devicePosition => 'Posição';

  @override
  String get deviceTemperature => 'Temperatura';

  @override
  String get deviceMoving => 'Movendo…';

  @override
  String get deviceFocusPosition => 'Posição do foco';

  @override
  String get deviceStep => 'Passo';

  @override
  String get deviceAutofocus => 'Foco automático';

  @override
  String get deviceCancelAutofocus => 'Cancelar foco automático';

  @override
  String get deviceState => 'Estado';

  @override
  String get deviceRms => 'RMS';

  @override
  String get deviceDither => 'Dithering';

  @override
  String get deviceDitherBy => 'Dithering de';

  @override
  String devicePowerPercent(int percent) {
    return '$percent % de potência';
  }

  @override
  String deviceLetGoToMoveTo(String position) {
    return 'Solte para ir a $position';
  }

  @override
  String deviceMovingTo(String position) {
    return 'Indo para $position…';
  }

  @override
  String deviceRmsRaDec(String ra, String dec) {
    return 'AR $ra · Dec $dec';
  }

  @override
  String deviceDitherEveryFrames(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: 'A cada $count quadros',
      one: 'A cada quadro',
    );
    return '$_temp0';
  }

  @override
  String previewFree(String size) {
    return '$size GB livres';
  }

  @override
  String get previewUnplugged => 'Desconectado';

  @override
  String get previewStorage => 'Armazenamento';

  @override
  String get previewCpu => 'CPU';

  @override
  String get previewMemory => 'Memória';

  @override
  String get previewUp => 'Ativo';

  @override
  String get previewHfr => 'HFR';

  @override
  String get previewStars => 'Estrelas';

  @override
  String get previewExposure => 'Exposição';

  @override
  String get previewGainOffset => 'Ganho · offset';

  @override
  String get previewClipped => 'Saturado';

  @override
  String get previewLoop => 'Contínuo';

  @override
  String get previewSingle => 'Único';

  @override
  String get previewDone => 'OK';

  @override
  String get previewStop => 'Parar';

  @override
  String get previewTakeFrame => 'Fazer um quadro';

  @override
  String previewStartsAt(String time) {
    return 'Começa às $time';
  }

  @override
  String get previewStopping => 'Parando…';

  @override
  String get previewTakingFrame => 'Fazendo um quadro';

  @override
  String get previewStarting => 'Iniciando…';

  @override
  String previewSecondsLeft(int seconds) {
    return 'faltam $seconds s';
  }

  @override
  String get previewDownloading => 'Baixando…';

  @override
  String get previewFrameReady => 'Quadro pronto';

  @override
  String get previewCaptureFailed => 'Falha na captura';

  @override
  String get previewLooping => 'Em contínuo';

  @override
  String get previewPlanPaused => 'Plano pausado';

  @override
  String get previewPlan => 'Plano';

  @override
  String get previewPlanSkipped => 'Pulado: fora das condições';

  @override
  String get previewParking => 'Estacionando';

  @override
  String get previewUnparking => 'Desestacionando';

  @override
  String get previewCooling => 'Resfriando';

  @override
  String previewStatusSpoken(String label, String detail) {
    return '$label, $detail';
  }

  @override
  String get focusAuto => 'Auto';

  @override
  String get focusManual => 'Manual';

  @override
  String get focusAutofocus => 'Foco automático';

  @override
  String get focusCancelAutofocus => 'Cancelar foco automático';

  @override
  String focusHfr(String value) {
    return 'HFR $value';
  }

  @override
  String get focusNoFocuser => 'Nenhum focalizador conectado';

  @override
  String get polarStart => 'Iniciar alinhamento polar';

  @override
  String get polarStop => 'Parar';

  @override
  String get polarActionStart => 'iniciar o alinhamento polar';

  @override
  String get polarActionStop => 'parar o alinhamento polar';

  @override
  String get polarActionSave => 'salvar os ajustes do alinhamento polar';

  @override
  String get polarActionComplete => 'concluir o alinhamento polar';

  @override
  String get polarPhaseSeeding => 'Encontrando o polo…';

  @override
  String get polarPhaseAdjusting => 'Ajustando';

  @override
  String get polarPhasePaused => 'Pausado';

  @override
  String get polarPhaseFailed => 'Falha no alinhamento polar';

  @override
  String get polarPhaseReady => 'Pronto';

  @override
  String get polarAltitude => 'Altitude';

  @override
  String get polarAzimuth => 'Azimute';

  @override
  String polarBoltWorse(String axis) {
    return '$axis piorou. Volte esse parafuso além do ponto de partida.';
  }

  @override
  String get polarStarLost => 'O guia não vê a estrela. Verifique o céu.';

  @override
  String get polarExposure => 'Exposição';

  @override
  String get polarDone => 'Concluir';

  @override
  String get searchHint => 'Objetos, ajustes, modos, imagens';

  @override
  String get searchGroupSky => 'Céu';

  @override
  String get searchGroupSettings => 'Ajustes';

  @override
  String get searchGroupModes => 'Modos';

  @override
  String get searchGroupImages => 'Imagens';

  @override
  String get searchImageLibrary => 'Biblioteca de imagens';

  @override
  String searchNoMatch(String query) {
    return 'Nada no Modo Simples corresponde a \"$query\".';
  }

  @override
  String searchMatches(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count resultados',
      one: '1 resultado',
    );
    return '$_temp0';
  }

  @override
  String get searchSectionRig => 'Setup';

  @override
  String get searchSectionDevices => 'Dispositivos';

  @override
  String get searchSectionCamera => 'Câmera principal';

  @override
  String get searchSectionMount => 'Montagem';

  @override
  String get searchSectionFocuser => 'Focalizador';

  @override
  String get searchSectionGuider => 'Câmera de guiagem';

  @override
  String get searchSettingAperture => 'Abertura do telescópio principal';

  @override
  String get searchSettingFocalLength =>
      'Distância focal do telescópio principal';

  @override
  String get searchSettingLatitude => 'Latitude';

  @override
  String get searchSettingLongitude => 'Longitude';

  @override
  String get searchSettingElevation => 'Elevação';

  @override
  String get searchSettingLocationTime => 'Local e hora';

  @override
  String get searchSettingPermanentPier => 'Pilar fixo';

  @override
  String get searchSettingSimpleMode => 'Modo Simples';

  @override
  String get searchSettingChangeProfile => 'Alterar perfil';

  @override
  String get searchSettingChangeRig => 'Alterar setup';

  @override
  String get searchSettingVersions => 'Versões';

  @override
  String get searchSettingLogs => 'Logs e relatório de erro';

  @override
  String get searchSettingConnect => 'Conectar';

  @override
  String get searchSettingDisconnect => 'Desconectar';

  @override
  String get searchSettingReconnect => 'Reconectar';

  @override
  String get searchSettingCooler => 'Refrigeração';

  @override
  String get searchSettingCoolerTarget => 'Alvo da refrigeração';

  @override
  String get searchSettingGain => 'Ganho';

  @override
  String get searchSettingTracking => 'Rastreamento';

  @override
  String get searchSettingPark => 'Estacionar';

  @override
  String get searchSettingMeridianFlip => 'Virada de meridiano';

  @override
  String get searchSettingFocuserStep => 'Passo do focalizador';

  @override
  String get searchSettingDither => 'Dithering';

  @override
  String get searchWordsRig => 'sistema perfil setup';

  @override
  String get searchWordsAperture => 'telescópio diâmetro';

  @override
  String get searchWordsFocalLength => 'telescópio';

  @override
  String get searchWordsSite => 'local localização coordenadas';

  @override
  String get searchWordsElevation => 'local altitude altura elevação';

  @override
  String get searchWordsLocationTime => 'gps relógio fuso horário local';

  @override
  String get searchWordsPermanentPier => 'alinhamento polar observatório pier';

  @override
  String get searchWordsSimpleMode => 'interface completa avançado';

  @override
  String get searchWordsChangeProfile => 'perfil';

  @override
  String get searchWordsChangeRig => 'servidor';

  @override
  String get searchWordsVersions => 'versão sobre firmware driver';

  @override
  String get searchWordsLogs => 'suporte logs bug erro';

  @override
  String get searchWordsDevices => 'equipamento dispositivos';

  @override
  String get searchWordsCooler =>
      'câmera temperatura resfriamento resfriador cooler';

  @override
  String get searchWordsGain => 'câmera iso hcg';

  @override
  String get searchWordsTracking => 'montagem sideral';

  @override
  String get searchWordsPark => 'montagem desestacionar park';

  @override
  String get searchWordsMeridianFlip => 'montagem lado do pilar flip';

  @override
  String get searchWordsFocuser => 'foco posição botão mover';

  @override
  String get searchWordsFocuserStep => 'foco mover';

  @override
  String get searchWordsGuider => 'guiagem rms phd2';

  @override
  String get searchWordsDither => 'guiagem dithering';

  @override
  String get nightSetupStepFocus => 'Foco';

  @override
  String get nightSetupStepPolarAlign => 'Alinh. polar';

  @override
  String get nightSetupStepPlan => 'Plano';

  @override
  String get nightSetupStepGuide => 'Guiagem';

  @override
  String nightSetupStepDone(String step) {
    return '$step, concluído';
  }

  @override
  String nightSetupStepSkipped(String step) {
    return '$step, pulado';
  }

  @override
  String nightSetupStepCurrent(String step) {
    return '$step, etapa atual';
  }

  @override
  String nightSetupStepNext(String step) {
    return '$step, próximo';
  }

  @override
  String get nightSetupBack => 'Voltar';

  @override
  String get nightSetupSkip => 'Pular';

  @override
  String get shellModeNightSetup => 'Preparar a noite';

  @override
  String get shellModePreview => 'Prévia';

  @override
  String get shellModePolarAlign => 'Alinh. polar';

  @override
  String get shellModeFocus => 'Foco';

  @override
  String get shellModePlan => 'Plano';

  @override
  String get shellRailPolarAlign => 'A.P.';

  @override
  String get shellRailNightSetup => 'Preparo';

  @override
  String shellStopAgain(String shortcut) {
    return 'Pressione $shortcut de novo para parar a sessão';
  }

  @override
  String shellDiscardEditQuestion(String name) {
    return 'Descartar as alterações em $name?';
  }

  @override
  String get shellKeepEditing => 'Continuar editando';

  @override
  String get shellDiscard => 'Descartar';

  @override
  String get shellShowControls => 'Mostrar controles';

  @override
  String get shellHideControls => 'Ocultar controles';

  @override
  String get shellTapShutter => 'Toque no obturador para fazer um quadro';

  @override
  String get shellImageThis => 'Fotografar';

  @override
  String get shellSearch => 'Buscar';

  @override
  String get shellNightModeOn => 'Modo noturno ligado';

  @override
  String get shellNightMode => 'Modo noturno';

  @override
  String get shellSettings => 'Ajustes';

  @override
  String get shellSky => 'Céu';

  @override
  String get shellInfo => 'Info';

  @override
  String get shellLibrary => 'Biblioteca';

  @override
  String shellModeSemantics(String mode) {
    return 'Modo: $mode';
  }

  @override
  String get shellGuiding => 'Guiagem';

  @override
  String shellGuidingRms(String rms) {
    return 'RMS $rms';
  }

  @override
  String get shellRa => 'AR';

  @override
  String get shellDec => 'Dec';

  @override
  String get shellHistogram => 'Histograma';

  @override
  String shellHistogramMedian(int median) {
    return 'Mediana $median';
  }

  @override
  String get shellSectionRig => 'Setup';

  @override
  String get shellSectionDevices => 'Dispositivos';

  @override
  String get shellSectionCamera => 'Câmera principal';

  @override
  String get shellSectionMount => 'Montagem';

  @override
  String get shellSectionFocuser => 'Focalizador';

  @override
  String get shellSectionGuider => 'Câmera de guiagem';

  @override
  String get shellRigNotConnected => 'Setup não conectado';

  @override
  String get shellFilterWheel => 'Roda de filtros';

  @override
  String get shellRotator => 'Rotacionador';

  @override
  String get shellFlatPanel => 'Painel de flat';

  @override
  String get shellDome => 'Cúpula';

  @override
  String get shellWeather => 'Clima';

  @override
  String shellGearTitle(String type, String name) {
    return '$type · $name';
  }

  @override
  String shellGearActionFailed(String action, String device) {
    return '$action falhou · $device';
  }

  @override
  String get shellOk => 'OK';

  @override
  String get shellBedtimeTitle => 'Antes de dormir';

  @override
  String get shellBedtimeAllGood => 'Tudo pronto para a noite.';

  @override
  String get shellBedtimeAnswer => 'Entendi — vou dormir';

  @override
  String get shellConnected => 'Conectado';

  @override
  String get shellConnecting => 'Conectando…';

  @override
  String get shellFailed => 'Falhou';

  @override
  String get shellNotConnected => 'Não conectado';

  @override
  String get shellDisconnect => 'Desconectar';

  @override
  String get shellReconnect => 'Reconectar';

  @override
  String get shellConnect => 'Conectar';

  @override
  String get shellCouldntSave => 'Não foi possível salvar';

  @override
  String get shellCouldntSetClock =>
      'Não foi possível acertar o relógio do setup';

  @override
  String get shellCouldntGetLocation => 'Não foi possível obter a localização';

  @override
  String get shellCouldntSaveSite => 'Não foi possível salvar o local';

  @override
  String shellSiteFrom(String source) {
    return 'Local obtido de $source';
  }

  @override
  String get shellChange => 'Alterar';

  @override
  String get shellName => 'Nome';

  @override
  String get shellChangeRig => 'Alterar setup';

  @override
  String get shellProfile => 'Perfil';

  @override
  String get shellChangeProfile => 'Alterar perfil';

  @override
  String get shellPlanRunning =>
      'Há um plano em andamento. Ele continua rodando no setup; escolha este perfil de novo para voltar a ele.';

  @override
  String get shellAperture => 'Abertura do telescópio principal';

  @override
  String get shellFocalLength => 'Distância focal do telescópio principal';

  @override
  String get shellLatitude => 'Latitude';

  @override
  String get shellLongitude => 'Longitude';

  @override
  String get shellElevation => 'Elevação';

  @override
  String shellUseLocation(String device) {
    return 'Usar local e hora de $device';
  }

  @override
  String get shellPermanentPier => 'Pilar fixo';

  @override
  String get shellPermanentPierHint => 'Pula o alinhamento polar';

  @override
  String get shellSimpleMode => 'Modo Simples';

  @override
  String get shellThisApp => 'Este app';

  @override
  String get shellAraServer => 'Servidor Ara';

  @override
  String get shellStarDatabase => 'Banco de estrelas';

  @override
  String get shellSystem => 'Sistema';

  @override
  String get shellLogs => 'Logs e relatório de erro';

  @override
  String get shellNotSet => 'Não definido';

  @override
  String get shellHemiNorth => 'N';

  @override
  String get shellHemiSouth => 'S';

  @override
  String get shellHemiEast => 'L';

  @override
  String get shellHemiWest => 'O';

  @override
  String shellWheelSemantics(String label, int index, int count) {
    return '$label, roda $index de $count';
  }

  @override
  String get shellRaise => 'Aumentar';

  @override
  String get shellLower => 'Diminuir';

  @override
  String get planNeverRises => 'Nunca nasce aqui';

  @override
  String get planBelowHorizon => 'Abaixo do seu horizonte';

  @override
  String planBelowHorizonRises(String time) {
    return 'Abaixo do seu horizonte · nasce às $time';
  }

  @override
  String planAltitudeUpMax(int alt, int max) {
    return '$alt° de altura · máx. $max°';
  }

  @override
  String planAltitudeUp(int alt) {
    return '$alt° de altura';
  }

  @override
  String get planBelowTheHorizon => 'abaixo do horizonte';

  @override
  String planRisesAt(String time) {
    return 'nasce às $time';
  }

  @override
  String planRisesAtCap(String time) {
    return 'Nasce às $time';
  }

  @override
  String get planPausing => 'Pausando…';

  @override
  String get planPaused => 'Pausado';

  @override
  String get planDone => 'Concluído';

  @override
  String planMoreInQueue(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: 'Mais $count na fila',
      one: 'Mais 1 na fila',
    );
    return '$_temp0';
  }

  @override
  String get planQueued => 'na fila';

  @override
  String planSquareLabel(String name) {
    return 'Plano: $name';
  }

  @override
  String get planPickTargetFirst => 'Escolha um alvo primeiro';

  @override
  String get planStartImaging => 'Começar a fotografar';

  @override
  String get planPauseAfterFrame => 'Pausar após este quadro';

  @override
  String get planPausingLabel => 'Pausando';

  @override
  String get planResume => 'Retomar';

  @override
  String get planStopTheRun => 'Parar a sessão';

  @override
  String get planHoldToStop => 'Segure para parar';

  @override
  String get planTonightsBest => 'MELHORES DA NOITE';

  @override
  String planEditTarget(String name) {
    return 'Editar $name';
  }

  @override
  String get planSearchObjects => 'Buscar objetos';

  @override
  String get planSearch => 'Buscar';

  @override
  String get planSearchOrPick => 'Busque ou escolha no céu.';

  @override
  String get planPickInSky => 'Escolher no céu';

  @override
  String get planExposure => 'Exposição';

  @override
  String planExposureSuggested(String exposure) {
    return '$exposure · sugerido';
  }

  @override
  String get planFrames => 'Quadros';

  @override
  String planSuggested(String hint) {
    return 'Sugerido: $hint';
  }

  @override
  String get planUseAll => 'Usar todos';

  @override
  String planFrameCount(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count quadros',
      one: '1 quadro',
    );
    return '$_temp0';
  }

  @override
  String get planNotPolarAligned => 'Sem alinhamento polar esta noite';

  @override
  String get planStartsNow => 'Começa agora';

  @override
  String planStartsAtDark(String time) {
    return 'Começa ao escurecer ($time)';
  }

  @override
  String planStopsAtDawn(String time) {
    return 'Para ao amanhecer · $time';
  }

  @override
  String get planSquareParks => 'Estaciona';

  @override
  String planStartsAt(String time) {
    return 'Começa às $time';
  }

  @override
  String get planStartNowInstead => 'Começar agora';

  @override
  String planWaitForDark(String time) {
    return 'Esperar escurecer ($time)';
  }

  @override
  String get planPickATime => 'Escolher horário';

  @override
  String get planCancel => 'Cancelar';

  @override
  String get planSaving => 'Salvando…';

  @override
  String get planSave => 'Salvar';

  @override
  String planTargetAdded(String name, int count) {
    return '$name adicionado · $count na fila';
  }

  @override
  String get planAddToQueue => 'Adicionar à fila';

  @override
  String get planStarting => 'Iniciando…';

  @override
  String planStartAt(String time) {
    return 'Começar às $time';
  }

  @override
  String get planStartNow => 'Começar agora';

  @override
  String get planCreateAndStart => 'Criar e começar';

  @override
  String planTargetRemoved(String name) {
    return '$name removido';
  }

  @override
  String get planUndo => 'Desfazer';

  @override
  String planMoveTarget(String name) {
    return 'Mover $name';
  }

  @override
  String planRemoveFromQueue(String name) {
    return 'Remover $name da fila';
  }

  @override
  String get planRemove => 'Remover';

  @override
  String planQueueHeader(int count, String duration) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: 'alvos',
      one: 'alvo',
    );
    return 'FILA · $count $_temp0 · $duration';
  }

  @override
  String get planOpenStorage => 'Abrir armazenamento';

  @override
  String get planNightDone => 'Noite concluída';

  @override
  String get planStoppedAtDawn => 'parada ao amanhecer';

  @override
  String get planStopped => 'parada';

  @override
  String get planFailed => 'falhou';

  @override
  String planStoppedAt(String step) {
    return 'Parou em: $step';
  }

  @override
  String get planImageLibrary => 'Biblioteca de imagens';

  @override
  String get planNewPlan => 'Novo plano';

  @override
  String planImagingTarget(String name) {
    return 'FOTOGRAFANDO $name';
  }

  @override
  String get planMountStopped => 'Montagem parada';

  @override
  String planEndsLeft(String time, String duration) {
    return 'Termina às $time · faltam $duration';
  }

  @override
  String get planHintResume =>
      'Toque no obturador para retomar e recentralizar; segure para parar.';

  @override
  String get planResumeWithoutRecenter => 'Retomar sem recentralizar';

  @override
  String get planRecenterAndRefocus => 'Recentralizar e refocar';

  @override
  String get planHintPause =>
      'Toque no obturador para pausar após este quadro; segure para parar.';

  @override
  String get planAddTarget => 'Adicionar alvo';

  @override
  String planMagnitude(String value) {
    return 'mag $value';
  }

  @override
  String planAlreadyQueued(String name) {
    return '$name já está na fila.';
  }

  @override
  String get planRigUnreachable =>
      'Não foi possível falar com o setup. Verifique a conexão e tente de novo.';

  @override
  String get planActionCreate => 'criar o plano';

  @override
  String get planActionSaveEdit => 'salvar a edição';

  @override
  String get planActionPutBack => 'colocá-lo de volta';

  @override
  String get planActionReorder => 'reordenar a fila';

  @override
  String get planActionRemove => 'removê-lo da fila';

  @override
  String get planActionSchedule => 'agendar o início';

  @override
  String get planActionStart => 'iniciar a captura';

  @override
  String get planActionPause => 'pausar';

  @override
  String get planActionResume => 'retomar';

  @override
  String get planActionStop => 'parar a sessão';

  @override
  String get planRunEnding =>
      'A sessão está terminando; este alvo não pode entrar nela.';

  @override
  String get planQueueBusy =>
      'A fila ainda está sendo atualizada no setup. Tente de novo em instantes.';

  @override
  String get shellLanguage => 'Idioma';

  @override
  String get shellLanguageSystem => 'Sistema';

  @override
  String get estopCompactStop => 'PARAR';

  @override
  String get estopCompactHold => 'SEGURE';

  @override
  String get estopCompactPressAgain => 'OUTRA\nVEZ';

  @override
  String get estopStopping => 'Parando…';

  @override
  String get estopHoldToConfirm => 'Segure para confirmar';

  @override
  String get estopPressAgain => 'Pressione de novo';

  @override
  String get estopLabel => 'Parada de emergência';

  @override
  String get estopPressAgainAnnouncement =>
      'Pressione de novo em até 3 segundos para parar tudo';

  @override
  String get estopConfirmTitle => 'Parar tudo agora?';

  @override
  String get estopConfirmBody =>
      'Isto aborta a sequência em andamento e a exposição atual, para a guiagem, estaciona a montagem e desliga a luz do painel de flat e a refrigeração da câmera. Não pode ser desfeito — a sequência não é retomada.';

  @override
  String get estopCancel => 'Cancelar';

  @override
  String get estopConfirm => 'PARADA DE EMERGÊNCIA';

  @override
  String get estopNoServer => 'Nenhum servidor conectado — nada para parar.';

  @override
  String estopFailed(String error) {
    return 'FALHA NA PARADA DE EMERGÊNCIA — $error Verifique seu setup manualmente.';
  }

  @override
  String get estopActionReach => 'contatar o setup';

  @override
  String get estopAlreadyRunning =>
      'Uma parada de emergência já está em andamento — nenhum segundo comando enviado.';

  @override
  String estopRunsAborted(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count sequências abortadas',
      one: '1 sequência abortada',
    );
    return '$_temp0';
  }

  @override
  String get estopNoSequence => 'nenhuma sequência em andamento';

  @override
  String get estopExposureAborted => 'exposição abortada';

  @override
  String get estopGuidingStopped => 'guiagem parada';

  @override
  String get estopParkRequested => 'montagem instruída a estacionar';

  @override
  String get estopFlatPanelOff => 'luz do painel de flat desligada';

  @override
  String get estopCoolerOff => 'refrigeração da câmera desligada';

  @override
  String estopSummary(String parts) {
    return 'Parada de emergência executada: $parts.';
  }

  @override
  String estopCheckRig(String failures) {
    return 'VERIFIQUE O SETUP: $failures.';
  }

  @override
  String get estopFailAbortRuns =>
      'FALHA AO ABORTAR A SEQUÊNCIA (ela pode ainda estar rodando)';

  @override
  String get estopFailAbortExposure => 'FALHA AO ABORTAR A EXPOSIÇÃO';

  @override
  String get estopFailStopGuiding =>
      'A GUIAGEM PODE AINDA ESTAR ATIVA (falha ao parar)';

  @override
  String get estopFailPark =>
      'FALHA AO ESTACIONAR A MONTAGEM — verifique manualmente';

  @override
  String get estopFailFlatPanel => 'FALHA AO DESLIGAR A LUZ DO PAINEL DE FLAT';

  @override
  String get estopFailCooler =>
      'REFRIGERAÇÃO DA CÂMERA AINDA LIGADA (falha ao desligar)';

  @override
  String estopFailOther(String step) {
    return '$step FALHOU';
  }

  @override
  String get focusCardInFocus => 'Em foco';

  @override
  String focusCardInFocusHfr(String hfr) {
    return 'Em foco · HFR $hfr';
  }

  @override
  String get focusCardFailed => 'Foco automático falhou';

  @override
  String get focusCardCancelled => 'Cancelado';

  @override
  String get focusCardNotFocused => 'Ainda sem foco';

  @override
  String get focusCardFailedSeeLogs =>
      'O foco automático falhou — veja Suporte → Logs';

  @override
  String focusCardBackAt(String position) {
    return 'O focalizador voltou para $position.';
  }

  @override
  String focusCardSmart(String shot, String total) {
    return 'Smart Focus — imagem $shot de $total';
  }

  @override
  String get focusCardCoarse => 'Buscando o foco aproximado…';

  @override
  String focusCardSweeping(String done, String total) {
    return 'Varrendo — ponto $done de $total';
  }

  @override
  String focusCardPass(String pass) {
    return '(passada $pass)';
  }

  @override
  String get focusCardFitting => 'Ajustando a curva…';

  @override
  String get focusCardMoving => 'Indo para o melhor foco…';

  @override
  String get focusCardConfirming => 'Confirmando o foco…';

  @override
  String get focusCardBacklash => 'Medindo a folga do focalizador…';

  @override
  String get focusCardRunning => 'Foco automático em andamento…';

  @override
  String focusCardInFocusAt(String position) {
    return 'Em foco em $position';
  }

  @override
  String focusCardTook(String duration) {
    return 'em $duration';
  }

  @override
  String focusCardFailedReason(String reason) {
    return 'Falhou: $reason';
  }

  @override
  String get focusCardSeeLogs => 'veja Suporte → Logs';

  @override
  String focusCardRestoredPosition(String position) {
    return 'focalizador de volta em $position';
  }

  @override
  String get focusCardNoRunYet => 'Nenhum foco automático nesta sessão ainda.';

  @override
  String get filterDropdownLabel => 'Filtro';

  @override
  String get filterDropdownDidntMove =>
      'Não foi possível trocar o filtro - a roda não começou a se mover.';

  @override
  String filterDropdownNotASlot(String name) {
    return 'O filtro \'$name\' não é uma posição da roda conectada.';
  }

  @override
  String get filterDropdownBusy => 'Outra ação ainda está em andamento.';

  @override
  String get filterDropdownActionChange => 'trocar o filtro';

  @override
  String get polarHintOnTarget => 'No alvo';

  @override
  String get polarHintLower => 'Abaixar';

  @override
  String get polarHintRaise => 'Levantar';

  @override
  String get polarHintMoveWest => 'Mover para oeste';

  @override
  String get polarHintMoveEast => 'Mover para leste';

  @override
  String get polarHintAltitudeWorse =>
      'A altitude piorou depois desse giro — nesta montagem o parafuso de altitude move o eixo no sentido oposto. Volte-o além de onde você começou.';

  @override
  String get polarHintAzimuthWorse =>
      'O azimute piorou depois desse giro — nesta montagem o parafuso de azimute move o eixo no sentido oposto. Volte-o além de onde você começou.';

  @override
  String get guiderStateNotGuiding => 'Sem guiagem';

  @override
  String get guiderStateLooping => 'Em contínuo';

  @override
  String get guiderStateStarSelected => 'Estrela selecionada';

  @override
  String get guiderStateCalibrating => 'Calibrando';

  @override
  String get guiderStateGuiding => 'Guiando';

  @override
  String get guiderStateSettling => 'Estabilizando';

  @override
  String get guiderStatePaused => 'Pausado';

  @override
  String get guiderStateStarLost => 'Estrela perdida';

  @override
  String get guiderStateDithering => 'Fazendo dither';

  @override
  String get guiderStateAssistant => 'Assistente de guiagem';

  @override
  String get guiderStateUnknown => 'Desconhecido';

  @override
  String get guiderStateNotConnected => 'Não conectado';

  @override
  String guiderStateCalibrationStep(String direction, String step) {
    return '$direction passo $step';
  }

  @override
  String errorCouldnt(String action) {
    return 'Não foi possível $action';
  }

  @override
  String get errorActionDefault => 'fazer isso';

  @override
  String errorNoAnswer(String device) {
    return 'seu setup não respondeu. Verifique se ele está ligado e na mesma rede que $device.';
  }

  @override
  String get errorTooSlow =>
      'seu setup demorou demais para responder. Ele pode estar ocupado; tente de novo em instantes.';

  @override
  String get errorUntrusted => 'a conexão não era confiável.';

  @override
  String get errorCancelled => 'a solicitação foi cancelada.';

  @override
  String get errorNotFound =>
      'seu setup não tem isso. Ele pode estar com uma versão mais antiga do Ara.';

  @override
  String get errorBusy => 'outra coisa está usando isso agora.';

  @override
  String get errorRefusedValues => 'seu setup recusou esses valores.';

  @override
  String get errorServer =>
      'seu setup teve um erro. Suporte → Logs tem os detalhes.';

  @override
  String get errorRefused => 'seu setup recusou a solicitação.';

  @override
  String get errorConnectionFailed =>
      'a conexão falhou. Verifique se o setup está acessível.';

  @override
  String get errorFile => 'um problema de arquivo atrapalhou';

  @override
  String get errorUnreadable =>
      'seu setup enviou algo que o Ara não entendeu. Ele pode estar com outra versão.';

  @override
  String focusCardWeakFit(String r2) {
    return 'O ajuste da curva foi fraco (R² $r2) — nuvem passando, um movimento durante a varredura ou sondas desiguais dos dois lados do V. O foco aqui vem do quadro medido, não do ajuste; rode de novo se o céu estabilizou.';
  }

  @override
  String focusCardWeakFitPlateau(String r2) {
    return 'O ajuste da curva foi fraco (R² $r2): as sondas das pontas ficaram num platô, então a varredura é mais larga que o V. Um passo menor colocaria mais sondas na rampa. O foco aqui vem do quadro medido, não do ajuste.';
  }

  @override
  String focusCardWeakFitPlateauAuto(String r2) {
    return 'O ajuste da curva foi fraco (R² $r2): as sondas das pontas ficaram num platô, então esta varredura foi mais larga que o V. Com o passo automático ligado, o Ara dimensiona a próxima varredura pela largura do V quando dá para medi-la, então rodar de novo deve pôr mais sondas na rampa. O foco aqui vem do quadro medido, não do ajuste.';
  }

  @override
  String get focusBestPosition => 'Melhor posição';

  @override
  String get focusStars => 'Estrelas';

  @override
  String get focusFitR2 => 'R² do ajuste';

  @override
  String focusFocuserAt(String position) {
    return 'Focalizador em $position';
  }

  @override
  String get focusMeasuredStars => 'Estrelas medidas';

  @override
  String get bahtinovInFocusMaskOff => 'Em foco · sem máscara';

  @override
  String get bahtinovMaskOff => 'Sem máscara';

  @override
  String get bahtinovCameraError => 'Parado por um erro da câmera';

  @override
  String get bahtinovWaitingFirst => 'Aguardando o primeiro quadro';

  @override
  String bahtinovInFocusPx(String px) {
    return 'Em foco · $px px';
  }

  @override
  String bahtinovHintPx(String hint, String px) {
    return '$hint · $px px do foco';
  }

  @override
  String bahtinovStoppedLast(String px) {
    return 'Parado · último $px px do foco';
  }

  @override
  String get bahtinovFitMask => 'Coloque a máscara e toque em Iniciar';

  @override
  String get bahtinovFitMaskDetail =>
      'Coloque a máscara de Bahtinov na frente do telescópio e aponte para uma estrela brilhante. O Ara usa a estrela mais brilhante do quadro.';

  @override
  String get bahtinovWaiting => 'Aguardando';

  @override
  String get bahtinovWaitingDetail => 'O primeiro quadro está a caminho.';

  @override
  String get bahtinovNoStar => 'Sem estrela';

  @override
  String get bahtinovNoStarDetail =>
      'Aponte para uma estrela brilhante ou aumente a exposição.';

  @override
  String get bahtinovNearEdge => 'Estrela na borda';

  @override
  String get bahtinovNearEdgeDetail =>
      'Centralize a estrela para as espículas caberem no quadro.';

  @override
  String get bahtinovNoSpikes => 'Sem espículas';

  @override
  String get bahtinovNoSpikesDetail =>
      'Coloque a máscara na frente do telescópio. Muito fora de foco? Gire até a estrela diminuir e as espículas aparecerem.';

  @override
  String get bahtinovInFocus => 'Em foco';

  @override
  String bahtinovInFocusZoneDetail(String um) {
    return 'A espícula do meio está centrada, dentro da zona de foco de ±$um µm. Trave o focalizador e toque em Concluir.';
  }

  @override
  String get bahtinovInFocusDetail =>
      'A espícula do meio está centrada. Trave o focalizador e toque em Concluir.';

  @override
  String get bahtinovMakeMove => 'Faça um movimento';

  @override
  String get bahtinovMakeMoveDetail =>
      'Gire o focalizador um pouco para qualquer lado e observe o número.';

  @override
  String get bahtinovGoBack => 'Volte';

  @override
  String bahtinovPassedDetail(String size) {
    return 'Você passou do foco. Volte cerca de $size do último movimento.';
  }

  @override
  String get bahtinovKeepGoing => 'Continue';

  @override
  String bahtinovKeepGoingDetail(String size) {
    return 'Mesmo sentido — cerca de $size do último movimento de novo.';
  }

  @override
  String get bahtinovWrongWayDetail =>
      'Sentido errado — volte além de onde começou.';

  @override
  String get bahtinovMoveQuarter => 'um quarto';

  @override
  String get bahtinovMoveThird => 'um terço';

  @override
  String get bahtinovMoveHalf => 'metade';

  @override
  String get bahtinovMoveThreeQuarters => 'três quartos';

  @override
  String get bahtinovMoveSame => 'o mesmo';

  @override
  String get bahtinovMoveOneAndHalf => '1½×';

  @override
  String get bahtinovMoveTwice => 'o dobro';

  @override
  String get bahtinovMoveSeveral => 'várias vezes';

  @override
  String get bahtinovPxFromFocus => 'px do foco';

  @override
  String get bahtinovFinish => 'Concluir';

  @override
  String get bahtinovMaskQuestion => 'Tire a máscara de Bahtinov';

  @override
  String get bahtinovMaskQuestionDetail =>
      'Tire a máscara da frente do telescópio antes de fotografar. Se ficar, ela põe espículas em todas as estrelas e rouba luz.';

  @override
  String get bahtinovNotYet => 'Ainda não';

  @override
  String get bahtinovMaskRemoved => 'Máscara retirada';

  @override
  String get bahtinovExposure => 'Exposição';

  @override
  String get focusStartBahtinov => 'Iniciar a leitura de Bahtinov';

  @override
  String get focusStopBahtinov => 'Parar a leitura de Bahtinov';

  @override
  String focusWeakFitShort(String r2) {
    return 'Ajuste da curva fraco (R² $r2): o foco vem do quadro medido. Rode de novo se o céu estabilizou.';
  }

  @override
  String get guideFocusStarting => 'Iniciando…';

  @override
  String get guideFocusNoStarsMeasured => 'Ao vivo · nenhuma estrela medida';

  @override
  String guideFocusLive(String hfr) {
    return 'Ao vivo · HFR $hfr';
  }

  @override
  String guideFocusLiveInFocus(String hfr) {
    return 'Ao vivo · HFR $hfr — em foco';
  }

  @override
  String guideFocusLiveBest(String hfr) {
    return 'Ao vivo · HFR $hfr — o melhor até agora';
  }

  @override
  String get guideFocusStoppedError => 'Parado por um erro';

  @override
  String get guideFocusHeld => 'Em foco — mantido, parado';

  @override
  String guideFocusHeldBest(String hfr) {
    return 'Em foco — mantido, parado · melhor HFR $hfr';
  }

  @override
  String guideFocusInFocusBest(String hfr) {
    return 'Em foco · melhor HFR $hfr';
  }

  @override
  String get guideFocusStopped => 'Parado';

  @override
  String guideFocusStoppedBest(String hfr) {
    return 'Parado · melhor HFR $hfr';
  }

  @override
  String get guideFocusWaitingMain => 'Aguardando o telescópio principal';

  @override
  String get guideFocusNotReady => 'Não está pronto';

  @override
  String get guideFocusReady => 'Pronto para focar';

  @override
  String get guideFocusConnectGuider =>
      'Conecte a guiagem primeiro — os quadros vêm por ela.';

  @override
  String get guideFocusStopGuider =>
      'Pare a guiagem primeiro; ela usa a câmera enquanto faz loop, guia, calibra ou está pausada.';

  @override
  String get guideFocusNoStars => 'Sem estrelas';

  @override
  String get guideFocusNoStarsDetail =>
      'Aponte para um campo de estrelas ou aumente a exposição. Muito fora de foco? Faça movimentos grandes até as estrelas aparecerem.';

  @override
  String get guideFocusBigMoves => 'movimentos grandes';

  @override
  String get guideFocusSmallMoves => 'movimentos pequenos';

  @override
  String guideFocusInFocusDetail(String target) {
    return 'As estrelas estão tão finas quanto esta câmera resolve (em foco ≤ $target px). Trave o focalizador.';
  }

  @override
  String get guideFocusSharpest => 'O mais nítido até agora';

  @override
  String get guideFocusSharpestDetail =>
      'Fique aqui, ou mexa um pouquinho para qualquer lado para confirmar.';

  @override
  String guideFocusMakeMoveDetail(String size) {
    return 'Para dentro ou para fora, $size — e veja para onde o número vai.';
  }

  @override
  String guideFocusKeepGoingDetail(String size) {
    return 'Mesmo sentido do último movimento — $size.';
  }

  @override
  String guideFocusGoBackDetail(String size) {
    return 'O sentido contrário ao último movimento — $size.';
  }

  @override
  String get guideFocusHolding => 'Parado no lugar';

  @override
  String get guideFocusHoldingFar =>
      'Ainda bem fora de foco — faça um movimento maior e observe o número.';

  @override
  String get guideFocusHoldingNear =>
      'Tente um movimento pequeno para qualquer lado.';

  @override
  String get focusGuide => 'Guia';

  @override
  String get focusGuideScope =>
      'Luneta guia: gire o focalizador, ou deslize a câmera no tubo, em movimentos pequenos e veja o número cair.';

  @override
  String get focusGuideOag =>
      'Guia fora do eixo: compartilha o foco do telescópio principal. Gire o focalizador helicoidal do OAG em movimentos pequenos e veja o número cair.';

  @override
  String get focusGuideMainFirst =>
      'Foque o telescópio principal primeiro: um guia fora do eixo vê o mesmo plano focal, então o HFR dele só vale depois que o principal estiver em foco.';

  @override
  String get focusAlreadyInFocus => 'Já está em foco';

  @override
  String get focusFrames => 'Quadros';

  @override
  String get focusStartGuide => 'Iniciar o foco ao vivo da câmera de guia';

  @override
  String get focusStopGuide => 'Parar o foco ao vivo da câmera de guia';

  @override
  String get focusScopeMain => 'Principal';

  @override
  String get focusScopeGuide => 'Guia';

  @override
  String get guidedNextFocus => 'Próximo: Foco';

  @override
  String get guidedNextPolarAlign => 'Próximo: Alinhamento polar';

  @override
  String get guidedStartImaging => 'Iniciar a captura';

  @override
  String get guidedImagingStartsAfter =>
      'A captura começa depois do foco e do alinhamento polar.';

  @override
  String get guidedDoneStartImaging => 'Pronto · Iniciar a captura';

  @override
  String get shellGuideFocalLength => 'Distância focal da luneta guia';

  @override
  String get shellGuidePixelSize => 'Pixel da câmera de guia';

  @override
  String get searchWordsGuideScope =>
      'luneta guia guiagem câmera pixel distância focal polar guide scope';

  @override
  String get shellModeGuide => 'Guia';

  @override
  String get guidedNextGuide => 'Próximo: Guiagem';

  @override
  String get guideStart => 'Iniciar guiagem';

  @override
  String get guideStop => 'Parar guiagem';

  @override
  String get guideActionStart => 'iniciar a guiagem';

  @override
  String get guideActionStop => 'parar a guiagem';

  @override
  String get guideIdle => 'Parado';

  @override
  String get guideNoGuider => 'Guia não conectado';

  @override
  String get guideTapShutter =>
      'Toque no obturador para iniciar a guiagem. Ele calibra antes, quando preciso.';

  @override
  String get guideConnectFirst =>
      'Conecte o guia em Configurações › Dispositivos.';

  @override
  String get deviceGuiderCamera => 'Câmera de guiagem';

  @override
  String get shellNoDevices => 'Nenhum dispositivo conectado';

  @override
  String get guideStarting => 'Iniciando…';

  @override
  String get guideStopping => 'Parando…';

  @override
  String guideStopRefused(String state) {
    return 'O guia não parou: continua $state. Tente Parar de novo.';
  }

  @override
  String get guideExposureOwn => 'O do guia';

  @override
  String get focusNeedsFocuser =>
      'Conecte um focalizador para o foco automático, ou foque à mão no Manual.';

  @override
  String get shellMainCameraBusy =>
      'A câmera principal está ocupada: espere o quadro terminar, ou pare-o.';

  @override
  String get albumTitle => 'Álbum do Céu';

  @override
  String get albumBookMessier => 'Messier';

  @override
  String get albumBookCaldwell => 'Caldwell';

  @override
  String get dsoTypeGalaxy => 'Galáxia';

  @override
  String get dsoTypeGalaxyPair => 'Par de galáxias';

  @override
  String get dsoTypeGalaxyTriplet => 'Trio de galáxias';

  @override
  String get dsoTypeGalaxyGroup => 'Grupo de galáxias';

  @override
  String get dsoTypeNebula => 'Nebulosa';

  @override
  String get dsoTypeEmissionNebula => 'Nebulosa de emissão';

  @override
  String get dsoTypeReflectionNebula => 'Nebulosa de reflexão';

  @override
  String get dsoTypePlanetaryNebula => 'Nebulosa planetária';

  @override
  String get dsoTypeSupernovaRemnant => 'Remanescente de supernova';

  @override
  String get dsoTypeCluster => 'Aglomerado';

  @override
  String get dsoTypeOpenCluster => 'Aglomerado aberto';

  @override
  String get dsoTypeGlobularCluster => 'Aglomerado globular';

  @override
  String get dsoTypeClusterNebula => 'Aglomerado + nebulosa';

  @override
  String get dsoTypeObject => 'Objeto';

  @override
  String get albumCatalogs => 'Catálogos';

  @override
  String albumBookCount(String book, int collected) {
    return '$book · $collected';
  }

  @override
  String albumCollected(int collected, int total) {
    return '$collected / $total coletados';
  }

  @override
  String get albumTierBronze => 'Bronze';

  @override
  String get albumTierSilver => 'Prata';

  @override
  String get albumTierGold => 'Ouro';

  @override
  String albumTierCount(String tier, int count) {
    return '$tier $count';
  }

  @override
  String get albumEmpty => 'Fotografe um alvo para coletar seu primeiro cartão';

  @override
  String albumShelfTitle(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count cartões faltando bem posicionados esta noite',
      one: '1 cartão faltando bem posicionado esta noite',
    );
    return '$_temp0';
  }

  @override
  String get albumNotCollected => 'Ainda não coletado';

  @override
  String albumSetNumber(int number, int total) {
    return '$number / $total';
  }

  @override
  String get albumNight => 'Noite';

  @override
  String get albumLights => 'Quadros';

  @override
  String get albumSub => 'Sub';

  @override
  String get albumHours => 'Horas';

  @override
  String albumNightsTotal(int nights) {
    String _temp0 = intl.Intl.pluralLogic(
      nights,
      locale: localeName,
      other: '$nights noites',
      one: '1 noite',
    );
    return 'Total · $_temp0';
  }

  @override
  String get albumDidYouKnow => 'Você sabia?';

  @override
  String albumArtCredit(String credit) {
    return 'Arte (recortada): $credit';
  }

  @override
  String get shellAlbum => 'Álbum do Céu';

  @override
  String get albumImageThis => 'Fotografar este alvo';

  @override
  String get albumAddTime => 'Adicionar mais tempo';

  @override
  String albumLoadFailed(String reason) {
    return 'Não foi possível carregar o álbum: $reason';
  }

  @override
  String get albumNoServer =>
      'Conecte-se a um equipamento para preencher o álbum';

  @override
  String albumCardSemantics(String label, String name, String state) {
    return '$label $name, $state';
  }

  @override
  String get albumFilterAll => 'Todos';

  @override
  String get albumFilterCollected => 'Coletados';

  @override
  String get albumFilterMissing => 'Faltando';

  @override
  String albumNewCards(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count cartas novas',
      one: '1 carta nova',
    );
    return '$_temp0';
  }

  @override
  String albumTiersUp(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count níveis acima',
      one: '1 nível acima',
    );
    return '$_temp0';
  }

  @override
  String albumSinceLastVisit(String changes) {
    return '$changes desde a última visita';
  }
}
