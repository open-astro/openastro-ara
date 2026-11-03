import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:intl/intl.dart' as intl;

import 'app_localizations_en.dart';
import 'app_localizations_pt.dart';

// ignore_for_file: type=lint

/// Callers can lookup localized strings with an instance of AppLocalizations
/// returned by `AppLocalizations.of(context)`.
///
/// Applications need to include `AppLocalizations.delegate()` in their app's
/// `localizationDelegates` list, and the locales they support in the app's
/// `supportedLocales` list. For example:
///
/// ```dart
/// import 'l10n/app_localizations.dart';
///
/// return MaterialApp(
///   localizationsDelegates: AppLocalizations.localizationsDelegates,
///   supportedLocales: AppLocalizations.supportedLocales,
///   home: MyApplicationHome(),
/// );
/// ```
///
/// ## Update pubspec.yaml
///
/// Please make sure to update your pubspec.yaml to include the following
/// packages:
///
/// ```yaml
/// dependencies:
///   # Internationalization support.
///   flutter_localizations:
///     sdk: flutter
///   intl: any # Use the pinned version from flutter_localizations
///
///   # Rest of dependencies
/// ```
///
/// ## iOS Applications
///
/// iOS applications define key application metadata, including supported
/// locales, in an Info.plist file that is built into the application bundle.
/// To configure the locales supported by your app, you’ll need to edit this
/// file.
///
/// First, open your project’s ios/Runner.xcworkspace Xcode workspace file.
/// Then, in the Project Navigator, open the Info.plist file under the Runner
/// project’s Runner folder.
///
/// Next, select the Information Property List item, select Add Item from the
/// Editor menu, then select Localizations from the pop-up menu.
///
/// Select and expand the newly-created Localizations item then, for each
/// locale your application supports, add a new item and select the locale
/// you wish to add from the pop-up menu in the Value field. This list should
/// be consistent with the languages listed in the AppLocalizations.supportedLocales
/// property.
abstract class AppLocalizations {
  AppLocalizations(String locale)
    : localeName = intl.Intl.canonicalizedLocale(locale.toString());

  final String localeName;

  static AppLocalizations? of(BuildContext context) {
    return Localizations.of<AppLocalizations>(context, AppLocalizations);
  }

  static const LocalizationsDelegate<AppLocalizations> delegate =
      _AppLocalizationsDelegate();

  /// A list of this localizations delegate along with the default localizations
  /// delegates.
  ///
  /// Returns a list of localizations delegates containing this delegate along with
  /// GlobalMaterialLocalizations.delegate, GlobalCupertinoLocalizations.delegate,
  /// and GlobalWidgetsLocalizations.delegate.
  ///
  /// Additional delegates can be added by appending to this list in
  /// MaterialApp. This list does not have to be used at all if a custom list
  /// of delegates is preferred or required.
  static const List<LocalizationsDelegate<dynamic>> localizationsDelegates =
      <LocalizationsDelegate<dynamic>>[
        delegate,
        GlobalMaterialLocalizations.delegate,
        GlobalCupertinoLocalizations.delegate,
        GlobalWidgetsLocalizations.delegate,
      ];

  /// A list of this localizations delegate's supported locales.
  static const List<Locale> supportedLocales = <Locale>[
    Locale('en'),
    Locale('pt'),
    Locale('pt', 'BR'),
  ];

  /// Simple Mode message pills: the pill under the visible messages that unfolds the hidden ones; count is how many are hidden
  ///
  /// In en, this message translates to:
  /// **'+{count} more'**
  String pillsMore(int count);

  /// Simple Mode message pills: the pill that folds the extra messages away again
  ///
  /// In en, this message translates to:
  /// **'Show fewer'**
  String get pillsShowFewer;

  /// Simple Mode message pills: short count of hidden messages at the end of the newest pill when there is no room below it
  ///
  /// In en, this message translates to:
  /// **'+{count}'**
  String pillsMoreShort(int count);

  /// Simple Mode message pills: screen reader label for the short +N count button
  ///
  /// In en, this message translates to:
  /// **'{count, plural, =1{1 more message} other{{count} more messages}}'**
  String pillsMoreSemantics(int count);

  /// Simple Mode message pills: tooltip of the close button on an error message
  ///
  /// In en, this message translates to:
  /// **'Dismiss'**
  String get pillsDismiss;

  /// Simple Mode safety alarm: screen reader label for the full-screen warning
  ///
  /// In en, this message translates to:
  /// **'Safety alert'**
  String get safetyAlertSemantics;

  /// Simple Mode safety alarm: big heading of the full-screen warning (all caps)
  ///
  /// In en, this message translates to:
  /// **'SAFETY ALERT'**
  String get safetyAlertTitle;

  /// Simple Mode safety alarm: shown when the alarm gives no reason of its own
  ///
  /// In en, this message translates to:
  /// **'Conditions are UNSAFE.'**
  String get safetyUnsafe;

  /// Simple Mode safety alarm: status line while the alarm tone is sounding
  ///
  /// In en, this message translates to:
  /// **'Alarm ringing'**
  String get safetyRinging;

  /// Simple Mode safety alarm: status line during the silent delay before the tone starts
  ///
  /// In en, this message translates to:
  /// **'Silence it before it sounds'**
  String get safetySilenceBefore;

  /// Simple Mode safety alarm: the button that stops the sound and closes the warning
  ///
  /// In en, this message translates to:
  /// **'Silence alarm'**
  String get safetySilence;

  /// Camera window title, before the camera's name
  ///
  /// In en, this message translates to:
  /// **'Camera'**
  String get deviceCamera;

  /// Mount window title, before the mount's name
  ///
  /// In en, this message translates to:
  /// **'Mount'**
  String get deviceMount;

  /// Focuser window title, before the focuser's name
  ///
  /// In en, this message translates to:
  /// **'Focuser'**
  String get deviceFocuser;

  /// Guider window title, before the guide camera's name
  ///
  /// In en, this message translates to:
  /// **'Guider'**
  String get deviceGuider;

  /// Device window caption while the device connects
  ///
  /// In en, this message translates to:
  /// **'Connecting…'**
  String get deviceConnecting;

  /// Device window caption when the device is not connected
  ///
  /// In en, this message translates to:
  /// **'Not connected'**
  String get deviceNotConnected;

  /// Camera window row: what the sensor reads now
  ///
  /// In en, this message translates to:
  /// **'Temperature'**
  String get deviceCurrentTemperature;

  /// Camera window row: the cooler's On / Off
  ///
  /// In en, this message translates to:
  /// **'Cooler'**
  String get deviceCooler;

  /// Camera window: shown in the Cooler row when the camera has no cooler
  ///
  /// In en, this message translates to:
  /// **'No cooler'**
  String get deviceNoCooler;

  /// Camera window row: the cooler's target temperature
  ///
  /// In en, this message translates to:
  /// **'Target temperature'**
  String get deviceTarget;

  /// Error message when the cooler target could not be saved to the profile
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t save the target'**
  String get deviceCouldntSaveTarget;

  /// Error message when the camera refused a cooler change
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t change the cooler'**
  String get deviceCouldntChangeCooler;

  /// Versions table in a device's Settings page: the vendor SDK version
  ///
  /// In en, this message translates to:
  /// **'SDK'**
  String get deviceSdk;

  /// Versions table in a device's Settings page: the driver version
  ///
  /// In en, this message translates to:
  /// **'Driver'**
  String get deviceDriver;

  /// Versions table in a device's Settings page: the firmware version
  ///
  /// In en, this message translates to:
  /// **'Firmware'**
  String get deviceFirmware;

  /// Versions table in a device's Settings page: the device's serial number
  ///
  /// In en, this message translates to:
  /// **'Serial number'**
  String get deviceSerialNumber;

  /// Screen-reader label of the minus button of a device window's stepper
  ///
  /// In en, this message translates to:
  /// **'Less'**
  String get deviceLess;

  /// Screen-reader label of the plus button of a device window's stepper
  ///
  /// In en, this message translates to:
  /// **'More'**
  String get deviceMore;

  /// Device window segment: turns a feature on
  ///
  /// In en, this message translates to:
  /// **'On'**
  String get deviceOn;

  /// Device window segment or value: a feature is off
  ///
  /// In en, this message translates to:
  /// **'Off'**
  String get deviceOff;

  /// Title of the versions table in Settings (Rig, and a device's page)
  ///
  /// In en, this message translates to:
  /// **'Versions'**
  String get deviceVersions;

  /// Mount window row: tracking On / Off
  ///
  /// In en, this message translates to:
  /// **'Tracking'**
  String get deviceTracking;

  /// Mount window row: Parked / Unparked
  ///
  /// In en, this message translates to:
  /// **'Park'**
  String get devicePark;

  /// Mount window: shown in the Park row while the mount parks
  ///
  /// In en, this message translates to:
  /// **'Parking…'**
  String get deviceParking;

  /// Mount window: shown in the Park row while the mount unparks
  ///
  /// In en, this message translates to:
  /// **'Unparking…'**
  String get deviceUnparking;

  /// Mount window segment in the Park row: the mount is parked
  ///
  /// In en, this message translates to:
  /// **'Parked'**
  String get deviceParked;

  /// Mount window segment in the Park row: the mount is unparked
  ///
  /// In en, this message translates to:
  /// **'Unparked'**
  String get deviceUnparked;

  /// Mount window: asked when Parked is tapped while a plan runs
  ///
  /// In en, this message translates to:
  /// **'A plan is running. Park the mount anyway?'**
  String get devicePlanRunningParkAnyway;

  /// Mount window button: don't park while a plan runs
  ///
  /// In en, this message translates to:
  /// **'Cancel'**
  String get deviceCancel;

  /// Mount window button: park even though a plan runs
  ///
  /// In en, this message translates to:
  /// **'Park anyway'**
  String get deviceParkAnyway;

  /// Mount window row: the automatic meridian flip
  ///
  /// In en, this message translates to:
  /// **'Meridian flip'**
  String get deviceMeridianFlip;

  /// Mount window segment in the Meridian flip row: flip automatically
  ///
  /// In en, this message translates to:
  /// **'Auto'**
  String get deviceAuto;

  /// Error message when the meridian flip setting could not be saved
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t save the meridian flip'**
  String get deviceCouldntSaveMeridianFlip;

  /// Error message when the mount refused to track, park or unpark
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t move the mount'**
  String get deviceCouldntMoveMount;

  /// Camera and guider window row: the camera's gain
  ///
  /// In en, this message translates to:
  /// **'Gain'**
  String get deviceGain;

  /// Error message when the focuser refused a move
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t move the focuser'**
  String get deviceCouldntMoveFocuser;

  /// Error message when a focuser or guider setting could not be saved
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t save that'**
  String get deviceCouldntSaveThat;

  /// Focuser window, Refocus temp row: never refocus on temperature
  ///
  /// In en, this message translates to:
  /// **'Never'**
  String get deviceNever;

  /// Focuser window row: refocus every time the temperature changes by this much (the value reads e.g. "2 °C", or "Never")
  ///
  /// In en, this message translates to:
  /// **'Refocus every'**
  String get deviceRefocusTemp;

  /// Focuser window row: the focuser's position in steps
  ///
  /// In en, this message translates to:
  /// **'Position'**
  String get devicePosition;

  /// Focuser window row: the focuser's temperature
  ///
  /// In en, this message translates to:
  /// **'Temperature'**
  String get deviceTemperature;

  /// Focuser window caption while the focuser moves
  ///
  /// In en, this message translates to:
  /// **'Moving…'**
  String get deviceMoving;

  /// Screen-reader label of the focuser's dial
  ///
  /// In en, this message translates to:
  /// **'Focus position'**
  String get deviceFocusPosition;

