package id.co.santos.ipop

import android.app.ActivityManager
import android.app.NotificationManager
import android.app.usage.UsageStatsManager
import android.content.Context
import android.os.Build
import android.os.PowerManager
import android.os.SystemClock
import android.util.Log
import org.json.JSONArray
import org.json.JSONObject

/**
 * Delivery receipts: what actually happened to each FCM message on this phone, reported to
 * POST /api/push/native/receipt without auth (the server-issued deliveryId is the capability).
 * Sent synchronously after the notification is shown, 5 s timeout; on failure queued (max 50)
 * and flushed on the next app start or push.
 */
object Receipts {
    private const val TAG = "iPopReceipt"
    private const val QUEUE = "receipt_queue"
    private const val MAX_QUEUE = 50
    private const val TIMEOUT_MS = 5_000

    /** Set in App.onCreate: lets a receipt say whether this push cold-started the process. */
    @Volatile var processStartElapsed: Long = SystemClock.elapsedRealtime()
    @Volatile private var firstPushSeen = false

    private fun prefs(c: Context) = c.getSharedPreferences("receipts", Context.MODE_PRIVATE)

    /** Environment that decides whether a notification can be seen, as one compact string. */
    fun environment(c: Context, channelId: String?): String {
        val nm = c.getSystemService(NotificationManager::class.java)
        val parts = mutableListOf<String>()
        if (channelId != null) {
            val ch = nm.getNotificationChannel(channelId)
            parts += "channel=$channelId"
            parts += "importance=${ch?.importance ?: "missing"}"
            if (ch != null) {
                parts += "sound=${ch.sound != null}"
                parts += "lockscreen=${ch.lockscreenVisibility}"
                if (Build.VERSION.SDK_INT >= 29) parts += "bubbles=${ch.canBubble()}"
                if (Build.VERSION.SDK_INT >= 30) parts += "conversation=${ch.conversationId ?: "-"}"
            }
        }
        parts += "notifEnabled=${nm.areNotificationsEnabled()}"
        parts += "postPerm=${Perms.granted(c, android.Manifest.permission.POST_NOTIFICATIONS) || Build.VERSION.SDK_INT < 33}"
        if (Build.VERSION.SDK_INT >= 34) parts += "fsi=${nm.canUseFullScreenIntent()}"
        parts += "dnd=${filterName(nm.currentInterruptionFilter)}"
        parts += "batteryUnrestricted=${c.getSystemService(PowerManager::class.java).isIgnoringBatteryOptimizations(c.packageName)}"
        if (Build.VERSION.SDK_INT >= 28) {
            parts += "bgRestricted=${c.getSystemService(ActivityManager::class.java).isBackgroundRestricted}"
            parts += "bucket=${bucketName(c.getSystemService(UsageStatsManager::class.java).appStandbyBucket)}"
        }
        parts += "interactive=${c.getSystemService(PowerManager::class.java).isInteractive}"
        return parts.joinToString(" ")
    }

    /** True for the first push handled since process start if the process is younger than 10 s. */
    fun coldStartFlag(): Boolean {
        val first = !firstPushSeen
        firstPushSeen = true
        return first && SystemClock.elapsedRealtime() - processStartElapsed < 10_000
    }

    private fun filterName(f: Int) = when (f) {
        NotificationManager.INTERRUPTION_FILTER_ALL -> "off"
        NotificationManager.INTERRUPTION_FILTER_PRIORITY -> "priority"
        NotificationManager.INTERRUPTION_FILTER_ALARMS -> "alarms"
        NotificationManager.INTERRUPTION_FILTER_NONE -> "none"
        else -> "unknown($f)"
    }

    private fun bucketName(b: Int) = when (b) {
        UsageStatsManager.STANDBY_BUCKET_ACTIVE -> "active"
        UsageStatsManager.STANDBY_BUCKET_WORKING_SET -> "working_set"
        UsageStatsManager.STANDBY_BUCKET_FREQUENT -> "frequent"
        UsageStatsManager.STANDBY_BUCKET_RARE -> "rare"
        45 -> "restricted"
        else -> "b$b"
    }

    private fun body(deliveryId: String, kind: String, outcome: String, detail: String) = JSONObject()
        .put("deliveryId", deliveryId)
        .put("kind", kind)
        .put("outcome", outcome)
        .put("detail", detail.take(1500))
        .put("appVersion", BuildConfig.VERSION_NAME)
        .put("model", "${Build.MANUFACTURER} ${Build.MODEL}".trim())
        .put("sdk", Build.VERSION.SDK_INT)

    /**
     * Report one receipt. Call from the FCM thread AFTER the notification is shown; blocks up to ~5 s.
     * Messages without a deliveryId (debug/fake pushes) are only logged.
     */
    fun report(c: Context, deliveryId: String?, kind: String, outcome: String, detail: String) {
        Log.i(TAG, "kind=$kind outcome=$outcome id=${deliveryId ?: "-"} $detail")
        if (deliveryId.isNullOrBlank()) return
        val json = body(deliveryId, kind, outcome, detail)
        if (!send(json)) enqueue(c, json)
        flush(c) // anything left from earlier attempts
    }

    private fun send(json: JSONObject): Boolean = try {
        val r = Http.request("POST", "${Ipop.BASE}/api/push/native/receipt", json.toString(), null, TIMEOUT_MS)
        Log.i(TAG, "POST receipt ${json.optString("deliveryId")} -> ${r.code}")
        // 2xx accepted; 4xx means the server will never take it (unknown/expired id): drop, don't retry.
        r.code in 200..499
    } catch (e: Exception) {
        Log.w(TAG, "POST receipt failed: ${e.javaClass.simpleName}: ${e.message}")
        false
    }

    @Synchronized private fun enqueue(c: Context, json: JSONObject) {
        val arr = try { JSONArray(prefs(c).getString(QUEUE, "[]")) } catch (_: Exception) { JSONArray() }
        arr.put(json)
        val out = JSONArray()
        for (i in maxOf(0, arr.length() - MAX_QUEUE) until arr.length()) out.put(arr.get(i))
        prefs(c).edit().putString(QUEUE, out.toString()).apply()
        Log.i(TAG, "queued (${out.length()} pending)")
    }

    /** Retry queued receipts (blocking; call from a background thread). */
    @Synchronized fun flush(c: Context) {
        val raw = prefs(c).getString(QUEUE, null) ?: return
        val arr = try { JSONArray(raw) } catch (_: Exception) { JSONArray() }
        if (arr.length() == 0) return
        val left = JSONArray()
        var failed = false
        for (i in 0 until arr.length()) {
            val o = arr.optJSONObject(i) ?: continue
            if (failed || !send(o)) { failed = true; left.put(o) }
        }
        prefs(c).edit().putString(QUEUE, left.toString()).apply()
    }

    fun flushInBackground(c: Context) = Http.bg("receipt-flush") { flush(c.applicationContext) }

    fun pending(c: Context): Int = try { JSONArray(prefs(c).getString(QUEUE, "[]")).length() } catch (_: Exception) { 0 }
}
