package id.co.santos.ipop

import android.app.Activity
import android.app.Dialog
import android.content.Intent
import android.graphics.Color
import android.graphics.drawable.ColorDrawable
import android.net.Uri
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.ProgressBar
import android.widget.TextView
import androidx.core.content.FileProvider
import id.co.santos.ipop.Ui.dp
import org.json.JSONObject
import java.io.File
import java.net.HttpURLConnection
import java.net.URL
import java.util.Locale

/** Mockup frame 4: "Versi baru iPop tersedia" bottom sheet, APK download into cache, package installer. */
object Updater {
    private const val INTERVAL_MS = 6 * 60 * 60 * 1000L
    private val main = Handler(Looper.getMainLooper())

    data class Latest(val code: Int, val name: String, val size: Long, val url: String, val notes: List<String>, val minSupported: Int)

    fun checkOnStart(a: Activity) {
        val p = Ipop.prefs(a)
        val now = System.currentTimeMillis()
        if (now - p.getLong("update_checked", 0) < INTERVAL_MS) return
        p.edit().putLong("update_checked", now).apply()
        Http.bg("update-check") {
            val r = Http.request("GET", "${Ipop.BASE}/api/app/android/latest")
            if (!r.ok) return@bg // 404 = no release published: silent
            val o = JSONObject(r.body)
            val notes = o.optJSONArray("notes")?.let { arr -> (0 until arr.length()).map { arr.optString(it) }.filter { it.isNotBlank() } } ?: emptyList()
            val l = Latest(o.optInt("versionCode"), o.optString("versionName"), o.optLong("sizeBytes"),
                o.optString("url", "/download/ipop.apk"), notes, o.optInt("minSupported"))
            if (l.code > BuildConfig.VERSION_CODE) main.post { if (!a.isFinishing && !a.isDestroyed) show(a, l) }
        }
    }

    private fun sizeText(b: Long): String = when {
        b <= 0 -> ""
        b >= 1_000_000 -> String.format(Locale("in", "ID"), "%.1f MB", b / 1_000_000.0)
        else -> String.format(Locale("in", "ID"), "%d KB", b / 1000)
    }