  /// Focuser window row: how far one dial click moves
  ///
  /// In en, this message translates to:
  /// **'Step'**
  String get deviceStep;

  /// Button in the middle of the focuser dial: runs autofocus
  ///
  /// In en, this message translates to:
  /// **'Autofocus'**
  String get deviceAutofocus;

  /// Screen-reader label of the focuser dial's middle button while autofocus runs
  ///
  /// In en, this message translates to:
  /// **'Cancel autofocus'**
  String get deviceCancelAutofocus;

  /// Guider window row: what the guider is doing
  ///
  /// In en, this message translates to:
  /// **'State'**
  String get deviceState;

  /// Guider window row: the guiding error (root mean square)
  ///
  /// In en, this message translates to:
  /// **'RMS'**
  String get deviceRms;

  /// Guider window row: how often a plan dithers
  ///
  /// In en, this message translates to:
  /// **'Dither'**
  String get deviceDither;

  /// Guider window row: how far a dither moves, in pixels
  ///
  /// In en, this message translates to:
  /// **'Dither by'**
  String get deviceDitherBy;

  /// Camera window, Current temperature row: the cooler's power
  ///
  /// In en, this message translates to:
  /// **'{percent} % power'**
  String devicePowerPercent(int percent);

  /// Focuser window caption while the dial turns; position is a step count like 12 000
  ///
  /// In en, this message translates to:
  /// **'Let go to move to {position}'**
  String deviceLetGoToMoveTo(String position);

  /// Focuser window caption while the focuser moves to a position (a step count like 12 000)
  ///
  /// In en, this message translates to:
  /// **'Moving to {position}…'**
  String deviceMovingTo(String position);

  /// Guider window, under the RMS value: the error per axis (values like 0.42″)
  ///
  /// In en, this message translates to:
  /// **'RA {ra} · Dec {dec}'**
  String deviceRmsRaDec(String ra, String dec);

  /// Guider window, Dither row: a plan dithers every this many frames
  ///
  /// In en, this message translates to:
  /// **'{count, plural, =1{Every frame} other{{count} frames}}'**
  String deviceDitherEveryFrames(int count);

  /// Simple Mode System tile: free space on the save drive; size is a number in gigabytes
  ///
  /// In en, this message translates to:
  /// **'{size} GB free'**
  String previewFree(String size);

  /// Simple Mode System tile: the save drive can't be read
  ///
  /// In en, this message translates to:
  /// **'Unplugged'**
  String get previewUnplugged;

  /// Simple Mode System tile label: storage free on the server
  ///
  /// In en, this message translates to:
  /// **'Storage'**
  String get previewStorage;

  /// Simple Mode System tile label: server CPU use
  ///
  /// In en, this message translates to:
  /// **'CPU'**
  String get previewCpu;

  /// Simple Mode System tile label: server memory in use
  ///
  /// In en, this message translates to:
  /// **'Memory'**
  String get previewMemory;

  /// Simple Mode System tile label: server uptime (short, tile is 72 px wide)
  ///
  /// In en, this message translates to:
  /// **'Up'**
  String get previewUp;

  /// Simple Mode Info tile label: half-flux radius of the last frame's stars
  ///
  /// In en, this message translates to:
  /// **'HFR'**
  String get previewHfr;

  /// Simple Mode Info tile label: number of stars detected in the last frame
  ///
  /// In en, this message translates to:
  /// **'Stars'**
  String get previewStars;

  /// Simple Mode: exposure length, as an Info tile label and the Preview settings picker label
  ///
  /// In en, this message translates to:
  /// **'Exposure'**
  String get previewExposure;

  /// Simple Mode Info tile label: the last frame's camera gain and offset
  ///
  /// In en, this message translates to:
  /// **'Gain · offset'**
  String get previewGainOffset;

  /// Simple Mode Info tile label: share of saturated (clipped) highlight pixels
  ///
  /// In en, this message translates to:
  /// **'Clipped'**
  String get previewClipped;

  /// Simple Mode: title of the Preview settings popover
  ///
  /// In en, this message translates to:
  /// **'Preview settings'**
  String get previewSettings;

  /// Simple Mode Preview settings: segment for continuous live view (frames not saved)
  ///
  /// In en, this message translates to:
  /// **'Loop'**
  String get previewLoop;

  /// Simple Mode Preview settings: segment for taking one saved frame
  ///
  /// In en, this message translates to:
  /// **'Single'**
  String get previewSingle;

  /// Simple Mode: button closing a wheel picker and applying the pick
  ///
  /// In en, this message translates to:
  /// **'Done'**
  String get previewDone;

  /// Simple Mode shutter button, screen-reader label while something runs
  ///
  /// In en, this message translates to:
  /// **'Stop'**
  String get previewStop;

  /// Simple Mode shutter button, screen-reader label at rest
  ///
  /// In en, this message translates to:
  /// **'Take a frame'**
  String get previewTakeFrame;

  /// Simple Mode status pill: a scheduled plan's start time (HH:MM)
  ///
  /// In en, this message translates to:
  /// **'Starts at {time}'**
  String previewStartsAt(String time);

  /// Simple Mode status pill: the shot is being cancelled
  ///
  /// In en, this message translates to:
  /// **'Stopping…'**
  String get previewStopping;

  /// Simple Mode status pill: the camera is exposing
  ///
  /// In en, this message translates to:
  /// **'Taking a frame'**
  String get previewTakingFrame;

  /// Simple Mode status pill detail: the exposure is about to start
  ///
  /// In en, this message translates to:
  /// **'Starting…'**
  String get previewStarting;

  /// Simple Mode status pill detail: whole seconds left in the exposure (s = seconds)
  ///
  /// In en, this message translates to:
  /// **'{seconds} s left'**
  String previewSecondsLeft(int seconds);

  /// Simple Mode status pill: the frame is downloading from the camera
  ///
  /// In en, this message translates to:
  /// **'Downloading…'**
  String get previewDownloading;

  /// Simple Mode status pill: the frame has arrived
  ///
  /// In en, this message translates to:
  /// **'Frame ready'**
  String get previewFrameReady;

  /// Simple Mode status pill: the capture failed (the reason follows)
  ///
  /// In en, this message translates to:
  /// **'Capture failed'**
  String get previewCaptureFailed;

  /// Simple Mode status pill: live view is running
  ///
  /// In en, this message translates to:
  /// **'Looping'**
  String get previewLooping;

  /// Simple Mode status pill: the imaging plan is paused (its step follows)
  ///
  /// In en, this message translates to:
  /// **'Plan paused'**
  String get previewPlanPaused;

  /// Simple Mode status pill: the imaging plan is running (its step follows)
  ///
  /// In en, this message translates to:
  /// **'Plan'**
  String get previewPlan;

  /// Simple Mode status pill: the mount is parking
  ///
  /// In en, this message translates to:
  /// **'Parking'**
  String get previewParking;

  /// Simple Mode status pill: the mount is unparking
  ///
  /// In en, this message translates to:
  /// **'Unparking'**
  String get previewUnparking;

  /// Simple Mode status pill: the camera sensor is cooling to its target (temperatures follow)
  ///
  /// In en, this message translates to:
  /// **'Cooling'**
  String get previewCooling;

  /// Simple Mode status pill, screen-reader text: the status and its detail
  ///
  /// In en, this message translates to:
  /// **'{label}, {detail}'**
  String previewStatusSpoken(String label, String detail);

  /// Simple Mode Focus: rail chip caption when focus runs by autofocus
  ///
  /// In en, this message translates to:
  /// **'Auto'**
  String get focusAuto;

  /// Simple Mode Focus: rail chip caption when focusing by hand
  ///
  /// In en, this message translates to:
  /// **'Manual'**
  String get focusManual;

  /// Simple Mode Focus shutter, screen-reader label: start autofocus
  ///
  /// In en, this message translates to:
  /// **'Autofocus'**
  String get focusAutofocus;

  /// Simple Mode Focus shutter, screen-reader label while autofocus runs
  ///
  /// In en, this message translates to:
  /// **'Cancel autofocus'**
  String get focusCancelAutofocus;

  /// Simple Mode Focus panel: the big half-flux radius readout; value is a number or a dash
  ///
  /// In en, this message translates to:
  /// **'HFR {value}'**
  String focusHfr(String value);

  /// Simple Mode Focus panel: shown when no focuser is connected
  ///
  /// In en, this message translates to:
  /// **'No focuser connected'**
  String get focusNoFocuser;

  /// Simple Mode Polar align panel: loop mode segment (shoots frames on its own); also the rail chip caption
  ///
  /// In en, this message translates to:
  /// **'Loop'**
  String get polarLoop;

  /// Simple Mode Polar align panel: single mode segment (waits for a tap before each frame); also the rail chip caption
  ///
  /// In en, this message translates to:
  /// **'Single'**
  String get polarSingle;

  /// Polar align shutter button's spoken label when idle
  ///
  /// In en, this message translates to:
  /// **'Start polar alignment'**
  String get polarStart;

  /// Polar align shutter button's spoken label while the routine runs
  ///
  /// In en, this message translates to:
  /// **'Stop'**
  String get polarStop;

  /// Simple Mode Polar align panel: error message tail after "Couldn't " when starting the routine fails; lower-case, no period
  ///
  /// In en, this message translates to:
  /// **'start polar alignment'**
  String get polarActionStart;

  /// Simple Mode Polar align panel: error message tail after "Couldn't " when stopping the routine fails; lower-case, no period
  ///
  /// In en, this message translates to:
  /// **'stop polar alignment'**
  String get polarActionStop;

  /// Simple Mode Polar align panel: error message tail after "Couldn't " when saving its settings fails; lower-case, no period
  ///
  /// In en, this message translates to:
  /// **'save the polar-align settings'**
  String get polarActionSave;

  /// Simple Mode Polar align panel: error message tail after "Couldn't " when taking a frame fails; lower-case, no period
  ///
  /// In en, this message translates to:
  /// **'take a frame'**
  String get polarActionCapture;

  /// Simple Mode Polar align panel: error message tail after "Couldn't " when finishing the routine fails; lower-case, no period
  ///
  /// In en, this message translates to:
  /// **'complete polar alignment'**
  String get polarActionComplete;

  /// Simple Mode Polar align panel: routine phase: first frames, locating the pole
  ///
  /// In en, this message translates to:
  /// **'Finding the pole…'**
  String get polarPhaseSeeding;

  /// Simple Mode Polar align panel: routine phase: the user turns the mount bolts
  ///
  /// In en, this message translates to:
  /// **'Adjusting'**
  String get polarPhaseAdjusting;

  /// Simple Mode Polar align panel: routine phase: paused
  ///
  /// In en, this message translates to:
  /// **'Paused'**
  String get polarPhasePaused;

  /// Simple Mode Polar align panel: routine phase: failed
  ///
  /// In en, this message translates to:
  /// **'Polar alignment failed'**
  String get polarPhaseFailed;

  /// Simple Mode Polar align panel: routine phase: idle
  ///
  /// In en, this message translates to:
  /// **'Ready'**
  String get polarPhaseReady;

  /// Simple Mode Polar align panel: altitude axis row label (bolt to turn)
  ///
  /// In en, this message translates to:
  /// **'Altitude'**
  String get polarAltitude;

  /// Simple Mode Polar align panel: azimuth axis row label (bolt to turn)
  ///
  /// In en, this message translates to:
  /// **'Azimuth'**
  String get polarAzimuth;

  /// Simple Mode Polar align panel: warning after a bolt turn made the error worse
  ///
  /// In en, this message translates to:
  /// **'{axis} got worse. Turn that bolt back past where you started.'**
  String polarBoltWorse(String axis);

