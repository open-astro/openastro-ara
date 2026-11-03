// ignore: unused_import
import 'package:intl/intl.dart' as intl;

import 'app_localizations.dart';

// ignore_for_file: type=lint

/// The translations for Portuguese (`pt`).
class AppLocalizationsPt extends AppLocalizations {
  AppLocalizationsPt([String locale = 'pt']) : super(locale);

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
  String get deviceCamera => 'Câmera';

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
  String get deviceCurrentTemperature => 'Temperatura atual';

  @override
  String get deviceCooler => 'Refrigeração';

  @override
  String get deviceNoCooler => 'Sem refrigeração';

  @override
  String get deviceTarget => 'Alvo';

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
  String get deviceCouldntMoveFocuser => 'Não foi possível mover o focalizador';

  @override
  String get deviceCouldntSaveThat => 'Não foi possível salvar';

  @override
  String get deviceNever => 'Nunca';

  @override
  String get deviceRefocusTemp => 'Refocar por temp.';

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
  String get previewSettings => 'Ajustes da prévia';

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
  String get polarLoop => 'Contínuo';

  @override
  String get polarSingle => 'Único';

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
  String get polarActionCapture => 'fazer um quadro';

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
  String polarNoSolve(int count) {
    return 'Sem plate solve ($count seguidos). Verifique o céu.';
  }

  @override
  String get polarExposure => 'Exposição';

  @override
  String get polarTakeFrame => 'Fazer quadro';

  @override
  String get polarDone => 'Concluir';

  @override
  String get polarDoneNextPlan => 'Concluir · Próximo: Plano';

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
  String get searchSectionRig => 'Setup';

  @override
  String get searchSectionDevices => 'Dispositivos';

  @override
  String get searchSectionCamera => 'Câmera';

  @override
  String get searchSectionMount => 'Montagem';

  @override
  String get searchSectionFocuser => 'Focalizador';

  @override
  String get searchSectionGuider => 'Guia';

  @override
  String get searchSettingAperture => 'Abertura';

  @override
  String get searchSettingFocalLength => 'Distância focal';

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
  String get autopilotStepFocus => 'Foco';

  @override
  String get autopilotStepPolarAlign => 'Alinh. polar';

  @override
  String get autopilotStepPlan => 'Plano';

  @override
  String autopilotStepDone(String step) {
    return '$step, concluído';
  }

  @override
  String autopilotStepSkipped(String step) {
    return '$step, pulado';
  }

  @override
  String autopilotStepCurrent(String step) {
    return '$step, etapa atual';
  }

  @override
  String autopilotStepNext(String step) {
    return '$step, próximo';
  }

  @override
  String get autopilotBack => 'Voltar';

  @override
  String get autopilotSkip => 'Pular';

  @override
  String get autopilotNothingDone => 'Nada feito ainda esta noite';

  @override
  String get autopilotStart => 'Iniciar Piloto automático';

  @override
  String get autopilotNotNow => 'Agora não';

  @override
  String get shellModeAutopilot => 'Piloto automático';

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
  String get shellRailAutopilot => 'Auto';

  @override
  String shellStopAgain(String shortcut) {
    return 'Pressione $shortcut de novo para parar a sessão';
  }

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
  String get shellSectionCamera => 'Câmera';

  @override
  String get shellSectionMount => 'Montagem';

  @override
  String get shellSectionFocuser => 'Focalizador';

  @override
  String get shellSectionGuider => 'Guia';

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
  String get shellAperture => 'Abertura';

  @override
  String get shellFocalLength => 'Distância focal';

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
  String get shellAraServer => 'Servidor OpenAstro Ara';

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
  String get planTonightsPlan => 'Plano da noite';

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
  String get shellLanguage => 'Idioma';

  @override
  String get shellLanguageSystem => 'Sistema';
}
