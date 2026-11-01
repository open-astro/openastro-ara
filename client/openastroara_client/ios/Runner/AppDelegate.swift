import Flutter
import UIKit

@main
@objc class AppDelegate: FlutterAppDelegate, FlutterImplicitEngineDelegate {
  private var displayChannel: FlutterMethodChannel?

  override func application(
    _ application: UIApplication,
    didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]?
  ) -> Bool {
    return super.application(application, didFinishLaunchingWithOptions: launchOptions)
  }

  func didInitializeImplicitFlutterEngine(_ engineBridge: FlutterImplicitEngineBridge) {
    GeneratedPluginRegistrant.register(with: engineBridge.pluginRegistry)
    guard let registrar = engineBridge.pluginRegistry.registrar(forPlugin: "OpenAstroAraDisplay") else { return }
    // NotchSideProbe: which way the window is turned, so a landscape iPhone
    // keeps its margin only on the Dynamic Island side.
    let channel = FlutterMethodChannel(name: "openastroara/display", binaryMessenger: registrar.messenger())
    channel.setMethodCallHandler { call, result in
      guard call.method == "interfaceOrientation" else {
        result(FlutterMethodNotImplemented)
        return
      }
      let scene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
      switch scene?.effectiveGeometry.interfaceOrientation {
      case .landscapeLeft: result("landscapeLeft")
      case .landscapeRight: result("landscapeRight")
      case .portrait, .portraitUpsideDown: result("portrait")
      default: result(nil)
      }
    }
    displayChannel = channel
  }
}
