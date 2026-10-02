package org.openastro.openastroara

import android.content.Context
import android.net.wifi.WifiManager
import io.flutter.embedding.android.FlutterActivity
import io.flutter.embedding.engine.FlutterEngine
import io.flutter.plugin.common.MethodChannel

class MainActivity : FlutterActivity() {
    // Many Android devices filter inbound multicast unless an app holds a
    // MulticastLock, so mDNS discovery of the rig heard nothing (#1129). The
    // scan screen takes it while open (lib/services/multicast_lock.dart) and
    // gives it back on close; onPause drops it too so a backgrounded scan
    // screen doesn't keep Wi-Fi awake. CHANGE_WIFI_MULTICAST_STATE is declared
    // in the manifest.
    private var multicastLock: WifiManager.MulticastLock? = null
    private var wanted = false

    override fun configureFlutterEngine(flutterEngine: FlutterEngine) {
        super.configureFlutterEngine(flutterEngine)
        MethodChannel(flutterEngine.dartExecutor.binaryMessenger, "openastroara/multicast_lock")
            .setMethodCallHandler { call, result ->
                when (call.method) {
                    "acquire" -> { wanted = true; hold(); result.success(null) }
                    "release" -> { wanted = false; drop(); result.success(null) }
                    else -> result.notImplemented()
                }
            }
    }

    override fun onResume() {
        super.onResume()
        if (wanted) hold()
    }

    override fun onPause() {
        drop()
        super.onPause()
    }

    // Both fail silent, matching lib/services/multicast_lock.dart: without the
    // lock discovery falls back to the subnet sweep. A SecurityException (the
    // manifest permission missing) must not escape into the method channel.
    private fun hold() {
        try {
            val wifi = applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager ?: return
            val lock = multicastLock ?: wifi.createMulticastLock("openastroara-mdns").apply {
                setReferenceCounted(false)
            }.also { multicastLock = it }
            if (!lock.isHeld) lock.acquire()
        } catch (e: SecurityException) {
            multicastLock = null
        }
    }

    private fun drop() {
        try {
            multicastLock?.let { if (it.isHeld) it.release() }
        } catch (e: RuntimeException) {
            // Already released by the system (e.g. Wi-Fi turned off).
        }
    }
}