  /// Simple Mode Polar align panel: warning when frames fail to plate-solve
  ///
  /// In en, this message translates to:
  /// **'No solve ({count} in a row). Check the sky.'**
  String polarNoSolve(int count);

  /// Simple Mode Polar align panel: exposure picker tile label
  ///
  /// In en, this message translates to:
  /// **'Exposure'**
  String get polarExposure;

  /// Simple Mode Polar align panel: button that shoots the next frame in Single mode
  ///
  /// In en, this message translates to:
  /// **'Take frame'**
  String get polarTakeFrame;

  /// Simple Mode Polar align panel: button that finishes polar alignment
  ///
  /// In en, this message translates to:
  /// **'Done'**
  String get polarDone;

  /// Simple Mode search panel: search field placeholder
  ///
  /// In en, this message translates to:
  /// **'Objects, settings, modes, images'**
  String get searchHint;

  /// Simple Mode search panel: heading over the results found in sky
  ///
  /// In en, this message translates to:
  /// **'Sky'**
  String get searchGroupSky;

  /// Simple Mode search panel: heading over the results found in settings
  ///
  /// In en, this message translates to:
  /// **'Settings'**
  String get searchGroupSettings;

  /// Simple Mode search panel: heading over the results found in modes
  ///
  /// In en, this message translates to:
  /// **'Modes'**
  String get searchGroupModes;

  /// Simple Mode search panel: heading over the results found in images
  ///
  /// In en, this message translates to:
  /// **'Images'**
  String get searchGroupImages;

  /// Simple Mode search panel: detail beside an imaged target result, where it opens
  ///
  /// In en, this message translates to:
  /// **'Image Library'**
  String get searchImageLibrary;

  /// Simple Mode search panel: shown when nothing matches what was typed
  ///
  /// In en, this message translates to:
  /// **'Nothing in Simple Mode matches \"{query}\".'**
  String searchNoMatch(String query);

  /// Simple Mode search panel: what screen readers hear after each keystroke, the number of results shown
  ///
  /// In en, this message translates to:
  /// **'{count, plural, =1{1 match} other{{count} matches}}'**
  String searchMatches(int count);

  /// Simple Mode search panel: name of the Settings sheet's Rig section, shown beside a setting result (and the result for the section itself)
  ///
  /// In en, this message translates to:
  /// **'Rig'**
  String get searchSectionRig;

  /// Simple Mode search panel: name of the Settings sheet's Devices section, shown beside a setting result (and the result for the section itself)
  ///
  /// In en, this message translates to:
  /// **'Devices'**
  String get searchSectionDevices;

  /// Simple Mode search panel: name of the Settings sheet's Camera section, shown beside a setting result (and the result for the section itself)
  ///
  /// In en, this message translates to:
  /// **'Camera'**
  String get searchSectionCamera;

  /// Simple Mode search panel: name of the Settings sheet's Mount section, shown beside a setting result (and the result for the section itself)
  ///
  /// In en, this message translates to:
  /// **'Mount'**
  String get searchSectionMount;

  /// Simple Mode search panel: name of the Settings sheet's Focuser section, shown beside a setting result (and the result for the section itself)
  ///
  /// In en, this message translates to:
  /// **'Focuser'**
  String get searchSectionFocuser;

  /// Simple Mode search panel: name of the Settings sheet's Guider section, shown beside a setting result (and the result for the section itself)
  ///
  /// In en, this message translates to:
  /// **'Guider'**
  String get searchSectionGuider;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Main scope aperture'**
  String get searchSettingAperture;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Main scope focal length'**
  String get searchSettingFocalLength;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Latitude'**
  String get searchSettingLatitude;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Longitude'**
  String get searchSettingLongitude;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Elevation'**
  String get searchSettingElevation;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Location and time'**
  String get searchSettingLocationTime;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Permanent pier'**
  String get searchSettingPermanentPier;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Simple Mode'**
  String get searchSettingSimpleMode;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Change profile'**
  String get searchSettingChangeProfile;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Change rig'**
  String get searchSettingChangeRig;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Versions'**
  String get searchSettingVersions;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Logs & bug report'**
  String get searchSettingLogs;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Connect'**
  String get searchSettingConnect;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Disconnect'**
  String get searchSettingDisconnect;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Reconnect'**
  String get searchSettingReconnect;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Cooler'**
  String get searchSettingCooler;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Cooler target'**
  String get searchSettingCoolerTarget;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Gain'**
  String get searchSettingGain;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Tracking'**
  String get searchSettingTracking;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Park'**
  String get searchSettingPark;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Meridian flip'**
  String get searchSettingMeridianFlip;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Focuser step'**
  String get searchSettingFocuserStep;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Dither'**
  String get searchSettingDither;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'system profile'**
  String get searchWordsRig;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'telescope diameter'**
  String get searchWordsAperture;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'telescope'**
  String get searchWordsFocalLength;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'site location coordinates'**
  String get searchWordsSite;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'site altitude height'**
  String get searchWordsElevation;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'gps clock time zone site'**
  String get searchWordsLocationTime;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'polar alignment observatory'**
  String get searchWordsPermanentPier;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'full ui advanced'**
  String get searchWordsSimpleMode;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'profile'**
  String get searchWordsChangeProfile;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'server'**
  String get searchWordsChangeRig;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'version about firmware driver'**
  String get searchWordsVersions;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'support logs'**
  String get searchWordsLogs;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'equipment gear'**
  String get searchWordsDevices;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'camera temperature cooling'**
  String get searchWordsCooler;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'camera iso hcg'**
  String get searchWordsGain;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'mount sidereal'**
  String get searchWordsTracking;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'mount unpark'**
  String get searchWordsPark;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'mount pier side'**
  String get searchWordsMeridianFlip;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'focus position knob move'**
  String get searchWordsFocuser;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'focus move'**
  String get searchWordsFocuserStep;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'guiding rms phd2'**
  String get searchWordsGuider;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'guiding dithering'**
  String get searchWordsDither;

  /// Simple Mode Guided Run strip: step name: autofocus
  ///
  /// In en, this message translates to:
  /// **'Focus'**
  String get autopilotStepFocus;

  /// Simple Mode Guided Run strip: step name: polar alignment
  ///
  /// In en, this message translates to:
  /// **'Polar align'**
  String get autopilotStepPolarAlign;

  /// Simple Mode Guided Run strip: step name: the imaging plan
  ///
  /// In en, this message translates to:
  /// **'Plan'**
  String get autopilotStepPlan;

  /// Simple Mode Guided Run strip: screen-reader label of a step done tonight
  ///
  /// In en, this message translates to:
  /// **'{step}, done'**
  String autopilotStepDone(String step);

  /// Simple Mode Guided Run strip: screen-reader label of a skipped step
  ///
  /// In en, this message translates to:
  /// **'{step}, skipped'**
  String autopilotStepSkipped(String step);

  /// Simple Mode Guided Run strip: screen-reader label of the current step
  ///
  /// In en, this message translates to:
  /// **'{step}, current step'**
  String autopilotStepCurrent(String step);

  /// Simple Mode Guided Run strip: screen-reader label of a step still ahead
  ///
  /// In en, this message translates to:
  /// **'{step}, next'**
  String autopilotStepNext(String step);

  /// Simple Mode Guided Run strip: button that goes back one step
  ///
  /// In en, this message translates to:
  /// **'Back'**
  String get autopilotBack;

  /// Simple Mode Guided Run strip: button that skips the current step
  ///
  /// In en, this message translates to:
  /// **'Skip'**
  String get autopilotSkip;

  /// Simple Mode Guided Run offer under the status strip: why Guided Run is offered
  ///
  /// In en, this message translates to:
  /// **'Nothing done yet tonight'**
  String get autopilotNothingDone;

  /// Simple Mode Guided Run offer: button that starts Guided Run
  ///
  /// In en, this message translates to:
  /// **'Start Guided Run'**
  String get autopilotStart;

  /// Simple Mode Guided Run offer: tooltip of the × that dismisses it
  ///
  /// In en, this message translates to:
  /// **'Not now'**
  String get autopilotNotNow;

  /// Simple Mode mode name (mode grid, Mode: label for screen readers)
  ///
  /// In en, this message translates to:
  /// **'Guided Run'**
  String get shellModeAutopilot;

  /// Simple Mode mode name
  ///
  /// In en, this message translates to:
  /// **'Preview'**
  String get shellModePreview;

  /// Simple Mode mode name
  ///
  /// In en, this message translates to:
  /// **'Polar align'**
  String get shellModePolarAlign;

  /// Simple Mode mode name
  ///
  /// In en, this message translates to:
  /// **'Focus'**
  String get shellModeFocus;

  /// Simple Mode mode name
  ///
  /// In en, this message translates to:
  /// **'Plan'**
  String get shellModePlan;

  /// Simple Mode right rail: short name of Polar align (60 px wide rail, keep very short)
  ///
  /// In en, this message translates to:
  /// **'P.A.'**
  String get shellRailPolarAlign;

  /// Simple Mode right rail: short name of Guided Run (60 px wide rail, keep very short)
  ///
  /// In en, this message translates to:
  /// **'Guided'**
  String get shellRailAutopilot;

  /// Simple Mode message after the stop keyboard shortcut once; shortcut is the key combo (⌘. or Ctrl+.)
  ///
  /// In en, this message translates to:
  /// **'Press {shortcut} again to stop the run'**
  String shellStopAgain(String shortcut);

  /// Simple Mode screen reader label of the image when the controls are hidden
  ///
  /// In en, this message translates to:
  /// **'Show controls'**
  String get shellShowControls;

  /// Simple Mode screen reader label of the image when the controls are shown
  ///
  /// In en, this message translates to:
  /// **'Hide controls'**
  String get shellHideControls;

  /// Simple Mode hint in the middle of the screen before the first frame
  ///
  /// In en, this message translates to:
  /// **'Tap the shutter to take a frame'**
  String get shellTapShutter;

  /// Simple Mode button on a picked sky object: start imaging it
  ///
  /// In en, this message translates to:
  /// **'Image this'**
  String get shellImageThis;

  /// Simple Mode top bar: search button tooltip
  ///
  /// In en, this message translates to:
  /// **'Search'**
  String get shellSearch;

  /// Simple Mode top bar: night mode button tooltip while night mode is on
  ///
  /// In en, this message translates to:
  /// **'Night mode on'**
  String get shellNightModeOn;

  /// Simple Mode top bar: night mode button tooltip while night mode is off
  ///
  /// In en, this message translates to:
  /// **'Night mode'**
  String get shellNightMode;

  /// Simple Mode top bar: Settings button tooltip; title of the Settings page
  ///
  /// In en, this message translates to:
  /// **'Settings'**
  String get shellSettings;

  /// Simple Mode left rail button: show the sky (planetarium)
  ///
  /// In en, this message translates to:
  /// **'Sky'**
  String get shellSky;

  /// Simple Mode left rail button: show the info strips
  ///
  /// In en, this message translates to:
  /// **'Info'**
  String get shellInfo;

  /// Simple Mode left rail button: open the image library
  ///
  /// In en, this message translates to:
  /// **'Library'**
  String get shellLibrary;

  /// Simple Mode screen reader label of the mode button on the right rail; mode is the mode name
  ///
  /// In en, this message translates to:
  /// **'Mode: {mode}'**
  String shellModeSemantics(String mode);

  /// Simple Mode info strip title: the guiding graph
  ///
  /// In en, this message translates to:
  /// **'Guiding'**
  String get shellGuiding;

  /// Simple Mode guiding strip: total RMS guiding error; rms is the formatted value with its unit
  ///
  /// In en, this message translates to:
  /// **'RMS {rms}'**
  String shellGuidingRms(String rms);

