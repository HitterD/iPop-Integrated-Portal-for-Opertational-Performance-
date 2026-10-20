package id.co.santos.ipop

import android.Manifest
import android.annotation.SuppressLint
import android.app.Activity
import android.app.NotificationManager
import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.PowerManager
import android.provider.Settings
import androidx.core.app.NotificationManagerCompat
import org.json.JSONObject

/** Permission state + the right Android settings page for each one. */
object Perms {
    fun notifications(c: Context): Boolean = NotificationManagerCompat.from(c).areNotificationsEnabled() &&
        (Build.VERSION.SDK_INT < 33 || c.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED)

    fun fullScreen(c: Context): Boolean =
        Build.VERSION.SDK_INT < 34 || c.getSystemService(NotificationManager::class.java).canUseFullScreenIntent()

    fun battery(c: Context): Boolean = c.getSystemService(PowerManager::class.java).isIgnoringBatteryOptimizations(c.packageName)

    fun granted(c: Context, p: String) = c.checkSelfPermission(p) == PackageManager.PERMISSION_GRANTED

    /** Runtime permission: granted / denied (asked before and refused) / default (never asked). */
    private fun runtimeState(c: Context, p: String): String {
        if (granted(c, p)) return "granted"
        return if (Ipop.prefs(c).getBoolean("asked:$p", false)) "denied" else "default"
    }

    fun markAsked(c: Context, vararg ps: String) {
        val e = Ipop.prefs(c).edit(); ps.forEach { e.putBoolean("asked:$it", true) }; e.apply()
    }

    fun stateJson(c: Context): String {
        val notif = when {
            notifications(c) -> "granted"
            Build.VERSION.SDK_INT >= 33 -> runtimeState(c, Manifest.permission.POST_NOTIFICATIONS).let { if (it == "granted") "denied" else it }
            else -> "denied"
        }
        return JSONObject()
            .put("notifications", notif)
            .put("fullscreen", if (fullScreen(c)) "granted" else "denied")
            .put("battery", if (battery(c)) "granted" else "denied")
            .put("mic", runtimeState(c, Manifest.permission.RECORD_AUDIO))
            .put("camera", runtimeState(c, Manifest.permission.CAMERA))
            .toString()
    }

    /** True when an important permission is off, so the first-run screen should show again. */
    fun needsAttention(c: Context) = !notifications(c) || !fullScreen(c)

    @SuppressLint("BatteryLife")
    fun openSettings(a: Activity, kind: String) {
        val pkg = Uri.parse("package:${a.packageName}")
        val i = when (kind) {
            "notifications" -> Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS).putExtra(Settings.EXTRA_APP_PACKAGE, a.packageName)
            "fullscreen" -> if (Build.VERSION.SDK_INT >= 34) Intent(Settings.ACTION_MANAGE_APP_USE_FULL_SCREEN_INTENT, pkg)
                else Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS).putExtra(Settings.EXTRA_APP_PACKAGE, a.packageName)
            "battery" -> Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, pkg)
            else -> Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, pkg)
        }
        try { a.startActivity(i) } catch (_: ActivityNotFoundException) {
            try { a.startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, pkg)) } catch (_: Exception) { }
        }
    }
}
