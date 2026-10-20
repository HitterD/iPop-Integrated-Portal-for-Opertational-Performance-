package id.co.santos.ipop

import android.app.NotificationManager
import android.webkit.JavascriptInterface

/**
 * window.iPopAndroid. Every method first checks that the top page is https://ipop.santos.co.id
 * (MainActivity.currentUrl, kept on the UI thread; WebView.getUrl() may not be called from here).
 */
class Bridge(private val a: MainActivity) {
    private fun allowed() = Ipop.isIpop(a.currentUrl)

    @JavascriptInterface fun getAppVersion(): String = if (allowed()) BuildConfig.VERSION_NAME else ""

    @JavascriptInterface fun getFcmToken(): String = if (allowed()) Push.token(a) else ""

    @JavascriptInterface fun setSession(accessToken: String?, expiresAtEpochMs: Double) {
        if (!allowed()) return
        Session.set(a, accessToken, expiresAtEpochMs.toLong())
    }

    @JavascriptInterface fun onSignedIn() {
        if (!allowed()) return
        a.runOnUiThread { a.withPageToken { bearer -> if (bearer != null) Push.register(a, bearer) } }
    }

    @JavascriptInterface fun onSignedOut() {
        if (!allowed()) return
        val bearer = Session.token(a)
        Push.unregister(a, bearer)
        Session.clear(a)
        ChatStore.clearAll(a)
        a.getSystemService(NotificationManager::class.java).cancelAll()
        OngoingCallService.stop(a)
    }

    @JavascriptInterface fun openSettings(kind: String?) {
        if (!allowed()) return
        a.runOnUiThread { Perms.openSettings(a, kind ?: "") }
    }

    @JavascriptInterface fun getPermissionState(): String = if (allowed()) Perms.stateJson(a) else "{}"

    /** Launcher badges on Android follow the app's notifications; nothing extra to do. */
    @JavascriptInterface fun setBadge(@Suppress("UNUSED_PARAMETER") n: Int) { }

    @JavascriptInterface fun callStarted(name: String?) {
        if (!allowed()) return
        a.runOnUiThread { OngoingCallService.start(a, name ?: "") }
    }

    @JavascriptInterface fun callEnded() {
        if (!allowed()) return
        a.runOnUiThread { OngoingCallService.stop(a) }
    }

    /**
     * Real end-to-end test: POST /api/push/test with the page's bearer token; the server queues a "test"
     * push to every device of this user, so this phone gets it back through FCM. The HTTP status is shown
     * as a toast and also dispatched as window 'ipop-native' {action:'test', status}.
     */
    @JavascriptInterface fun sendTestNotification() {
        if (!allowed()) return
        a.runOnUiThread {
            a.withPageToken { bearer ->
                if (bearer == null) { a.nativeResult("test", 401); return@withPageToken }
                Http.bg("push-test") {
                    val code = try { Http.request("POST", "${Ipop.BASE}/api/push/test", "{}", bearer).code } catch (_: Exception) { 0 }
                    android.util.Log.i(Ipop.TAG, "push test -> $code")
                    a.runOnUiThread { a.nativeResult("test", code) }
                }
            }
        }
    }

    /** Used by the download shim for blob:/data: links (see Downloads.JS_SHIM). */
    @JavascriptInterface fun saveBase64(name: String?, mime: String?, base64: String?) {
        if (!allowed() || base64.isNullOrEmpty()) return
        Downloads.saveBase64(a, name ?: "download", mime, base64)
    }
}