  /// Simple Mode guiding strip legend: right ascension line (abbreviation)
  ///
  /// In en, this message translates to:
  /// **'RA'**
  String get shellRa;

  /// Simple Mode guiding strip legend: declination line (abbreviation)
  ///
  /// In en, this message translates to:
  /// **'Dec'**
  String get shellDec;

  /// Simple Mode info strip title: the frame histogram
  ///
  /// In en, this message translates to:
  /// **'Histogram'**
  String get shellHistogram;

  /// Simple Mode histogram strip: the frame's median pixel value
  ///
  /// In en, this message translates to:
  /// **'Median {median}'**
  String shellHistogramMedian(int median);

  /// Simple Mode Settings section: the rig (site, telescope, versions)
  ///
  /// In en, this message translates to:
  /// **'Rig'**
  String get shellSectionRig;

  /// Simple Mode Settings section: the list of devices
  ///
  /// In en, this message translates to:
  /// **'Devices'**
  String get shellSectionDevices;

  /// Simple Mode Settings section and device row: the camera
  ///
  /// In en, this message translates to:
  /// **'Camera'**
  String get shellSectionCamera;

  /// Simple Mode Settings section and device row: the mount
  ///
  /// In en, this message translates to:
  /// **'Mount'**
  String get shellSectionMount;

  /// Simple Mode Settings section and device row: the focuser
  ///
  /// In en, this message translates to:
  /// **'Focuser'**
  String get shellSectionFocuser;

  /// Simple Mode Settings section and device row: the guider
  ///
  /// In en, this message translates to:
  /// **'Guider'**
  String get shellSectionGuider;

  /// Simple Mode Settings › Devices when the app has no link to the rig
  ///
  /// In en, this message translates to:
  /// **'Rig not connected'**
  String get shellRigNotConnected;

  /// Simple Mode Settings › Devices row: the filter wheel
  ///
  /// In en, this message translates to:
  /// **'Filter wheel'**
  String get shellFilterWheel;

  /// Simple Mode Settings › Devices row: the rotator
  ///
  /// In en, this message translates to:
  /// **'Rotator'**
  String get shellRotator;

  /// Simple Mode Settings › Devices row: the flat panel
  ///
  /// In en, this message translates to:
  /// **'Flat panel'**
  String get shellFlatPanel;

  /// Simple Mode Settings › Devices row: the dome
  ///
  /// In en, this message translates to:
  /// **'Dome'**
  String get shellDome;

  /// Simple Mode Settings › Devices row: the weather station
  ///
  /// In en, this message translates to:
  /// **'Weather'**
  String get shellWeather;

  /// Simple Mode Settings › Devices row title: device type and its name
  ///
  /// In en, this message translates to:
  /// **'{type} · {name}'**
  String shellGearTitle(String type, String name);

  /// Simple Mode Settings › Devices row: device state
  ///
  /// In en, this message translates to:
  /// **'Connected'**
  String get shellConnected;

  /// Simple Mode Settings › Devices row: device state
  ///
  /// In en, this message translates to:
  /// **'Connecting…'**
  String get shellConnecting;

  /// Simple Mode Settings › Devices row: device state after a failed connect
  ///
  /// In en, this message translates to:
  /// **'Failed'**
  String get shellFailed;

  /// Simple Mode Settings › Devices row: device state
  ///
  /// In en, this message translates to:
  /// **'Not connected'**
  String get shellNotConnected;

  /// Simple Mode Settings › Devices row button
  ///
  /// In en, this message translates to:
  /// **'Disconnect'**
  String get shellDisconnect;

  /// Simple Mode Settings › Devices row button after a failure
  ///
  /// In en, this message translates to:
  /// **'Reconnect'**
  String get shellReconnect;

  /// Simple Mode Settings › Devices row button
  ///
  /// In en, this message translates to:
  /// **'Connect'**
  String get shellConnect;

  /// Simple Mode Settings › Rig: message when a changed value failed to save
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t save that'**
  String get shellCouldntSave;

  /// Simple Mode Settings › Rig: message when sending this device's time to the rig failed
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t set the rig\'s clock'**
  String get shellCouldntSetClock;

  /// Simple Mode Settings › Rig: message when no location could be found
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t get a location'**
  String get shellCouldntGetLocation;

  /// Simple Mode Settings › Rig: message when saving the location failed
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t save the site'**
  String get shellCouldntSaveSite;

  /// Simple Mode Settings › Rig: message after the location was set; source names where it came from (e.g. this iPad's GPS)
  ///
  /// In en, this message translates to:
  /// **'Site from {source}'**
  String shellSiteFrom(String source);

  /// Simple Mode Settings › Rig: button beside the rig name or the profile
  ///
  /// In en, this message translates to:
  /// **'Change'**
  String get shellChange;

  /// Simple Mode Settings › Rig: the rig's name
  ///
  /// In en, this message translates to:
  /// **'Name'**
  String get shellName;

  /// Simple Mode Settings › Rig: screen reader label of the Change button beside the rig name
  ///
  /// In en, this message translates to:
  /// **'Change rig'**
  String get shellChangeRig;

  /// Simple Mode Settings › Rig: the active profile
  ///
  /// In en, this message translates to:
  /// **'Profile'**
  String get shellProfile;

  /// Simple Mode Settings › Rig: screen reader label of the Change button beside the profile
  ///
  /// In en, this message translates to:
  /// **'Change profile'**
  String get shellChangeProfile;

  /// Simple Mode Settings › Rig: note under the profile while a plan runs
  ///
  /// In en, this message translates to:
  /// **'A plan is running. It keeps running on the rig; pick this profile again to come back to it.'**
  String get shellPlanRunning;

  /// Simple Mode Settings › Rig: the telescope's aperture
  ///
  /// In en, this message translates to:
  /// **'Main scope aperture'**
  String get shellAperture;

  /// Simple Mode Settings › Rig: the telescope's focal length
  ///
  /// In en, this message translates to:
  /// **'Main scope focal length'**
  String get shellFocalLength;

  /// Simple Mode Settings › Rig: the site's latitude
  ///
  /// In en, this message translates to:
  /// **'Latitude'**
  String get shellLatitude;

  /// Simple Mode Settings › Rig: the site's longitude
  ///
  /// In en, this message translates to:
  /// **'Longitude'**
  String get shellLongitude;

  /// Simple Mode Settings › Rig: the site's elevation
  ///
  /// In en, this message translates to:
  /// **'Elevation'**
  String get shellElevation;

  /// Simple Mode Settings › Rig button; device names this device (e.g. this iPad)
  ///
  /// In en, this message translates to:
  /// **'Use {device}\'s location and time'**
  String shellUseLocation(String device);

  /// Simple Mode Settings › Rig switch: the mount stays on a permanent pier
  ///
  /// In en, this message translates to:
  /// **'Permanent pier'**
  String get shellPermanentPier;

  /// Simple Mode Settings › Rig: hint under the Permanent pier switch
  ///
  /// In en, this message translates to:
  /// **'Skips polar alignment'**
  String get shellPermanentPierHint;

  /// Simple Mode Settings › Rig switch: turn Simple Mode off to go to the full app
  ///
  /// In en, this message translates to:
  /// **'Simple Mode'**
  String get shellSimpleMode;

  /// Simple Mode Settings › Rig versions list: this app's version
  ///
  /// In en, this message translates to:
  /// **'This app'**
  String get shellThisApp;

  /// Simple Mode Settings › Rig versions list: the rig's server (OpenAstro Ara is the product name)
  ///
  /// In en, this message translates to:
  /// **'OpenAstro Ara server'**
  String get shellAraServer;

  /// Simple Mode Settings › Rig versions list: the plate solving star databases
  ///
  /// In en, this message translates to:
  /// **'Star database'**
  String get shellStarDatabase;

  /// Simple Mode Settings › Rig versions list: the rig's operating system and board
  ///
  /// In en, this message translates to:
  /// **'System'**
  String get shellSystem;

  /// Simple Mode Settings › Rig button: open the logs and send a bug report
  ///
  /// In en, this message translates to:
  /// **'Logs & bug report'**
  String get shellLogs;

  /// Simple Mode Settings › Rig: an aperture or focal length not set yet
  ///
  /// In en, this message translates to:
  /// **'Not set'**
  String get shellNotSet;

  /// Simple Mode Settings › Rig: latitude hemisphere, north (one letter)
  ///
  /// In en, this message translates to:
  /// **'N'**
  String get shellHemiNorth;

  /// Simple Mode Settings › Rig: latitude hemisphere, south (one letter)
  ///
  /// In en, this message translates to:
  /// **'S'**
  String get shellHemiSouth;

  /// Simple Mode Settings › Rig: longitude hemisphere, east (one letter)
  ///
  /// In en, this message translates to:
  /// **'E'**
  String get shellHemiEast;

  /// Simple Mode Settings › Rig: longitude hemisphere, west (one letter)
  ///
  /// In en, this message translates to:
  /// **'W'**
  String get shellHemiWest;

  /// Simple Mode Settings › Rig: screen reader label of one digit wheel; label is the value's name
  ///
  /// In en, this message translates to:
  /// **'{label}, wheel {index} of {count}'**
  String shellWheelSemantics(String label, int index, int count);

  /// Simple Mode Settings › Rig: screen reader label of the + button (elevation)
  ///
  /// In en, this message translates to:
  /// **'Raise'**
  String get shellRaise;

  /// Simple Mode Settings › Rig: screen reader label of the − button (elevation)
  ///
  /// In en, this message translates to:
  /// **'Lower'**
  String get shellLower;

  /// Plan sheet, under the picked target: it never rises at this site
  ///
  /// In en, this message translates to:
  /// **'Never rises here'**
  String get planNeverRises;

  /// Plan sheet, under the picked target: it is below the horizon, no rise time known
  ///
  /// In en, this message translates to:
  /// **'Below your horizon'**
  String get planBelowHorizon;

  /// Plan sheet, under the picked target: below the horizon, with its rise time (HH:MM)
  ///
  /// In en, this message translates to:
  /// **'Below your horizon · rises {time}'**
  String planBelowHorizonRises(String time);

  /// Plan sheet, under the picked target: altitude now and highest tonight, in degrees
  ///
  /// In en, this message translates to:
  /// **'{alt}° up · max {max}°'**
  String planAltitudeUpMax(int alt, int max);

  /// Tonight's best suggestion line: altitude now in degrees
  ///
  /// In en, this message translates to:
  /// **'{alt}° up'**
  String planAltitudeUp(int alt);

  /// Tonight's best suggestion line, lowercase: below the horizon
  ///
  /// In en, this message translates to:
  /// **'below the horizon'**
  String get planBelowTheHorizon;

  /// Lowercase: when a target rises (HH:MM); suggestion line and plan square
  ///
  /// In en, this message translates to:
  /// **'rises {time}'**
  String planRisesAt(String time);

  /// Shutter screen-reader label while the plan waits for the target to rise (HH:MM)
  ///
  /// In en, this message translates to:
  /// **'Rises {time}'**
  String planRisesAtCap(String time);

  /// Plan square: the run is pausing after the current frame
  ///
  /// In en, this message translates to:
  /// **'Pausing…'**
  String get planPausing;

  /// Plan square: the run is paused
  ///
  /// In en, this message translates to:
  /// **'Paused'**
  String get planPaused;

  /// Plan square: the target or run is finished
  ///
  /// In en, this message translates to:
  /// **'Done'**
  String get planDone;

  /// Screen-reader label of the +N plan square: targets not shown
  ///
  /// In en, this message translates to:
  /// **'{count, plural, other{{count} more in the queue}}'**
  String planMoreInQueue(int count);

  /// Plan square for the hidden rest of the queue, lowercase
  ///
  /// In en, this message translates to:
  /// **'queued'**
  String get planQueued;

