package org.openastro.openastroara

import android.content.Context
import android.net.wifi.WifiManager
import io.flutter.embedding.android.FlutterActivity

class MainActivity : FlutterActivity() {
    // Many Android devices filter inbound multicast unless an app holds a
    // MulticastLock, so mDNS discovery of the rig heard nothing (#1129). Held
    // only while the app is in the foreground; CHANGE_WIFI_MULTICAST_STATE is
    // already declared in the manifest.
    private var multicastLock: WifiManager.MulticastLock? = null

    override fun onResume() {
        super.onResume()
        val wifi = applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager ?: return
        multicastLock = (multicastLock ?: wifi.createMulticastLock("openastroara-mdns").apply {
            setReferenceCounted(false)
        }).also { if (!it.isHeld) it.acquire() }
    }

    override fun onPause() {
        multicastLock?.let { if (it.isHeld) it.release() }
        super.onPause()
    }
}
