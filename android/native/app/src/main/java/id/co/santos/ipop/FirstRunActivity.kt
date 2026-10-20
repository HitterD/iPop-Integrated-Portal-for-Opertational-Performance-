package id.co.santos.ipop

import android.Manifest
import android.app.Activity
import android.graphics.Color
import android.os.Build
import android.os.Bundle
import android.view.Gravity
import android.view.View
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import id.co.santos.ipop.Ui.dp

/** Mockup frame 3: one screen with the three permissions that keep notifications and calls on time. */
class FirstRunActivity : Activity() {
    private lateinit var notifRow: Row
    private lateinit var fsiRow: Row
    private lateinit var batteryRow: Row

    private class Row(val status: TextView, val button: TextView)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        App.firstRunShownThisProcess = true
        Ipop.prefs(this).edit().putBoolean("first_run_done", true).apply()
        setContentView(build())
    }

    override fun onResume() {
        super.onResume()
        refresh()
    }

    private fun build(): View {
        val c = this
        val scroll = ScrollView(c).apply { isFillViewport = true; fitsSystemWindows = true }
        val col = Ui.vertical(c).apply { setPadding(dp(c, 20), dp(c, 18), dp(c, 20), dp(c, 18)) }
        scroll.addView(col)

        val logo = FrameLayout(c).apply {
            background = Ui.round(Color.WHITE, dp(c, 15).toFloat())
            elevation = dp(c, 3).toFloat()
        }
        logo.addView(ImageView(c).apply { setImageResource(R.mipmap.ic_maskable) },
            FrameLayout.LayoutParams(dp(c, 42), dp(c, 42), Gravity.CENTER))
        col.addView(logo, Ui.lp(dp(c, 52), dp(c, 52)).apply { topMargin = dp(c, 8); bottomMargin = dp(c, 12) })

        col.addView(Ui.text(c, getString(R.string.fr_title), 22f, getColor(R.color.ink), bold = true))
        col.addView(Ui.text(c, getString(R.string.fr_lead), 15f, getColor(R.color.ink3)),
            Ui.lp(Ui.WRAP, Ui.WRAP).apply { topMargin = dp(c, 6); bottomMargin = dp(c, 18) })

        notifRow = row(col, R.drawable.ic_bell, 0xFFFF3B30.toInt(), R.string.fr_notif, R.string.fr_notif_sub) { requestNotifications() }
        fsiRow = row(col, R.drawable.ic_device, 0xFF0A84FF.toInt(), R.string.fr_fsi, R.string.fr_fsi_sub) { Perms.openSettings(this, "fullscreen") }
        batteryRow = row(col, R.drawable.ic_battery, 0xFF34C759.toInt(), R.string.fr_battery, R.string.fr_battery_sub) { Perms.openSettings(this, "battery") }
        val mic = row(col, R.drawable.ic_mic, 0xFF5E5CE6.toInt(), R.string.fr_mic, R.string.fr_mic_sub) { }
        mic.button.visibility = View.GONE
        mic.status.visibility = View.VISIBLE
        mic.status.text = getString(R.string.fr_later)
        mic.status.setTextColor(getColor(R.color.ink3))

        col.addView(View(c), Ui.lp(1, 0, 1f))
        col.addView(Ui.button(c, getString(R.string.fr_continue), getColor(R.color.accent)).apply {
            setOnClickListener { finish() }
        }, Ui.lp(Ui.MATCH, dp(c, 50)).apply { topMargin = dp(c, 16) })
        col.addView(Ui.text(c, getString(R.string.fr_skip), 13f, getColor(R.color.ink3)).apply { gravity = Gravity.CENTER },
            Ui.lp(Ui.MATCH, Ui.WRAP).apply { topMargin = dp(c, 10) })
        return scroll
    }

    private fun row(parent: LinearLayout, icon: Int, color: Int, title: Int, sub: Int, onEnable: () -> Unit): Row {
        val c = this
        val r = Ui.horizontal(c).apply {
            background = Ui.round(Color.WHITE, dp(c, 14).toFloat())
            setPadding(dp(c, 12), dp(c, 10), dp(c, 12), dp(c, 10))
        }
        val ic = FrameLayout(c).apply { background = Ui.round(color, dp(c, 11).toFloat()) }
        val img = Ui.icon(c, icon, 22)
        ic.addView(img, FrameLayout.LayoutParams(dp(c, 22), dp(c, 22), Gravity.CENTER))
        r.addView(ic, Ui.lp(dp(c, 38), dp(c, 38)).apply { marginEnd = dp(c, 10) })
        val tx = Ui.vertical(c)
        tx.addView(Ui.text(c, getString(title), 14f, getColor(R.color.ink), bold = true))
        tx.addView(Ui.text(c, getString(sub), 12f, getColor(R.color.ink3)))
        r.addView(tx, Ui.lp(0, Ui.WRAP, 1f))
        val status = Ui.text(c, getString(R.string.fr_on), 13f, getColor(R.color.accent), bold = true)
        val btn = TextView(c).apply {
            text = getString(R.string.fr_enable)
            setTextColor(Color.WHITE)
            textSize = 13f
            typeface = android.graphics.Typeface.DEFAULT_BOLD
            gravity = Gravity.CENTER
            setPadding(dp(c, 14), 0, dp(c, 14), 0)
            background = Ui.ripple(Ui.round(getColor(R.color.accent), dp(c, 15).toFloat()))
            setOnClickListener { onEnable() }
        }
        r.addView(status, Ui.lp(Ui.WRAP, Ui.WRAP).apply { marginStart = dp(c, 8) })
        r.addView(btn, Ui.lp(Ui.WRAP, dp(c, 32)).apply { marginStart = dp(c, 8) })
        parent.addView(r, Ui.lp(Ui.MATCH, Ui.WRAP).apply { bottomMargin = dp(c, 8) })
        return Row(status, btn)
    }

    private fun setRow(r: Row, on: Boolean) {
        r.status.visibility = if (on) View.VISIBLE else View.GONE
        r.button.visibility = if (on) View.GONE else View.VISIBLE
    }

    private fun refresh() {
        setRow(notifRow, Perms.notifications(this))
        setRow(fsiRow, Perms.fullScreen(this))
        setRow(batteryRow, Perms.battery(this))
    }

    private fun requestNotifications() {
        val p = Manifest.permission.POST_NOTIFICATIONS
        // Ask once at runtime; after a refusal Android stops showing the dialog, so go to settings.
        if (Build.VERSION.SDK_INT >= 33 && !Perms.granted(this, p) &&
            (!Ipop.prefs(this).getBoolean("asked:$p", false) || shouldShowRequestPermissionRationale(p))) {
            Perms.markAsked(this, p)
            requestPermissions(arrayOf(p), 7)
        } else {
            Perms.openSettings(this, "notifications")
        }
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        refresh()
    }

}