  /// Screen-reader label of a plan square, with the target name
  ///
  /// In en, this message translates to:
  /// **'Plan: {name}'**
  String planSquareLabel(String name);

  /// Shutter screen-reader label: nothing to start yet
  ///
  /// In en, this message translates to:
  /// **'Pick a target first'**
  String get planPickTargetFirst;

  /// Shutter screen-reader label: start the run
  ///
  /// In en, this message translates to:
  /// **'Start imaging'**
  String get planStartImaging;

  /// Shutter screen-reader label while imaging
  ///
  /// In en, this message translates to:
  /// **'Pause after this frame'**
  String get planPauseAfterFrame;

  /// Shutter screen-reader label while pausing
  ///
  /// In en, this message translates to:
  /// **'Pausing'**
  String get planPausingLabel;

  /// Shutter screen-reader label while paused
  ///
  /// In en, this message translates to:
  /// **'Resume'**
  String get planResume;

  /// Shutter screen-reader custom action: stop the run
  ///
  /// In en, this message translates to:
  /// **'Stop the run'**
  String get planStopTheRun;

  /// Hint beside the shutter: hold it to stop the run
  ///
  /// In en, this message translates to:
  /// **'Hold to stop'**
  String get planHoldToStop;

  /// Plan sheet caption above tonight's suggested targets (uppercase)
  ///
  /// In en, this message translates to:
  /// **'TONIGHT\'S BEST'**
  String get planTonightsBest;

  /// Plan sheet heading
  ///
  /// In en, this message translates to:
  /// **'Tonight\'s plan'**
  String get planTonightsPlan;

  /// Plan sheet heading when editing a queued target
  ///
  /// In en, this message translates to:
  /// **'Edit {name}'**
  String planEditTarget(String name);

  /// Plan sheet search field hint
  ///
  /// In en, this message translates to:
  /// **'Search objects'**
  String get planSearchObjects;

  /// Tooltip of the plan sheet's search button
  ///
  /// In en, this message translates to:
  /// **'Search'**
  String get planSearch;

  /// Plan sheet hint when no target is picked
  ///
  /// In en, this message translates to:
  /// **'Search, or pick in the sky.'**
  String get planSearchOrPick;

  /// Plan sheet button: pick the target in the planetarium
  ///
  /// In en, this message translates to:
  /// **'Pick in Sky'**
  String get planPickInSky;

  /// Plan sheet row tile label: sub-exposure length
  ///
  /// In en, this message translates to:
  /// **'Exposure'**
  String get planExposure;

  /// Exposure option marked as the suggested length; exposure is e.g. 2 min
  ///
  /// In en, this message translates to:
  /// **'{exposure} · suggested'**
  String planExposureSuggested(String exposure);

  /// Plan sheet row tile label: frame count; Night done row label for an unfiltered row
  ///
  /// In en, this message translates to:
  /// **'Frames'**
  String get planFrames;

  /// Plan sheet line with the suggested sub lengths, e.g. L 2 min · GB 3 min
  ///
  /// In en, this message translates to:
  /// **'Suggested: {hint}'**
  String planSuggested(String hint);

  /// Plan sheet button: apply all suggested sub lengths
  ///
  /// In en, this message translates to:
  /// **'Use all'**
  String get planUseAll;

  /// Plan sheet: total frames in the plan; progress panel frame count
  ///
  /// In en, this message translates to:
  /// **'{count, plural, other{{count} frames}}'**
  String planFrameCount(int count);

  /// Plan sheet warning: polar alignment not done tonight
  ///
  /// In en, this message translates to:
  /// **'Not polar aligned tonight'**
  String get planNotPolarAligned;

  /// Plan sheet: the run starts right away
  ///
  /// In en, this message translates to:
  /// **'Starts now'**
  String get planStartsNow;

  /// Plan sheet: the run waits for dark (HH:MM)
  ///
  /// In en, this message translates to:
  /// **'Starts at dark ({time})'**
  String planStartsAtDark(String time);

  /// Plan sheet: the run starts at a picked time (HH:MM)
  ///
  /// In en, this message translates to:
  /// **'Starts at {time}'**
  String planStartsAt(String time);

  /// Plan sheet button: don't wait, start right away
  ///
  /// In en, this message translates to:
  /// **'Start now instead'**
  String get planStartNowInstead;

  /// Plan sheet button: wait for dark to start (HH:MM)
  ///
  /// In en, this message translates to:
  /// **'Wait for dark ({time})'**
  String planWaitForDark(String time);

  /// Plan sheet button: choose a start time
  ///
  /// In en, this message translates to:
  /// **'Pick a time'**
  String get planPickATime;

  /// Plan sheet button: leave the edit without saving
  ///
  /// In en, this message translates to:
  /// **'Cancel'**
  String get planCancel;

  /// Plan sheet save button while saving
  ///
  /// In en, this message translates to:
  /// **'Saving…'**
  String get planSaving;

  /// Plan sheet button: save the edited target
  ///
  /// In en, this message translates to:
  /// **'Save'**
  String get planSave;

  /// Plan sheet confirmation after adding a target to the queue
  ///
  /// In en, this message translates to:
  /// **'{name} added · {count} in the queue'**
  String planTargetAdded(String name, int count);

  /// Plan sheet button: queue the picked target
  ///
  /// In en, this message translates to:
  /// **'Add to queue'**
  String get planAddToQueue;

  /// Plan sheet start button while starting
  ///
  /// In en, this message translates to:
  /// **'Starting…'**
  String get planStarting;

  /// Plan sheet start button with a scheduled start (HH:MM)
  ///
  /// In en, this message translates to:
  /// **'Start at {time}'**
  String planStartAt(String time);

  /// Plan sheet start button when a queue exists: start this target now
  ///
  /// In en, this message translates to:
  /// **'Start now'**
  String get planStartNow;

  /// Plan sheet start button: create the plan and start it
  ///
  /// In en, this message translates to:
  /// **'Create and start'**
  String get planCreateAndStart;

  /// Message after a target is removed from the queue
  ///
  /// In en, this message translates to:
  /// **'{name} removed'**
  String planTargetRemoved(String name);

  /// Message action: put the removed target back
  ///
  /// In en, this message translates to:
  /// **'Undo'**
  String get planUndo;

  /// Screen-reader label of a queue row's drag handle
  ///
  /// In en, this message translates to:
  /// **'Move {name}'**
  String planMoveTarget(String name);

  /// Tooltip of a queue row's remove button
  ///
  /// In en, this message translates to:
  /// **'Remove {name} from the queue'**
  String planRemoveFromQueue(String name);

  /// Shown behind a queue row swiped to remove it
  ///
  /// In en, this message translates to:
  /// **'Remove'**
  String get planRemove;

  /// Queue caption (uppercase): target count and total time, e.g. 6 h 20 min
  ///
  /// In en, this message translates to:
  /// **'QUEUE · {count} {count, plural, =1{target} other{targets}} · {duration}'**
  String planQueueHeader(int count, String duration);

  /// Button under a run error: open storage settings
  ///
  /// In en, this message translates to:
  /// **'Open Storage'**
  String get planOpenStorage;

  /// Night done heading after the run ends
  ///
  /// In en, this message translates to:
  /// **'Night done'**
  String get planNightDone;

  /// Night done summary, lowercase: the run was stopped
  ///
  /// In en, this message translates to:
  /// **'stopped'**
  String get planStopped;

  /// Night done summary, lowercase: the run failed
  ///
  /// In en, this message translates to:
  /// **'failed'**
  String get planFailed;

  /// Night done: the step where the run stopped
  ///
  /// In en, this message translates to:
  /// **'Stopped at: {step}'**
  String planStoppedAt(String step);

  /// Night done button: open the image library
  ///
  /// In en, this message translates to:
  /// **'Image Library'**
  String get planImageLibrary;

  /// Night done button: start a new plan
  ///
  /// In en, this message translates to:
  /// **'New plan'**
  String get planNewPlan;

  /// Progress panel caption (uppercase) with the target name
  ///
  /// In en, this message translates to:
  /// **'IMAGING {name}'**
  String planImagingTarget(String name);

  /// Progress panel: a panic stop paused the run
  ///
  /// In en, this message translates to:
  /// **'Mount stopped'**
  String get planMountStopped;

  /// Progress panel: when the queue ends (HH:MM) and the time left, e.g. 1 h 5 min
  ///
  /// In en, this message translates to:
  /// **'Ends {time} · {duration} left'**
  String planEndsLeft(String time, String duration);

  /// Progress panel hint while paused
  ///
  /// In en, this message translates to:
  /// **'Tap the shutter to resume and re-center; hold it to stop.'**
  String get planHintResume;

  /// Progress panel button while paused
  ///
  /// In en, this message translates to:
  /// **'Resume without re-centering'**
  String get planResumeWithoutRecenter;

  /// Progress panel button while paused: resume after re-centering and refocusing
  ///
  /// In en, this message translates to:
  /// **'Re-center and refocus'**
  String get planRecenterAndRefocus;

  /// Progress panel hint while imaging
  ///
  /// In en, this message translates to:
  /// **'Tap the shutter to pause after this frame; hold it to stop.'**
  String get planHintPause;

  /// Progress panel button: queue another target
  ///
  /// In en, this message translates to:
  /// **'Add target'**
  String get planAddTarget;

  /// Search suggestion: the object's magnitude, e.g. mag 8.4
  ///
  /// In en, this message translates to:
  /// **'mag {value}'**
  String planMagnitude(String value);

  /// Settings › Rig: the app language row (this device only)
  ///
  /// In en, this message translates to:
  /// **'Language'**
  String get shellLanguage;

  /// Settings › Rig language picker: follow the device's language
  ///
  /// In en, this message translates to:
  /// **'System'**
  String get shellLanguageSystem;

  /// Emergency Stop button (Simple Mode rail and full UI status bar): the compact square's label at rest; keep it short (60 px)
  ///
  /// In en, this message translates to:
  /// **'STOP'**
  String get estopCompactStop;

  /// Emergency Stop button (Simple Mode rail and full UI status bar): the compact square's label while held
  ///
  /// In en, this message translates to:
  /// **'HOLD'**
  String get estopCompactHold;

  /// Emergency Stop button (Simple Mode rail and full UI status bar): the compact square's label after one keyboard/screen-reader press; two short lines (60 px)
  ///
  /// In en, this message translates to:
  /// **'PRESS\nAGAIN'**
  String get estopCompactPressAgain;

  /// Emergency Stop button (Simple Mode rail and full UI status bar): label while the stop request is in flight
  ///
  /// In en, this message translates to:
  /// **'Stopping…'**
  String get estopStopping;

  /// Emergency Stop button (Simple Mode rail and full UI status bar): label while the press is held
  ///
  /// In en, this message translates to:
  /// **'Hold to confirm'**
  String get estopHoldToConfirm;

  /// Emergency Stop button (Simple Mode rail and full UI status bar): label after one keyboard/screen-reader press
  ///
  /// In en, this message translates to:
  /// **'Press again'**
  String get estopPressAgain;

  /// Emergency Stop button (Simple Mode rail and full UI status bar): label at rest
  ///
  /// In en, this message translates to:
  /// **'Emergency Stop'**
  String get estopLabel;

  /// Emergency Stop button (Simple Mode rail and full UI status bar): screen-reader announcement after the first press
  ///
  /// In en, this message translates to:
  /// **'Press again within 3 seconds to stop everything'**
  String get estopPressAgainAnnouncement;

  /// Emergency Stop confirm dialog (keyboard/screen reader, full UI): title
  ///
  /// In en, this message translates to:
  /// **'Stop everything now?'**
  String get estopConfirmTitle;

