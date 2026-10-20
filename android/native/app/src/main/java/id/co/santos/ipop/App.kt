package id.co.santos.ipop

import android.app.Application
import android.content.Context
import android.content.SharedPreferences
import android.net.Uri
import android.util.Log
import org.json.JSONArray
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL

class App : Application() {
    override fun onCreate() {
        Receipts.processStartElapsed = android.os.SystemClock.elapsedRealtime()
        super.onCreate()
        Channels.ensure(this)
        Receipts.flushInBackground(this)
        // FirebaseInitProvider is removed from the manifest; a push that wakes the process needs
        // Firebase up before PushService runs, so initialise from the last config we fetched.
        Push.initFromCache(this)
    }

    companion object {
        /** FirstRunActivity is shown at most once per app start. */
        @Volatile var firstRunShownThisProcess = false
    }
}

object Ipop {
    const val TAG = "iPop"
    const val HOST = "ipop.santos.co.id"
    const val BASE = "https://$HOST"
    const val START = "$BASE/dashboard?source=android"

    fun prefs(c: Context): SharedPreferences = c.getSharedPreferences("ipop", Context.MODE_PRIVATE)

    fun isIpop(url: String?): Boolean {
        if (url.isNullOrBlank()) return false
        return try {
            val u = Uri.parse(url)
            u.scheme == "https" && u.host.equals(HOST, ignoreCase = true) && (u.port == -1 || u.port == 443)
        } catch (_: Exception) { false }
    }

    /** Relative path ("/chat/x") or absolute iPop URL -> absolute iPop URL; anything else -> null. */
    fun absolute(url: String?): String? {
        if (url.isNullOrBlank()) return null
        if (url.startsWith("/") && !url.startsWith("//")) return BASE + url
        return if (isIpop(url)) url else null
    }

    /** Path + query + fragment, for Blazor.navigateTo. */
    fun pathOf(abs: String): String {
        val u = Uri.parse(abs)
        val sb = StringBuilder(u.encodedPath ?: "/")
        u.encodedQuery?.let { sb.append('?').append(it) }
        u.encodedFragment?.let { sb.append('#').append(it) }
        return sb.toString()
    }

    fun jsString(s: String): String = JSONObject.quote(s)
}

/** Short-lived access token handed over by the web (window.iPopAndroid.setSession). App-private prefs. */
object Session {
    private fun p(c: Context) = c.getSharedPreferences("session", Context.MODE_PRIVATE)

    fun set(c: Context, token: String?, expiresAtMs: Long) {
        if (token.isNullOrBlank()) { clear(c); return }
        p(c).edit().putString("t", token).putLong("e", expiresAtMs).apply()
    }

    /** The stored token if it is still valid for at least 30 s, else null. */
    fun token(c: Context): String? {
        val pp = p(c)
        val t = pp.getString("t", null) ?: return null
        return if (pp.getLong("e", 0) > System.currentTimeMillis() + 30_000) t else null
    }

    fun clear(c: Context) { p(c).edit().clear().apply() }
}

/** Last 6 messages per chat notification tag, so MessagingStyle can show the conversation. */
object ChatStore {
    data class Msg(val sender: String, val text: String, val time: Long, val me: Boolean)

    private fun p(c: Context) = c.getSharedPreferences("chat_msgs", Context.MODE_PRIVATE)

    @Synchronized
    fun add(c: Context, tag: String, m: Msg) {
        val arr = try { JSONArray(p(c).getString(tag, "[]")) } catch (_: Exception) { JSONArray() }
        arr.put(JSONObject().put("s", m.sender).put("t", m.text).put("w", m.time).put("m", m.me))
        val out = JSONArray()
        for (i in maxOf(0, arr.length() - 6) until arr.length()) out.put(arr.get(i))
        p(c).edit().putString(tag, out.toString()).apply()
    }

    @Synchronized
    fun list(c: Context, tag: String): List<Msg> {
        val arr = try { JSONArray(p(c).getString(tag, "[]")) } catch (_: Exception) { JSONArray() }
        return (0 until arr.length()).map {
            val o = arr.getJSONObject(it)
            Msg(o.optString("s"), o.optString("t"), o.optLong("w"), o.optBoolean("m"))
        }
    }

    fun clear(c: Context, tag: String?) { if (tag != null) p(c).edit().remove(tag).apply() }
    fun clearAll(c: Context) { p(c).edit().clear().apply() }
}

/** Tiny HttpURLConnection helper. TLS uses the app's network security config (system CAs + SJA root). */
object Http {
    class Res(val code: Int, val body: String) { val ok get() = code in 200..299 }

    fun request(method: String, url: String, json: String? = null, bearer: String? = null, timeoutMs: Int = 15_000): Res {
        val c = URL(url).openConnection() as HttpURLConnection
        try {
            c.requestMethod = method
            c.connectTimeout = timeoutMs
            c.readTimeout = timeoutMs
            c.instanceFollowRedirects = false
            c.setRequestProperty("Accept", "application/json")
            c.setRequestProperty("User-Agent", "iPopAndroid/${BuildConfig.VERSION_NAME}")
            if (bearer != null) c.setRequestProperty("Authorization", "Bearer $bearer")
            if (json != null) {
                c.doOutput = true
                c.setRequestProperty("Content-Type", "application/json; charset=utf-8")
                c.outputStream.use { it.write(json.toByteArray(Charsets.UTF_8)) }
            }
            val code = c.responseCode
            val stream = if (code >= 400) c.errorStream else c.inputStream
            val body = stream?.bufferedReader()?.use { it.readText() } ?: ""
            return Res(code, body)
        } finally {
            c.disconnect()
        }
    }

    fun bg(name: String, block: () -> Unit) {
        Thread({
            try { block() } catch (t: Throwable) { Log.w(Ipop.TAG, "$name failed: ${t.javaClass.simpleName}: ${t.message}") }
        }, "ipop-$name").start()
    }
}
