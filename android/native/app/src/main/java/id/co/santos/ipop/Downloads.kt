package id.co.santos.ipop

import android.app.DownloadManager
import android.content.ContentValues
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.os.Handler
import android.os.Looper
import android.provider.MediaStore
import android.util.Base64
import android.webkit.CookieManager
import android.webkit.MimeTypeMap
import android.webkit.URLUtil
import android.widget.Toast
import java.io.InputStream
import java.net.HttpURLConnection
import java.net.URL

/**
 * Downloads from the WebView.
 *  - https on ipop.santos.co.id: fetched here with HttpURLConnection, because DownloadManager runs in
 *    the system download process and does NOT trust the bundled SJA root (network_security_config is per app).
 *  - other https hosts: DownloadManager with the cookie header.
 *  - blob: / data: (how the web saves authenticated files): handed over as base64 by the JS shim in MainActivity.
 * Files land in the public Download folder (MediaStore on 29+, DownloadManager-visible file on 26-28).
 */
object Downloads {
    private val main = Handler(Looper.getMainLooper())

    private fun toast(c: Context, msg: String) = main.post { Toast.makeText(c, msg, Toast.LENGTH_SHORT).show() }

    fun start(c: Context, url: String, userAgent: String?, contentDisposition: String?, mime: String?) {
        val name = URLUtil.guessFileName(url, contentDisposition, mime)
        when {
            url.startsWith("data:") -> Http.bg("dl-data") { saveDataUrl(c, url, name) }
            url.startsWith("blob:") -> Unit // handled by the JS shim (needs the page to read the blob)
            Ipop.isIpop(url) && Build.VERSION.SDK_INT >= 29 -> Http.bg("dl-ipop") { fetch(c, url, userAgent, contentDisposition, mime) }
            url.startsWith("https://") || url.startsWith("http://") -> viaDownloadManager(c, url, userAgent, name, mime)
        }
    }

    private fun viaDownloadManager(c: Context, url: String, ua: String?, name: String, mime: String?) {
        try {
            val req = DownloadManager.Request(Uri.parse(url)).apply {
                CookieManager.getInstance().getCookie(url)?.let { addRequestHeader("Cookie", it) }
                if (ua != null) addRequestHeader("User-Agent", ua)
                if (mime != null) setMimeType(mime)
                setTitle(name)
                setNotificationVisibility(DownloadManager.Request.VISIBILITY_VISIBLE_NOTIFY_COMPLETED)
                setDestinationInExternalPublicDir(Environment.DIRECTORY_DOWNLOADS, name)
            }
            c.getSystemService(DownloadManager::class.java).enqueue(req)
            toast(c, c.getString(R.string.dl_started, name))
        } catch (e: Exception) {
            toast(c, c.getString(R.string.dl_failed, name))
        }
    }

    private fun fetch(c: Context, url: String, ua: String?, cd: String?, mime: String?) {
        val conn = URL(url).openConnection() as HttpURLConnection
        var name = URLUtil.guessFileName(url, cd, mime)
        try {
            CookieManager.getInstance().getCookie(url)?.let { conn.setRequestProperty("Cookie", it) }
            if (ua != null) conn.setRequestProperty("User-Agent", ua)
            conn.connectTimeout = 15_000; conn.readTimeout = 60_000
            if (conn.responseCode !in 200..299) { toast(c, c.getString(R.string.dl_failed, name)); return }
            val realCd = conn.getHeaderField("Content-Disposition") ?: cd
            val realMime = conn.contentType?.substringBefore(';')?.trim() ?: mime
            name = URLUtil.guessFileName(url, realCd, realMime)
            toast(c, c.getString(R.string.dl_started, name))
            conn.inputStream.use { save(c, name, realMime, it) }
        } catch (e: Exception) {
            toast(c, c.getString(R.string.dl_failed, name))
        } finally { conn.disconnect() }
    }

    private fun saveDataUrl(c: Context, url: String, name: String) {
        val comma = url.indexOf(',')
        if (comma < 0) return
        val header = url.substring(5, comma)
        val mime = header.substringBefore(';').ifBlank { "application/octet-stream" }
        val bytes = if (header.endsWith(";base64")) Base64.decode(url.substring(comma + 1), Base64.DEFAULT)
            else Uri.decode(url.substring(comma + 1)).toByteArray()
        save(c, name, mime, bytes.inputStream())
    }

