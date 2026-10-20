package id.co.santos.ipop

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.net.http.SslCertificate
import android.net.http.SslError
import android.util.Log
import java.net.URL
import java.security.cert.X509Certificate
import javax.net.ssl.HttpsURLConnection

/**
 * DEBUG BUILDS ONLY (src/debug). Simulates an FCM data message without Firebase:
 *   adb shell am broadcast -a id.co.santos.ipop.debug.PUSH -p id.co.santos.ipop \
 *     --es kind chat --es title "RM Bagastyo" --es body "tes" --es tag chat:room1 --es roomId 00000000-0000-0000-0000-000000000001
 */
class DebugPushReceiver : BroadcastReceiver() {
    private val keys = listOf("kind", "title", "body", "url", "tag", "callId", "actionToken", "apiBase", "roomId", "sender", "channel", "urgent")

    override fun onReceive(c: Context, intent: Intent) {
        if (intent.getStringExtra("kind") == "ssltest") { sslSelfTest(c.applicationContext); return }
        val d = keys.mapNotNull { k -> intent.getStringExtra(k)?.let { k to it } }.toMap()
        if (d["kind"].isNullOrBlank()) return
        Notifier.handle(c.applicationContext, d)
    }

    /** Feeds the live server certificate through the same check onReceivedSslError uses. */
    private fun sslSelfTest(c: Context) = Http.bg("ssltest") {
        val conn = URL(Ipop.BASE + "/").openConnection() as HttpsURLConnection
        conn.connect()
        val leaf = conn.serverCertificates[0] as X509Certificate
        conn.disconnect()
        fun run(url: String, code: Int) = SjaTrust.shouldProceed(c, SslError(code, SslCertificate(leaf), url))
        Log.i(Ipop.TAG, "ssltest leaf=${leaf.subjectX500Principal}")
        Log.i(Ipop.TAG, "ssltest untrusted@ipop.santos.co.id -> proceed=${run(Ipop.BASE + "/", SslError.SSL_UNTRUSTED)}")
        Log.i(Ipop.TAG, "ssltest untrusted@evil.santos.co.id -> proceed=${run("https://evil.santos.co.id/", SslError.SSL_UNTRUSTED)}")
        Log.i(Ipop.TAG, "ssltest expired@ipop.santos.co.id -> proceed=${run(Ipop.BASE + "/", SslError.SSL_EXPIRED)}")
    }
}
