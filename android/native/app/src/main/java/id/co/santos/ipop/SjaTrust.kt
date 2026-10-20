package id.co.santos.ipop

import android.content.Context
import android.net.Uri
import android.net.http.SslCertificate
import android.net.http.SslError
import android.os.Build
import android.util.Log
import java.security.KeyStore
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import javax.net.ssl.TrustManagerFactory
import javax.net.ssl.X509TrustManager

/**
 * WebView does not apply network_security_config to its own TLS, so on phones that do not trust
 * SJA-ROOT-CA it raises onReceivedSslError for ipop.santos.co.id. We proceed only when the server
 * certificate validates against the bundled SJA root (and nothing else) and names ipop.santos.co.id.
 */
object SjaTrust {
    @Volatile private var tm: X509TrustManager? = null

    fun trustManager(c: Context): X509TrustManager {
        tm?.let { return it }
        val cf = CertificateFactory.getInstance("X.509")
        val root = c.resources.openRawResource(R.raw.sja_root_ca).use { cf.generateCertificate(it) as X509Certificate }
        val ks = KeyStore.getInstance(KeyStore.getDefaultType()).apply {
            load(null, null)
            setCertificateEntry("sja-root", root)
        }
        val tmf = TrustManagerFactory.getInstance(TrustManagerFactory.getDefaultAlgorithm()).apply { init(ks) }
        return (tmf.trustManagers.first { it is X509TrustManager } as X509TrustManager).also { tm = it }
    }

    private fun leafOf(cert: SslCertificate?): X509Certificate? {
        if (cert == null) return null
        if (Build.VERSION.SDK_INT >= 29) cert.x509Certificate?.let { return it }
        val bytes = SslCertificate.saveState(cert).getByteArray("x509-certificate") ?: return null
        return CertificateFactory.getInstance("X.509").generateCertificate(bytes.inputStream()) as X509Certificate
    }

    fun shouldProceed(c: Context, error: SslError): Boolean {
        val host = try { Uri.parse(error.url).host } catch (_: Exception) { null }
        if (!host.equals(Ipop.HOST, ignoreCase = true)) return false
        // Only "untrusted issuer"; expired / wrong host / not yet valid are never overridden.
        if (error.hasError(SslError.SSL_EXPIRED) || error.hasError(SslError.SSL_IDMISMATCH) ||
            error.hasError(SslError.SSL_NOTYETVALID) || error.hasError(SslError.SSL_DATE_INVALID) ||
            error.hasError(SslError.SSL_INVALID)) return false
        return try {
            val leaf = leafOf(error.certificate) ?: return false
            leaf.checkValidity()
            val auth = if (leaf.publicKey.algorithm == "EC") "ECDHE_ECDSA" else "ECDHE_RSA"
            trustManager(c).checkServerTrusted(arrayOf(leaf), auth)
            matchesHost(leaf, Ipop.HOST)
        } catch (e: Exception) {
            Log.w(Ipop.TAG, "SJA trust rejected ${error.url}: ${e.javaClass.simpleName}")
            false
        }
    }

    private fun matchesHost(cert: X509Certificate, host: String): Boolean {
        val names = cert.subjectAlternativeNames ?: return false
        return names.any { entry ->
            val type = entry[0] as? Int
            val name = (entry[1] as? String)?.lowercase() ?: return@any false
            type == 2 && (name == host || (name.startsWith("*.") &&
                host.endsWith(name.substring(1)) && host.count { it == '.' } == name.count { it == '.' }))
        }
    }
}