  /// Emergency Stop confirm dialog: what the stop does
  ///
  /// In en, this message translates to:
  /// **'This aborts the running sequence and the in-flight exposure, stops guiding, parks the mount, and switches the flat panel light and the camera cooler off. It cannot be undone — the sequence does not resume.'**
  String get estopConfirmBody;

  /// Emergency Stop confirm dialog: cancel button
  ///
  /// In en, this message translates to:
  /// **'Cancel'**
  String get estopCancel;

  /// Emergency Stop confirm dialog: the confirm button
  ///
  /// In en, this message translates to:
  /// **'EMERGENCY STOP'**
  String get estopConfirm;

  /// Emergency Stop message when no rig is connected
  ///
  /// In en, this message translates to:
  /// **'No server connected — nothing to stop.'**
  String get estopNoServer;

  /// Emergency Stop message when the request itself failed; error is a full sentence saying why
  ///
  /// In en, this message translates to:
  /// **'EMERGENCY STOP FAILED — {error} Check your rig manually.'**
  String estopFailed(String error);

  /// Emergency Stop: error message tail after "Couldn't " when the rig can't be reached; lower-case, no period
  ///
  /// In en, this message translates to:
  /// **'reach the rig'**
  String get estopActionReach;

  /// Emergency Stop result when a stop was already in progress
  ///
  /// In en, this message translates to:
  /// **'An emergency stop is already running — no second volley sent.'**
  String get estopAlreadyRunning;

  /// Emergency Stop result, one item of the comma-separated list: running sequences aborted
  ///
  /// In en, this message translates to:
  /// **'{count, plural, other{{count} sequence run(s) aborted}}'**
  String estopRunsAborted(int count);

  /// Emergency Stop result list item
  ///
  /// In en, this message translates to:
  /// **'no sequence was running'**
  String get estopNoSequence;

  /// Emergency Stop result list item
  ///
  /// In en, this message translates to:
  /// **'exposure aborted'**
  String get estopExposureAborted;

  /// Emergency Stop result list item
  ///
  /// In en, this message translates to:
  /// **'guiding stopped'**
  String get estopGuidingStopped;

  /// Emergency Stop result list item
  ///
  /// In en, this message translates to:
  /// **'mount told to park'**
  String get estopParkRequested;

  /// Emergency Stop result list item
  ///
  /// In en, this message translates to:
  /// **'flat panel light off'**
  String get estopFlatPanelOff;

  /// Emergency Stop result list item
  ///
  /// In en, this message translates to:
  /// **'camera cooler off'**
  String get estopCoolerOff;

  /// Emergency Stop result; parts is the comma-separated list of what each step did
  ///
  /// In en, this message translates to:
  /// **'Emergency stop executed: {parts}.'**
  String estopSummary(String parts);

  /// Emergency Stop result when a step failed; failures is the semicolon-separated list
  ///
  /// In en, this message translates to:
  /// **'CHECK THE RIG: {failures}.'**
  String estopCheckRig(String failures);

  /// Emergency Stop failed step
  ///
  /// In en, this message translates to:
  /// **'SEQUENCE ABORT FAILED (the run may still be executing)'**
  String get estopFailAbortRuns;

  /// Emergency Stop failed step
  ///
  /// In en, this message translates to:
  /// **'EXPOSURE ABORT FAILED'**
  String get estopFailAbortExposure;

  /// Emergency Stop failed step
  ///
  /// In en, this message translates to:
  /// **'GUIDING MAY STILL BE ACTIVE (stop failed)'**
  String get estopFailStopGuiding;

  /// Emergency Stop failed step
  ///
  /// In en, this message translates to:
  /// **'MOUNT PARK FAILED — verify it manually'**
  String get estopFailPark;

  /// Emergency Stop failed step
  ///
  /// In en, this message translates to:
  /// **'FLAT PANEL LIGHT-OFF FAILED'**
  String get estopFailFlatPanel;

  /// Emergency Stop failed step
  ///
  /// In en, this message translates to:
  /// **'CAMERA COOLER STILL ON (switching it off failed)'**
  String get estopFailCooler;