    fun show(a: Activity, l: Latest) {
        val force = BuildConfig.VERSION_CODE < l.minSupported
        val d = Dialog(a, R.style.Theme_IPop_Sheet)
        d.setCancelable(!force)
        d.setCanceledOnTouchOutside(!force)

        val sheet = Ui.vertical(a).apply {
            background = android.graphics.drawable.GradientDrawable().apply {
                setColor(Color.WHITE)
                val r = dp(a, 22).toFloat()
                cornerRadii = floatArrayOf(r, r, r, r, 0f, 0f, 0f, 0f)
            }
            setPadding(dp(a, 20), dp(a, 10), dp(a, 20), dp(a, 26))
        }
        sheet.addView(View(a).apply { background = Ui.round(a.getColor(R.color.fill), dp(a, 3).toFloat()) },
            Ui.lp(dp(a, 38), dp(a, 5)).apply { gravity = Gravity.CENTER_HORIZONTAL; bottomMargin = dp(a, 14) })
        sheet.addView(Ui.text(a, a.getString(R.string.up_title), 19f, a.getColor(R.color.ink), bold = true))
        val meta = a.getString(R.string.up_meta, l.name, sizeText(l.size)).replace(" ·  · ", " · ")
        sheet.addView(Ui.text(a, meta, 13.5f, a.getColor(R.color.ink3)),
            Ui.lp(Ui.WRAP, Ui.WRAP).apply { topMargin = dp(a, 4); bottomMargin = dp(a, 12) })
        for (n in l.notes) {
            sheet.addView(Ui.text(a, "•  $n", 13.5f, a.getColor(R.color.ink2)), Ui.lp(Ui.MATCH, Ui.WRAP).apply { bottomMargin = dp(a, 4) })
        }
        val progress = ProgressBar(a, null, android.R.attr.progressBarStyleHorizontal).apply {
            max = 100; progressTintList = android.content.res.ColorStateList.valueOf(a.getColor(R.color.accent)); visibility = View.GONE
        }
        sheet.addView(progress, Ui.lp(Ui.MATCH, dp(a, 6)).apply { topMargin = dp(a, 12) })
        val status = Ui.text(a, "", 13f, a.getColor(R.color.ink3)).apply { visibility = View.GONE }
        sheet.addView(status, Ui.lp(Ui.MATCH, Ui.WRAP).apply { topMargin = dp(a, 4) })

        val now = Ui.button(a, a.getString(R.string.up_now), a.getColor(R.color.accent), heightDp = 48)
        sheet.addView(now, Ui.lp(Ui.MATCH, dp(a, 48)).apply { topMargin = dp(a, 16) })
        val later = Ui.button(a, a.getString(R.string.up_later), Color.TRANSPARENT, a.getColor(R.color.ink3), heightDp = 40)
        if (!force) sheet.addView(later, Ui.lp(Ui.MATCH, dp(a, 40)).apply { topMargin = dp(a, 4) })
        later.setOnClickListener { d.dismiss() }

        var apk: File? = null
        now.setOnClickListener {
            apk?.let { install(a, it, status); return@setOnClickListener }
            now.isEnabled = false; now.alpha = 0.6f
            progress.visibility = View.VISIBLE; status.visibility = View.VISIBLE
            status.text = a.getString(R.string.up_downloading, 0)
            download(a, l, { pct -> main.post { progress.progress = pct; status.text = a.getString(R.string.up_downloading, pct) } }) { f ->
                main.post {
                    now.isEnabled = true; now.alpha = 1f
                    if (f == null) { status.text = a.getString(R.string.up_failed); return@post }
                    apk = f
                    now.text = a.getString(R.string.up_install)
                    status.text = ""
                    install(a, f, status)
                }
            }
        }

        d.setContentView(sheet)
        d.window?.apply {
            setBackgroundDrawable(ColorDrawable(Color.TRANSPARENT))
            setLayout(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT)
            setGravity(Gravity.BOTTOM)
        }
        d.show()
    }

    private fun download(a: Activity, l: Latest, onProgress: (Int) -> Unit, done: (File?) -> Unit) {
        val src = Ipop.absolute(l.url) ?: run { done(null); return }
        Http.bg("update-download") {
            val dir = File(a.cacheDir, "updates").apply { mkdirs(); listFiles()?.forEach { it.delete() } }
            val out = File(dir, "ipop-${l.code}.apk")
            var ok = false
            try {
                val c = URL(src).openConnection() as HttpURLConnection
                c.connectTimeout = 15_000; c.readTimeout = 30_000
                try {
                    if (c.responseCode !in 200..299) return@bg
                    val total = c.contentLengthLong.takeIf { it > 0 } ?: l.size
                    var read = 0L; var last = -1
                    c.inputStream.use { input ->
                        out.outputStream().use { o ->
                            val buf = ByteArray(64 * 1024)
                            while (true) {
                                val n = input.read(buf); if (n < 0) break
                                o.write(buf, 0, n); read += n
                                if (total > 0) { val pct = (read * 100 / total).toInt().coerceIn(0, 100); if (pct != last) { last = pct; onProgress(pct) } }
                            }
                        }
                    }
                    ok = read > 0
                } finally { c.disconnect() }
            } finally {
                done(if (ok) out else null.also { out.delete() })
            }
        }
    }

    private fun install(a: Activity, f: File, status: TextView) {
        if (!a.packageManager.canRequestPackageInstalls()) {
            status.visibility = View.VISIBLE
            status.text = a.getString(R.string.up_allow)
            a.startActivity(Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:${a.packageName}")))
            return
        }
        val uri = FileProvider.getUriForFile(a, "${a.packageName}.files", f)
        a.startActivity(Intent(Intent.ACTION_VIEW).apply {
            setDataAndType(uri, "application/vnd.android.package-archive")
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
        })
    }

}
