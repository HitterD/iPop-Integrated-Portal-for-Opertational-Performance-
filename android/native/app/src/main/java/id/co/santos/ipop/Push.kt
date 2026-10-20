package id.co.santos.ipop

import android.content.Context
import android.os.Build
import android.util.Log
import com.google.firebase.FirebaseApp
import com.google.firebase.FirebaseOptions
import com.google.firebase.messaging.FirebaseMessaging
import org.json.JSONObject

/**
 * FCM without google-services.json: client values come from GET /api/push/native/config and are cached.
 * Device registration needs the user's bearer token, supplied by the caller (from the page or Session).
 */
object Push {
    private const val CFG = "fcm_cfg"
    private const val TOKEN = "fcm_token"
    private const val PENDING = "fcm_pending_register"

    fun token(c: Context): String = Ipop.prefs(c).getString(TOKEN, "") ?: ""

    fun isReady(): Boolean {
        val c = appCtx ?: return false
        return try { FirebaseApp.getApps(c).isNotEmpty() } catch (_: Exception) { false }
    }

    @Volatile private var appCtx: Context? = null

    /** Called from App.onCreate: initialise Firebase from the last fetched config, if push was enabled. */
    fun initFromCache(c: Context) {
        appCtx = c.applicationContext
        val raw = Ipop.prefs(c).getString(CFG, null) ?: return
        try { init(c, JSONObject(raw)) } catch (e: Exception) { Log.w(Ipop.TAG, "fcm cached init: ${e.message}") }
    }

    private fun init(c: Context, cfg: JSONObject): Boolean {
        if (!cfg.optBoolean("enabled")) return false
        val appId = cfg.optString("applicationId"); val key = cfg.optString("apiKey")
        val project = cfg.optString("projectId"); val sender = cfg.optString("senderId")
        if (appId.isBlank() || key.isBlank() || project.isBlank() || sender.isBlank()) return false
        if (FirebaseApp.getApps(c).isEmpty()) {
            val opts = FirebaseOptions.Builder().setApplicationId(appId).setApiKey(key)
                .setProjectId(project).setGcmSenderId(sender).build()
            FirebaseApp.initializeApp(c.applicationContext, opts)
        }
        FirebaseMessaging.getInstance().isAutoInitEnabled = true
        return true
    }

    /** Fetch the config (background), initialise, then fetch the FCM token. */
    fun refreshConfig(c: Context, then: (() -> Unit)? = null) {
        val app = c.applicationContext
        Http.bg("fcm-config") {
            val r = Http.request("GET", "${Ipop.BASE}/api/push/native/config")
            Log.i(Ipop.TAG, "fcm config -> ${r.code}")
            if (!r.ok) return@bg
            val cfg = JSONObject(r.body)
            Log.i(Ipop.TAG, "fcm enabled=${cfg.optBoolean("enabled")}")
            Ipop.prefs(app).edit().putString(CFG, cfg.toString()).apply()
            if (!init(app, cfg)) return@bg
            FirebaseMessaging.getInstance().token.addOnSuccessListener { t ->
                if (!t.isNullOrBlank()) Ipop.prefs(app).edit().putString(TOKEN, t).apply()
                then?.invoke()
            }
        }
    }

    /** FirebaseMessagingService.onNewToken. Registers right away if we still hold a valid session token. */
    fun onNewToken(c: Context, t: String) {
        Ipop.prefs(c).edit().putString(TOKEN, t).putBoolean(PENDING, true).apply()
        Session.token(c)?.let { register(c, it) }
    }

    fun hasPendingRegistration(c: Context) = Ipop.prefs(c).getBoolean(PENDING, false)

    /** POST /api/push/native/devices with this phone's FCM token. Background. */
    fun register(c: Context, bearer: String) {
        val app = c.applicationContext
        val t = token(app)
        if (t.isBlank()) {
            // Token not fetched yet: fetch config + token, then come back.
            refreshConfig(app) { if (token(app).isNotBlank()) register(app, bearer) }
            return
        }
        Http.bg("fcm-register") {
            val body = JSONObject().put("token", t).put("platform", "android")
                .put("appVersion", BuildConfig.VERSION_NAME).put("deviceName", Build.MODEL ?: "")
            val r = Http.request("POST", "${Ipop.BASE}/api/push/native/devices", body.toString(), bearer)
            Log.i(Ipop.TAG, "fcm register -> ${r.code}")
            if (r.ok) Ipop.prefs(app).edit().putBoolean(PENDING, false).apply()
        }
    }

    /** POST /api/push/native/devices/remove, then drop the FCM token so this phone stops receiving. */
    fun unregister(c: Context, bearer: String?) {
        val app = c.applicationContext
        val t = token(app)
        Ipop.prefs(app).edit().remove(TOKEN).putBoolean(PENDING, false).apply()
        if (t.isBlank()) return
        Http.bg("fcm-unregister") {
            if (bearer != null) {
                val r = Http.request("POST", "${Ipop.BASE}/api/push/native/devices/remove",
                    JSONObject().put("token", t).toString(), bearer)
                Log.i(Ipop.TAG, "fcm unregister -> ${r.code}")
            }
            // Even if the remove call failed, a deleted token is dead: the server prunes it on the next send.
            if (isReady()) FirebaseMessaging.getInstance().deleteToken()
        }
    }
}