  /// Emergency Stop failed step the app has no wording for; step is the daemon's token
  ///
  /// In en, this message translates to:
  /// **'{step} FAILED'**
  String estopFailOther(String step);

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): headline after a run
  ///
  /// In en, this message translates to:
  /// **'In focus'**
  String get focusCardInFocus;

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): headline after a run, with the final HFR
  ///
  /// In en, this message translates to:
  /// **'In focus · HFR {hfr}'**
  String focusCardInFocusHfr(String hfr);

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): headline after a failed run
  ///
  /// In en, this message translates to:
  /// **'Autofocus failed'**
  String get focusCardFailed;

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): headline/phase after a cancelled run
  ///
  /// In en, this message translates to:
  /// **'Canceled'**
  String get focusCardCancelled;

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): headline before any run
  ///
  /// In en, this message translates to:
  /// **'Not focused yet'**
  String get focusCardNotFocused;

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): failure banner when the daemon gave no reason
  ///
  /// In en, this message translates to:
  /// **'Autofocus failed — see Support → Logs'**
  String get focusCardFailedSeeLogs;

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): failure banner tail; position is the focuser step
  ///
  /// In en, this message translates to:
  /// **'The focuser is back at {position}.'**
  String focusCardBackAt(String position);

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): phase while Smart Focus runs
  ///
  /// In en, this message translates to:
  /// **'Smart Focus — shot {shot} of {total}'**
  String focusCardSmart(String shot, String total);

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): phase
  ///
  /// In en, this message translates to:
  /// **'Finding rough focus…'**
  String get focusCardCoarse;

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): phase during the V-curve sweep
  ///
  /// In en, this message translates to:
  /// **'Sweeping — probe {done} of {total}'**
  String focusCardSweeping(String done, String total);

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): after the sweep phase when it is not the first sweep
  ///
  /// In en, this message translates to:
  /// **'(pass {pass})'**
  String focusCardPass(String pass);

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): phase
  ///
  /// In en, this message translates to:
  /// **'Fitting the curve…'**
  String get focusCardFitting;

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): phase
  ///
  /// In en, this message translates to:
  /// **'Moving to best focus…'**
  String get focusCardMoving;

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): phase
  ///
  /// In en, this message translates to:
  /// **'Confirming focus…'**
  String get focusCardConfirming;

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): phase
  ///
  /// In en, this message translates to:
  /// **'Measuring focuser backlash…'**
  String get focusCardBacklash;

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): phase the app has no wording for
  ///
  /// In en, this message translates to:
  /// **'Autofocus running…'**
  String get focusCardRunning;

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): phase line after a run; position is the focuser step
  ///
  /// In en, this message translates to:
  /// **'In focus at {position}'**
  String focusCardInFocusAt(String position);

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): after the In focus phase line; duration like 2m 00s
  ///
  /// In en, this message translates to:
  /// **'in {duration}'**
  String focusCardTook(String duration);

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): phase line after a failed run; reason is the daemon's text
  ///
  /// In en, this message translates to:
  /// **'Failed: {reason}'**
  String focusCardFailedReason(String reason);

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): failed phase line when the daemon gave no reason
  ///
  /// In en, this message translates to:
  /// **'see Support → Logs'**
  String get focusCardSeeLogs;

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): after a failed/cancelled phase line, following a dash
  ///
  /// In en, this message translates to:
  /// **'focuser restored to {position}'**
  String focusCardRestoredPosition(String position);

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): phase line before any run
  ///
  /// In en, this message translates to:
  /// **'No autofocus yet this session.'**
  String get focusCardNoRunYet;

  /// Filter picker (Simple Mode camera window and full UI): field label
  ///
  /// In en, this message translates to:
  /// **'Filter'**
  String get filterDropdownLabel;

  /// Filter picker: message when the wheel never moved
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t change the filter - the wheel didn\'t start moving.'**
  String get filterDropdownDidntMove;

  /// Filter picker: the picked name matches no slot on the wheel
  ///
  /// In en, this message translates to:
  /// **'Filter \'{name}\' isn\'t a slot on the connected wheel.'**
  String filterDropdownNotASlot(String name);

  /// Filter picker: the rig is busy with another action
  ///
  /// In en, this message translates to:
  /// **'Another action is still in progress.'**
  String get filterDropdownBusy;

  /// Filter picker: error message tail after "Couldn't "; lower-case, no period
  ///
  /// In en, this message translates to:
  /// **'change the filter'**
  String get filterDropdownActionChange;

  /// Polar align (Simple Mode panel and full UI): an axis within tolerance
  ///
  /// In en, this message translates to:
  /// **'On target'**
  String get polarHintOnTarget;

  /// Polar align (Simple Mode panel and full UI): altitude bolt direction
  ///
  /// In en, this message translates to:
  /// **'Lower'**
  String get polarHintLower;

  /// Polar align (Simple Mode panel and full UI): altitude bolt direction
  ///
  /// In en, this message translates to:
  /// **'Raise'**
  String get polarHintRaise;

  /// Polar align (Simple Mode panel and full UI): azimuth bolt direction
  ///
  /// In en, this message translates to:
  /// **'Move west'**
  String get polarHintMoveWest;

  /// Polar align (Simple Mode panel and full UI): azimuth bolt direction
  ///
  /// In en, this message translates to:
  /// **'Move east'**
  String get polarHintMoveEast;

  /// Polar align (Simple Mode panel and full UI) full UI: warning after an altitude bolt turn made the error worse
  ///
  /// In en, this message translates to:
  /// **'Altitude got worse after that turn — the altitude bolt moves the axis the other way on this mount. Turn it back past where you started.'**
  String get polarHintAltitudeWorse;

  /// Polar align (Simple Mode panel and full UI) full UI: warning after an azimuth bolt turn made the error worse
  ///
  /// In en, this message translates to:
  /// **'Azimuth got worse after that turn — the azimuth bolt moves the axis the other way on this mount. Turn it back past where you started.'**
  String get polarHintAzimuthWorse;

  /// Guider state (Simple Mode guider window, guiding strip, full UI guider page and Run tab)
  ///
  /// In en, this message translates to:
  /// **'Not guiding'**
  String get guiderStateNotGuiding;

  /// Guider state (Simple Mode guider window, guiding strip, full UI guider page and Run tab)
  ///
  /// In en, this message translates to:
  /// **'Looping'**
  String get guiderStateLooping;

  /// Guider state (Simple Mode guider window, guiding strip, full UI guider page and Run tab)
  ///
  /// In en, this message translates to:
  /// **'Star selected'**
  String get guiderStateStarSelected;

  /// Guider state (Simple Mode guider window, guiding strip, full UI guider page and Run tab)
  ///
  /// In en, this message translates to:
  /// **'Calibrating'**
  String get guiderStateCalibrating;

  /// Guider state (Simple Mode guider window, guiding strip, full UI guider page and Run tab)
  ///
  /// In en, this message translates to:
  /// **'Guiding'**
  String get guiderStateGuiding;

  /// Guider state (Simple Mode guider window, guiding strip, full UI guider page and Run tab)
  ///
  /// In en, this message translates to:
  /// **'Settling'**
  String get guiderStateSettling;

  /// Guider state (Simple Mode guider window, guiding strip, full UI guider page and Run tab)
  ///
  /// In en, this message translates to:
  /// **'Paused'**
  String get guiderStatePaused;

  /// Guider state (Simple Mode guider window, guiding strip, full UI guider page and Run tab)
  ///
  /// In en, this message translates to:
  /// **'Star lost'**
  String get guiderStateStarLost;

  /// Guider state (Simple Mode guider window, guiding strip, full UI guider page and Run tab)
  ///
  /// In en, this message translates to:
  /// **'Dithering'**
  String get guiderStateDithering;

  /// Guider state (Simple Mode guider window, guiding strip, full UI guider page and Run tab)
  ///
  /// In en, this message translates to:
  /// **'Guiding Assistant'**
  String get guiderStateAssistant;

  /// Guider state (Simple Mode guider window, guiding strip, full UI guider page and Run tab)
  ///
  /// In en, this message translates to:
  /// **'Unknown'**
  String get guiderStateUnknown;

  /// Guider state (Simple Mode guider window, guiding strip, full UI guider page and Run tab)
  ///
  /// In en, this message translates to:
  /// **'Not connected'**
  String get guiderStateNotConnected;

  /// Guider state (Simple Mode guider window, guiding strip, full UI guider page and Run tab): the calibration leg; direction is the guider's own word (North, West)
  ///
  /// In en, this message translates to:
  /// **'{direction} step {step}'**
  String guiderStateCalibrationStep(String direction, String step);

  /// Error message lead (every screen); action is a lower-case phrase like "save that"
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t {action}'**
  String errorCouldnt(String action);

  /// Error message: the action when the screen names none, after "Couldn't "
  ///
  /// In en, this message translates to:
  /// **'do that'**
  String get errorActionDefault;

  /// Error message (every screen), after "Couldn't <action> — "; lower-case start; device names this device (e.g. this iPhone or iPad)
  ///
  /// In en, this message translates to:
  /// **'your rig didn\'t answer. Check it\'s powered on and on the same network as {device}.'**
  String errorNoAnswer(String device);

  /// Error message (every screen), after "Couldn't <action> — "; lower-case start
  ///
  /// In en, this message translates to:
  /// **'your rig took too long to answer. It may be busy; try again in a moment.'**
  String get errorTooSlow;

  /// Error message (every screen), after "Couldn't <action> — "; lower-case start
  ///
  /// In en, this message translates to:
  /// **'the connection wasn\'t trusted.'**
  String get errorUntrusted;

  /// Error message (every screen), after "Couldn't <action> — "; lower-case start
  ///
  /// In en, this message translates to:
  /// **'the request was canceled.'**
  String get errorCancelled;

  /// Error message (every screen), after "Couldn't <action> — "; lower-case start
  ///
  /// In en, this message translates to:
  /// **'your rig doesn\'t have that. It may be running an older version of Ara.'**
  String get errorNotFound;

  /// Error message (every screen), after "Couldn't <action> — "; lower-case start
  ///
  /// In en, this message translates to:
  /// **'something else is using it right now.'**
  String get errorBusy;

  /// Error message (every screen), after "Couldn't <action> — "; lower-case start
  ///
  /// In en, this message translates to:
  /// **'your rig refused those values.'**
  String get errorRefusedValues;

  /// Error message (every screen), after "Couldn't <action> — "; lower-case start
  ///
  /// In en, this message translates to:
  /// **'your rig hit an error. Support → Logs has the details.'**
  String get errorServer;

  /// Error message (every screen), after "Couldn't <action> — "; lower-case start
  ///
  /// In en, this message translates to:
  /// **'your rig refused the request.'**
  String get errorRefused;

  /// Error message (every screen), after "Couldn't <action> — "; lower-case start
  ///
  /// In en, this message translates to:
  /// **'the connection failed. Check your rig is reachable.'**
  String get errorConnectionFailed;

  /// Error message (every screen), after "Couldn't <action> — "; lower-case start; followed by a period or by ": <reason>."
  ///
  /// In en, this message translates to:
  /// **'a file problem got in the way'**
  String get errorFile;

  /// Error message (every screen), after "Couldn't <action> — "; lower-case start
  ///
  /// In en, this message translates to:
  /// **'your rig sent something Ara didn\'t understand. It may be running a different version.'**
  String get errorUnreadable;

  /// A finished autofocus whose curve fit was weak; r2 is the fit's R² (e.g. 0.55)
  ///
  /// In en, this message translates to:
  /// **'The curve fit was weak (R² {r2}) — passing cloud, a slew during the sweep, or probes that straddle the V unevenly. The focus here comes from the measured frame, not the fit; run again if the sky has settled.'**
  String focusCardWeakFit(String r2);

  /// Weak autofocus fit whose outer probes sit on a plateau, manual step size
  ///
  /// In en, this message translates to:
  /// **'The curve fit was weak (R² {r2}): the outer probes sit on a plateau, so the sweep is wider than the V. A smaller step size would put more probes on the slope. The focus here comes from the measured frame, not the fit.'**
  String focusCardWeakFitPlateau(String r2);

  /// Weak autofocus fit on a plateau, automatic step size
  ///
  /// In en, this message translates to:
  /// **'The curve fit was weak (R² {r2}): the outer probes sit on a plateau, so this sweep was wider than the V. With automatic step size on, Ara sizes the next sweep from the V\'s width where a run lets it be measured, so running again should put more probes on the slope. The focus here comes from the measured frame, not the fit.'**
  String focusCardWeakFitPlateauAuto(String r2);

  /// Simple Focus panel stat: the focuser position autofocus settled on
  ///
  /// In en, this message translates to:
  /// **'Best position'**
  String get focusBestPosition;

  /// Simple Focus panel stat: stars measured in the focused frame
  ///
  /// In en, this message translates to:
  /// **'Stars'**
  String get focusStars;

  /// Simple Focus panel stat: how well the V-curve fit the probes (0-1)
  ///
  /// In en, this message translates to:
  /// **'Fit R²'**
  String get focusFitR2;

  /// Simple Focus panel: the focuser's position, first part of the line under the headline
  ///
  /// In en, this message translates to:
  /// **'Focuser at {position}'**
  String focusFocuserAt(String position);

  /// Simple Focus panel row: show the focus run's frame with its measured stars circled, full screen
  ///
  /// In en, this message translates to:
  /// **'Measured stars'**
  String get focusMeasuredStars;

  /// Bahtinov focusing finished in focus, mask taken off
  ///
  /// In en, this message translates to:
  /// **'In focus · mask off'**
  String get bahtinovInFocusMaskOff;

  /// Bahtinov focusing finished (not confirmed in focus), mask off
  ///
  /// In en, this message translates to:
  /// **'Mask off'**
  String get bahtinovMaskOff;

  /// Bahtinov readout stopped by a camera error
  ///
  /// In en, this message translates to:
  /// **'Stopped by a camera error'**
  String get bahtinovCameraError;

  /// Bahtinov readout running, no frame yet
  ///
  /// In en, this message translates to:
  /// **'Waiting for the first frame'**
  String get bahtinovWaitingFirst;

  /// Bahtinov headline in focus; px is the spike offset
  ///
  /// In en, this message translates to:
  /// **'In focus · {px} px'**
  String bahtinovInFocusPx(String px);

  /// Bahtinov headline: the turn advice and the offset
  ///
  /// In en, this message translates to:
  /// **'{hint} · {px} px from focus'**
  String bahtinovHintPx(String hint, String px);

  /// Bahtinov readout stopped; the last offset
  ///
  /// In en, this message translates to:
  /// **'Stopped · last {px} px from focus'**
  String bahtinovStoppedLast(String px);

  /// Bahtinov before starting
  ///
  /// In en, this message translates to:
  /// **'Fit the mask, then Start'**
  String get bahtinovFitMask;

  /// Simple Focus Manual, before starting: what to do
  ///
  /// In en, this message translates to:
  /// **'Put the Bahtinov mask over the front of the telescope and point at a bright star. Ara uses the brightest star in the frame.'**
  String get bahtinovFitMaskDetail;

  /// Bahtinov turn advice: no frame yet
  ///
  /// In en, this message translates to:
  /// **'Waiting'**
  String get bahtinovWaiting;

  /// Bahtinov turn advice detail
  ///
  /// In en, this message translates to:
  /// **'The first frame is on its way.'**
  String get bahtinovWaitingDetail;

  /// Bahtinov: no star found
  ///
  /// In en, this message translates to:
  /// **'No star'**
  String get bahtinovNoStar;

  /// Bahtinov: no star found, what to do
  ///
  /// In en, this message translates to:
  /// **'Point at a bright star, or lengthen the exposure.'**
  String get bahtinovNoStarDetail;

  /// Bahtinov: the star is too close to the frame edge
  ///
  /// In en, this message translates to:
  /// **'Star at the edge'**
  String get bahtinovNearEdge;

  /// Bahtinov: star at the edge, what to do
  ///
  /// In en, this message translates to:
  /// **'Center the star so its spikes fit in the frame.'**
  String get bahtinovNearEdgeDetail;

  /// Bahtinov: no diffraction spikes found
  ///
  /// In en, this message translates to:
  /// **'No spikes'**
  String get bahtinovNoSpikes;

  /// Bahtinov: no spikes, what to do
  ///
  /// In en, this message translates to:
  /// **'Fit the mask over the front of the telescope. Far out of focus? Turn until the star shrinks and spikes appear.'**
  String get bahtinovNoSpikesDetail;

  /// Bahtinov turn advice: in focus
  ///
  /// In en, this message translates to:
  /// **'In focus'**
  String get bahtinovInFocus;

  /// Bahtinov in focus with the optics' zone in µm
  ///
  /// In en, this message translates to:
  /// **'The middle spike is centered, inside the ±{um} µm focus zone. Lock the focuser, then Finish.'**
  String bahtinovInFocusZoneDetail(String um);

  /// Bahtinov in focus
  ///
  /// In en, this message translates to:
  /// **'The middle spike is centered. Lock the focuser, then Finish.'**
  String get bahtinovInFocusDetail;

  /// Bahtinov turn advice: no move seen yet
  ///
  /// In en, this message translates to:
  /// **'Make a move'**
  String get bahtinovMakeMove;

  /// Bahtinov: first move
  ///
  /// In en, this message translates to:
  /// **'Turn the focuser a little either way, then watch the number.'**
  String get bahtinovMakeMoveDetail;

  /// Bahtinov turn advice: turn back
  ///
  /// In en, this message translates to:
  /// **'Go back'**
  String get bahtinovGoBack;

  /// Bahtinov: passed focus; size is how far (e.g. 'half', 'a third of')
  ///
  /// In en, this message translates to:
  /// **'You passed focus. Turn back about {size} your last move.'**
  String bahtinovPassedDetail(String size);

  /// Bahtinov turn advice: same way
  ///
  /// In en, this message translates to:
  /// **'Keep going'**
  String get bahtinovKeepGoing;

  /// Bahtinov: keep going; size is how far
  ///
  /// In en, this message translates to:
  /// **'Same way — about {size} your last move again.'**
  String bahtinovKeepGoingDetail(String size);

  /// Bahtinov: turned the wrong way
  ///
  /// In en, this message translates to:
  /// **'Wrong way — turn back past where you started.'**
  String get bahtinovWrongWayDetail;

  /// Move size word placed in 'about {size} your last move'
  ///
  /// In en, this message translates to:
  /// **'a quarter of'**
  String get bahtinovMoveQuarter;

  /// Move size word
  ///
  /// In en, this message translates to:
  /// **'a third of'**
  String get bahtinovMoveThird;

  /// Move size word
  ///
  /// In en, this message translates to:
  /// **'half'**
  String get bahtinovMoveHalf;

  /// Move size word
  ///
  /// In en, this message translates to:
  /// **'three quarters of'**
  String get bahtinovMoveThreeQuarters;

  /// Move size word
  ///
  /// In en, this message translates to:
  /// **'the same as'**
  String get bahtinovMoveSame;

  /// Move size word
  ///
  /// In en, this message translates to:
  /// **'1½×'**
  String get bahtinovMoveOneAndHalf;

  /// Move size word
  ///
  /// In en, this message translates to:
  /// **'twice'**
  String get bahtinovMoveTwice;

  /// Move size word
  ///
  /// In en, this message translates to:
  /// **'several times'**
  String get bahtinovMoveSeveral;

  /// Simple Focus Manual: unit under the big offset number
  ///
  /// In en, this message translates to:
  /// **'px from focus'**
  String get bahtinovPxFromFocus;

  /// Simple Focus Manual: finish focusing (then asks to take the mask off)
  ///
  /// In en, this message translates to:
  /// **'Finish'**
  String get bahtinovFinish;

  /// Simple Focus Manual: the mask-off question, inside the panel
  ///
  /// In en, this message translates to:
  /// **'Take the Bahtinov mask off'**
  String get bahtinovMaskQuestion;

  /// Simple Focus Manual: why the mask must come off
  ///
  /// In en, this message translates to:
  /// **'Take the mask off the front of the telescope before you image. Left on, it puts spikes on every star and costs light.'**
  String get bahtinovMaskQuestionDetail;

  /// Mask-off question: keep the mask on for now
  ///
  /// In en, this message translates to:
  /// **'Not yet'**
  String get bahtinovNotYet;

  /// Mask-off question: the mask is off
  ///
  /// In en, this message translates to:
  /// **'Mask removed'**
  String get bahtinovMaskRemoved;

  /// Simple Focus Manual: Bahtinov frame exposure
  ///
  /// In en, this message translates to:
  /// **'Exposure'**
  String get bahtinovExposure;

  /// Screen reader label of Focus's shutter on Manual
  ///
  /// In en, this message translates to:
  /// **'Start the Bahtinov readout'**
  String get focusStartBahtinov;

  /// Screen reader label of Focus's shutter on Manual while it reads
  ///
  /// In en, this message translates to:
  /// **'Stop the Bahtinov readout'**
  String get focusStopBahtinov;

  /// Simple Focus panel: a finished autofocus whose curve fit was weak, short form
  ///
  /// In en, this message translates to:
  /// **'Weak curve fit (R² {r2}): the focus comes from the measured frame. Run again if the sky has settled.'**
  String focusWeakFitShort(String r2);

  /// Guide camera live focus: starting
  ///
  /// In en, this message translates to:
  /// **'Starting…'**
  String get guideFocusStarting;

  /// Guide camera live focus headline: no stars
  ///
  /// In en, this message translates to:
  /// **'Live · no stars measured'**
  String get guideFocusNoStarsMeasured;

  /// Guide camera live focus headline
  ///
  /// In en, this message translates to:
  /// **'Live · HFR {hfr}'**
  String guideFocusLive(String hfr);

  /// Guide camera live focus headline, in focus
  ///
  /// In en, this message translates to:
  /// **'Live · HFR {hfr} — in focus'**
  String guideFocusLiveInFocus(String hfr);

  /// Guide camera live focus headline, the best so far
  ///
  /// In en, this message translates to:
  /// **'Live · HFR {hfr} — best so far'**
  String guideFocusLiveBest(String hfr);

  /// Guide camera live focus stopped on an error
  ///
  /// In en, this message translates to:
  /// **'Stopped on an error'**
  String get guideFocusStoppedError;

  /// Guide focus stopped on its own once held in focus
  ///
  /// In en, this message translates to:
  /// **'In focus — held, stopped'**
  String get guideFocusHeld;

  /// Same, with the best HFR
  ///
  /// In en, this message translates to:
  /// **'In focus — held, stopped · best HFR {hfr}'**
  String guideFocusHeldBest(String hfr);

  /// Guide focus stopped, in focus
  ///
  /// In en, this message translates to:
  /// **'In focus · best HFR {hfr}'**
  String guideFocusInFocusBest(String hfr);

  /// Guide focus stopped
  ///
  /// In en, this message translates to:
  /// **'Stopped'**
  String get guideFocusStopped;

  /// Guide focus stopped with the best HFR
  ///
  /// In en, this message translates to:
  /// **'Stopped · best HFR {hfr}'**
  String guideFocusStoppedBest(String hfr);

  /// Off-axis guider: the main telescope must be focused first
  ///
  /// In en, this message translates to:
  /// **'Waiting for the main telescope'**
  String get guideFocusWaitingMain;

  /// Guide focus can't start yet
  ///
  /// In en, this message translates to:
  /// **'Not ready'**
  String get guideFocusNotReady;

  /// Guide focus can start
  ///
  /// In en, this message translates to:
  /// **'Ready to focus'**
  String get guideFocusReady;

  /// Guide focus blocker
  ///
  /// In en, this message translates to:
  /// **'Connect the guider first — the frames come through it.'**
  String get guideFocusConnectGuider;

  /// Guide focus blocker
  ///
  /// In en, this message translates to:
  /// **'Stop the guider first; it owns its camera while it is looping, guiding, calibrating or paused.'**
  String get guideFocusStopGuider;

  /// Guide focus advice: no stars
  ///
  /// In en, this message translates to:
  /// **'No stars'**
  String get guideFocusNoStars;

  /// Guide focus advice detail: no stars
  ///
  /// In en, this message translates to:
  /// **'Point at a star field or lengthen the exposure. Far out of focus? Make big moves until stars appear.'**
  String get guideFocusNoStarsDetail;

  /// Move size inside the guide focus advice
  ///
  /// In en, this message translates to:
  /// **'big moves'**
  String get guideFocusBigMoves;

  /// Move size inside the guide focus advice
  ///
  /// In en, this message translates to:
  /// **'small moves'**
  String get guideFocusSmallMoves;

  /// Guide focus in focus
  ///
  /// In en, this message translates to:
  /// **'Stars are as tight as this camera resolves (in focus ≤ {target} px). Lock the focuser.'**
  String guideFocusInFocusDetail(String target);

  /// Guide focus advice: at the best HFR seen
  ///
  /// In en, this message translates to:
  /// **'Sharpest so far'**
  String get guideFocusSharpest;

  /// Guide focus advice detail
  ///
  /// In en, this message translates to:
  /// **'Hold here, or nudge a touch either way to confirm.'**
  String get guideFocusSharpestDetail;

  /// Guide focus first move; size is 'small moves'/'big moves'
  ///
  /// In en, this message translates to:
  /// **'In or out, {size} — then watch which way the number goes.'**
  String guideFocusMakeMoveDetail(String size);

  /// Guide focus keep going
  ///
  /// In en, this message translates to:
  /// **'Same way you just moved — {size}.'**
  String guideFocusKeepGoingDetail(String size);

  /// Guide focus go back
  ///
  /// In en, this message translates to:
  /// **'The other way from your last move — {size}.'**
  String guideFocusGoBackDetail(String size);

  /// Guide focus advice: no change seen
  ///
  /// In en, this message translates to:
  /// **'Holding'**
  String get guideFocusHolding;

  /// Guide focus holding, far
  ///
  /// In en, this message translates to:
  /// **'Still well off focus — make a bigger move and watch the number.'**
  String get guideFocusHoldingFar;

  /// Guide focus holding, near
  ///
  /// In en, this message translates to:
  /// **'Try a small move either way.'**
  String get guideFocusHoldingNear;

  /// Focus mode rail chip: focusing the guide camera
  ///
  /// In en, this message translates to:
  /// **'Guide'**
  String get focusGuide;

  /// Focus panel, guide camera: what to turn (guide scope)
  ///
  /// In en, this message translates to:
  /// **'Guide scope: turn its focuser, or slide the camera in the draw tube, in small moves and watch the number fall.'**
  String get focusGuideScope;

  /// Focus panel, guide camera: what to turn (off-axis guider)
  ///
  /// In en, this message translates to:
  /// **'Off-axis guider: it shares the main scope\'s focus. Turn the OAG\'s helical focuser in small moves and watch the number fall.'**
  String get focusGuideOag;

  /// Focus panel, guide camera, off-axis guider gate
  ///
  /// In en, this message translates to:
  /// **'Focus the main telescope first: an off-axis guider sees the same focal plane, so its HFR only means something once the main scope is in focus.'**
  String get focusGuideMainFirst;

  /// Focus panel: the main telescope was focused by hand
  ///
  /// In en, this message translates to:
  /// **'Already in focus'**
  String get focusAlreadyInFocus;

  /// Focus panel, guide camera: frames taken
  ///
  /// In en, this message translates to:
  /// **'Frames'**
  String get focusFrames;

  /// Screen reader label of Focus's shutter, guide camera
  ///
  /// In en, this message translates to:
  /// **'Start the guide camera\'s live focus'**
  String get focusStartGuide;

  /// Screen reader label of Focus's shutter, guide camera, while it reads
  ///
  /// In en, this message translates to:
  /// **'Stop the guide camera\'s live focus'**
  String get focusStopGuide;

  /// Focus panel: focus the main telescope (short, shares a line with Auto / Manual)
  ///
  /// In en, this message translates to:
  /// **'Main'**
  String get focusScopeMain;

  /// Focus panel: focus the guide camera (short)
  ///
  /// In en, this message translates to:
  /// **'Guide'**
  String get focusScopeGuide;

  /// Guided Run plan step: green button, on to focusing
  ///
  /// In en, this message translates to:
  /// **'Next: Focus'**
  String get guidedNextFocus;

  /// Guided Run plan step: green button, on to polar alignment
  ///
  /// In en, this message translates to:
  /// **'Next: Polar align'**
  String get guidedNextPolarAlign;

  /// Guided Run plan step: green button when focus and polar alignment are done already
  ///
  /// In en, this message translates to:
  /// **'Start imaging'**
  String get guidedStartImaging;

  /// Guided Run plan step: line above the buttons
  ///
  /// In en, this message translates to:
  /// **'Imaging starts after focus and polar alignment.'**
  String get guidedImagingStartsAfter;

  /// Guided Run: the last setup step's green Done, which starts imaging
  ///
  /// In en, this message translates to:
  /// **'Done · Start imaging'**
  String get guidedDoneStartImaging;

  /// Settings › Rig: the guide scope's focal length (mm), read from the guider when unset
  ///
  /// In en, this message translates to:
  /// **'Guide scope focal length'**
  String get shellGuideFocalLength;

  /// Settings › Rig: the guide camera's pixel size (µm), read from AlpacaBridge through the guider
  ///
  /// In en, this message translates to:
  /// **'Guide camera pixel size'**
  String get shellGuidePixelSize;

  /// Simple search: other words that find the guide scope rows (not shown)
  ///
  /// In en, this message translates to:
  /// **'guide scope guider camera pixel focal polar'**
  String get searchWordsGuideScope;
}

class _AppLocalizationsDelegate
    extends LocalizationsDelegate<AppLocalizations> {
  const _AppLocalizationsDelegate();

  @override
  Future<AppLocalizations> load(Locale locale) {
    return SynchronousFuture<AppLocalizations>(lookupAppLocalizations(locale));
  }

  @override
  bool isSupported(Locale locale) =>
      <String>['en', 'pt'].contains(locale.languageCode);

  @override
  bool shouldReload(_AppLocalizationsDelegate old) => false;
}

AppLocalizations lookupAppLocalizations(Locale locale) {
  // Lookup logic when language+country codes are specified.
  switch (locale.languageCode) {
    case 'pt':
      {
        switch (locale.countryCode) {
          case 'BR':
            return AppLocalizationsPtBr();
        }
        break;
      }
  }

  // Lookup logic when only language code is specified.
  switch (locale.languageCode) {
    case 'en':
      return AppLocalizationsEn();
    case 'pt':
      return AppLocalizationsPt();
  }

  throw FlutterError(
    'AppLocalizations.delegate failed to load unsupported locale "$locale". This is likely '
    'an issue with the localizations generation tool. Please file an issue '
    'on GitHub with a reproducible sample app and the gen-l10n configuration '
    'that was used.',
  );
}
