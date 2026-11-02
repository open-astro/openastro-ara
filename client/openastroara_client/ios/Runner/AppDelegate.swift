import Flutter
import Network
import UIKit

@main
@objc class AppDelegate: FlutterAppDelegate, FlutterImplicitEngineDelegate {
  private var displayChannel: FlutterMethodChannel?
  private let bonjour = BonjourBrowser()

  override func application(
    _ application: UIApplication,
    didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]?
  ) -> Bool {
    return super.application(application, didFinishLaunchingWithOptions: launchOptions)
  }

  func didInitializeImplicitFlutterEngine(_ engineBridge: FlutterImplicitEngineBridge) {
    GeneratedPluginRegistrant.register(with: engineBridge.pluginRegistry)
    if let registrar = engineBridge.pluginRegistry.registrar(forPlugin: "OpenAstroAraDisplay") {
      // NotchSideProbe: which way the window is turned, so a landscape iPhone
      // keeps its margin only on the Dynamic Island side.
      let channel = FlutterMethodChannel(name: "openastroara/display", binaryMessenger: registrar.messenger())
      channel.setMethodCallHandler { call, result in
        guard call.method == "interfaceOrientation" else {
          result(FlutterMethodNotImplemented)
          return
        }
        let scene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        // effectiveGeometry is iOS 16+; the deployment target is 15.
        let orientation: UIInterfaceOrientation?
        if #available(iOS 16.0, *) {
          orientation = scene?.effectiveGeometry.interfaceOrientation
        } else {
          orientation = scene?.interfaceOrientation
        }
        switch orientation {
        case .landscapeLeft: result("landscapeLeft")
        case .landscapeRight: result("landscapeRight")
        case .portrait, .portraitUpsideDown: result("portrait")
        default: result(nil)
        }
      }
      displayChannel = channel
    }
    guard let registrar = engineBridge.pluginRegistry.registrar(forPlugin: "OpenAstroAraBonjour") else { return }
    let channel = FlutterMethodChannel(name: "openastroara/bonjour", binaryMessenger: registrar.messenger())
    channel.setMethodCallHandler { [bonjour] call, result in
      guard call.method == "browse",
            let args = call.arguments as? [String: Any],
            let type = args["type"] as? String else {
        result(FlutterMethodNotImplemented)
        return
      }
      let timeoutMs = args["timeoutMs"] as? Int ?? 2000
      bonjour.browse(type: type, timeout: Double(timeoutMs) / 1000, completion: result)
    }
  }
}

/// Server discovery on iOS (ServerDiscoveryService._bonjourDiscover): iOS
/// refuses raw multicast sockets without Apple's multicast entitlement, but
/// its own Bonjour API needs only NSBonjourServices + Local Network and sees
/// everything mDNSResponder sees, a rig on another VLAN included. Each service
/// found is resolved to a numeric IPv4 by opening a TCP connection to it, so
/// the saved rig never depends on a .local name.
final class BonjourBrowser {
  private let queue = DispatchQueue(label: "openastroara.bonjour")

  func browse(type: String, timeout: TimeInterval, completion: @escaping ([[String: Any]]) -> Void) {
    let browser = NWBrowser(for: .bonjour(type: type, domain: nil), using: .tcp)
    var found: [String: [String: Any]] = [:]
    var resolving: Set<String> = []
    var connections: [NWConnection] = []
    // Every handler runs on `queue`, so the state above needs no lock.
    browser.browseResultsChangedHandler = { [queue] results, _ in
      for result in results {
        guard case let .service(name, _, _, _) = result.endpoint, resolving.insert(name).inserted else { continue }
        let params = NWParameters.tcp
        if let ip = params.defaultProtocolStack.internetProtocol as? NWProtocolIP.Options {
          ip.version = .v4
        }
        let connection = NWConnection(to: result.endpoint, using: params)
        connections.append(connection)
        connection.stateUpdateHandler = { state in
          guard case .ready = state,
                case let .hostPort(host, port)? = connection.currentPath?.remoteEndpoint else { return }
          // An IPv4 literal prints bare; drop any "%interface" scope suffix.
          let address = "\(host)".split(separator: "%").first.map(String.init) ?? "\(host)"
          found[name] = ["name": name, "host": address, "port": Int(port.rawValue)]
          connection.cancel()
        }
        connection.start(queue: queue)
      }
    }
    browser.start(queue: queue)
    queue.asyncAfter(deadline: .now() + timeout) {
      browser.cancel()
      connections.forEach { $0.cancel() }
      let rigs = Array(found.values)
      DispatchQueue.main.async { completion(rigs) }
    }
  }
}