    /** From the JS shim: window.iPopAndroid.saveBase64(name, mime, base64). */
    fun saveBase64(c: Context, name: String, mime: String?, b64: String) {
        Http.bg("dl-blob") {
            val bytes = Base64.decode(b64, Base64.DEFAULT)
            save(c, sanitize(name), mime, bytes.inputStream())
        }
    }

    private fun sanitize(n: String): String = n.replace(Regex("[\\\\/:*?\"<>|\\u0000-\\u001f]"), "_").trim().ifBlank { "download" }.take(120)

    private fun save(c: Context, name: String, mimeIn: String?, input: InputStream) {
        val mime = mimeIn?.takeIf { it.isNotBlank() && it != "application/octet-stream" }
            ?: MimeTypeMap.getSingleton().getMimeTypeFromExtension(name.substringAfterLast('.', "").lowercase())
            ?: "application/octet-stream"
        try {
            val uri: Uri
            if (Build.VERSION.SDK_INT >= 29) {
                val cr = c.contentResolver
                val values = ContentValues().apply {
                    put(MediaStore.Downloads.DISPLAY_NAME, name)
                    put(MediaStore.Downloads.MIME_TYPE, mime)
                    put(MediaStore.Downloads.IS_PENDING, 1)
                }
                uri = cr.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values) ?: throw IllegalStateException("insert")
                cr.openOutputStream(uri)!!.use { input.copyTo(it) }
                values.clear(); values.put(MediaStore.Downloads.IS_PENDING, 0)
                cr.update(uri, values, null, null)
            } else {
                val dir = c.getExternalFilesDir(Environment.DIRECTORY_DOWNLOADS) ?: c.filesDir
                val f = java.io.File(dir, name)
                f.outputStream().use { input.copyTo(it) }
                @Suppress("DEPRECATION")
                c.getSystemService(DownloadManager::class.java)
                    .addCompletedDownload(name, name, true, mime, f.absolutePath, f.length(), true)
                uri = Uri.fromFile(f)
            }
            toast(c, c.getString(R.string.dl_saved, name))
            if (Build.VERSION.SDK_INT >= 29) main.post { open(c, uri, mime) }
        } catch (e: Exception) {
            toast(c, c.getString(R.string.dl_failed, name))
        }
    }

    private fun open(c: Context, uri: Uri, mime: String) {
        try {
            c.startActivity(Intent(Intent.ACTION_VIEW).setDataAndType(uri, mime)
                .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK))
        } catch (_: Exception) { /* no viewer: the file is in Download */ }
    }

    /**
     * The web saves files as <a download href="blob:..."> on a detached anchor and revokes the blob URL right
     * after click(). WebView's DownloadListener cannot read blob URLs nor see the download name, so this shim
     * routes such clicks to the bridge (and delays revoke a little so a pending read can finish).
     */
    const val JS_SHIM = """
(function(){
  if (window.__ipopDl || !window.iPopAndroid) return; window.__ipopDl = 1;
  var rv = URL.revokeObjectURL.bind(URL);
  URL.revokeObjectURL = function(u){ setTimeout(function(){ rv(u); }, 60000); };
  function send(a){
    var href = a.href || '', name = a.getAttribute('download') || 'download';
    if (!(href.indexOf('blob:') === 0 || href.indexOf('data:') === 0)) return false;
    fetch(href).then(function(r){ return r.blob(); }).then(function(b){
      var fr = new FileReader();
      fr.onload = function(){ var s = String(fr.result); window.iPopAndroid.saveBase64(name, b.type || '', s.substring(s.indexOf(',') + 1)); };
      fr.readAsDataURL(b);
    }).catch(function(){});
    return true;
  }
  var click = HTMLAnchorElement.prototype.click;
  HTMLAnchorElement.prototype.click = function(){ if (this.hasAttribute('download') && send(this)) return; return click.apply(this, arguments); };
  document.addEventListener('click', function(e){
    var a = e.target && e.target.closest ? e.target.closest('a[download]') : null;
    if (a && send(a)) { e.preventDefault(); e.stopPropagation(); }
  }, true);
})();
"""
}
