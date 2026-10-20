package id.co.santos.ipop

import android.animation.ObjectAnimator
import android.animation.PropertyValuesHolder
import android.animation.ValueAnimator
import android.app.Activity
import android.app.KeyguardManager
import android.app.NotificationManager
import android.content.Intent
import android.graphics.Color
import android.graphics.drawable.GradientDrawable
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.view.View
import android.view.WindowManager
import android.view.animation.AccelerateDecelerateInterpolator
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextView
import id.co.santos.ipop.Ui.dp
import id.co.santos.ipop.Ui.margins
import java.lang.ref.WeakReference

/** Mockup frame 1: full-screen incoming call, also over the lock screen. */
class IncomingCallActivity : Activity() {
    companion object {
        const val ACTION_ANSWER = "id.co.santos.ipop.ANSWER"
        private const val TIMEOUT_MS = 45_000L
        private var current: WeakReference<IncomingCallActivity>? = null

        /** A "close" push for [tag]: finish the screen if it shows that call. */
        fun dismiss(tag: String) {
            Handler(Looper.getMainLooper()).post {
                val a = current?.get() ?: return@post
                if (a.data["tag"] == tag) a.finishCall()
            }
        }
    }

    private var data: Map<String, String> = emptyMap()
    private val main = Handler(Looper.getMainLooper())
    private val animators = mutableListOf<ValueAnimator>()
    private val timeout = Runnable { finishCall() }
    private var done = false
    private var reportedTag: String? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setShowWhenLocked(true)
        setTurnScreenOn(true)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        window.setBackgroundDrawable(GradientDrawable().apply {
            gradientType = GradientDrawable.RADIAL_GRADIENT
            colors = intArrayOf(0xFF1F5A43.toInt(), 0xFF123528.toInt(), 0xFF0B2219.toInt())
            setGradientCenter(0.5f, 0.2f)
            gradientRadius = resources.displayMetrics.heightPixels * 0.85f
        })
        current = WeakReference(this)
        bind(intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        bind(intent)
    }

    private fun bind(i: Intent) {
        data = ActionReceiver.extrasOf(i)
        if (i.action == ACTION_ANSWER) { answer(); return }
        // Receipt: the full-screen call screen really started (once per call).
        val tag = data["tag"]
        if (tag != null && tag != reportedTag) {
            reportedTag = tag
            val app = applicationContext
            val d = data
            Http.bg("receipt-call") {
                Receipts.report(app, d["deliveryId"], "call", "call-activity-started",
                    "${Receipts.environment(app, Channels.CALLS)} locked=${getSystemService(android.app.KeyguardManager::class.java).isKeyguardLocked}")
            }
        }
        done = false
        setContentView(build())
        Ringer.start(this, data["tag"] ?: "call:", TIMEOUT_MS)
        main.removeCallbacks(timeout)
        main.postDelayed(timeout, TIMEOUT_MS)
    }

    private fun build(): View {
        val c = this
        val name = Notifier.callerName(data)
        val root = FrameLayout(c)
        root.fitsSystemWindows = true

        val top = Ui.vertical(c).apply { gravity = Gravity.CENTER_HORIZONTAL }
        val app = Ui.horizontal(c).apply { gravity = Gravity.CENTER }
        app.addView(android.widget.ImageView(c).apply {
            setImageResource(R.mipmap.ic_maskable)
            background = Ui.round(Color.WHITE, dp(c, 5).toFloat())
            setPadding(dp(c, 1), dp(c, 1), dp(c, 1), dp(c, 1))
            clipToOutline = true
            layoutParams = LinearLayout.LayoutParams(dp(c, 16), dp(c, 16)).apply { marginEnd = dp(c, 6) }
        })
        app.addView(Ui.text(c, getString(R.string.call_sub), 13f, 0xBFFFFFFF.toInt()))
        top.addView(app, Ui.lp(Ui.WRAP, Ui.WRAP).apply { topMargin = dp(c, 70) })

        // Avatar tile with two expanding rings.
        val avatarBox = FrameLayout(c)
        val ringSize = dp(c, 144)
        repeat(2) { idx ->
            val ring = View(c).apply {
                background = GradientDrawable().apply {
                    cornerRadius = dp(c, 40).toFloat(); setStroke(dp(c, 2), 0x38FFFFFF)
                }
            }
            avatarBox.addView(ring, FrameLayout.LayoutParams(ringSize, ringSize, Gravity.CENTER))
            val a = ObjectAnimator.ofPropertyValuesHolder(ring,
                PropertyValuesHolder.ofFloat(View.SCALE_X, 0.9f, 1.25f),
                PropertyValuesHolder.ofFloat(View.SCALE_Y, 0.9f, 1.25f),
                PropertyValuesHolder.ofFloat(View.ALPHA, 1f, 0f)).apply {
                duration = 2000; repeatCount = ValueAnimator.INFINITE; startDelay = idx * 1000L
            }
            animators += a
        }
        val av = TextView(c).apply {
            text = Ui.initials(name)
            gravity = Gravity.CENTER
            setTextColor(Color.WHITE)
            textSize = 40f
            typeface = android.graphics.Typeface.DEFAULT_BOLD
            background = GradientDrawable(GradientDrawable.Orientation.TL_BR, intArrayOf(0xFFE07A5F.toInt(), 0xFFC9573C.toInt()))
                .apply { cornerRadius = dp(c, 30).toFloat() }
        }
        avatarBox.addView(av, FrameLayout.LayoutParams(dp(c, 116), dp(c, 116), Gravity.CENTER))
        top.addView(avatarBox, Ui.lp(ringSize + dp(c, 40), ringSize + dp(c, 40)).apply { topMargin = dp(c, 6) })

        top.addView(Ui.text(c, name, 28f, Color.WHITE, bold = true).apply { gravity = Gravity.CENTER; setPadding(dp(c, 24), 0, dp(c, 24), 0) })
        val subText = data["body"]?.takeIf { it.isNotBlank() && !it.equals("iPop voice call", true) } ?: getString(R.string.call_calling)
        top.addView(Ui.text(c, subText, 15f, 0xCCFFFFFF.toInt()).apply { gravity = Gravity.CENTER }, Ui.lp(Ui.WRAP, Ui.WRAP).apply { topMargin = dp(c, 4) })
        root.addView(top, FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.WRAP_CONTENT, Gravity.TOP))

        // Bottom: quick actions (Pesan / Ingatkan), then Tolak / Angkat.
        val bottom = Ui.vertical(c).apply { gravity = Gravity.CENTER_HORIZONTAL }
        val quick = Ui.horizontal(c).apply { gravity = Gravity.CENTER }
        quick.addView(roundAction(R.drawable.ic_message, getString(R.string.call_message), 46, 0x24FFFFFF) { message() })
        quick.addView(View(c), Ui.lp(dp(c, 56), 1))
        quick.addView(roundAction(R.drawable.ic_clock, getString(R.string.call_remind), 46, 0x24FFFFFF) { remind() })
        bottom.addView(quick)

        val btns = Ui.horizontal(c).apply { gravity = Gravity.CENTER }
        val decline = roundAction(R.drawable.ic_phone, getString(R.string.call_decline), 72, getColor(R.color.red), iconRotation = 135f) { decline() }
        val answer = roundAction(R.drawable.ic_phone, getString(R.string.call_answer), 72, getColor(R.color.call_green)) { answer() }
        btns.addView(decline)
        btns.addView(View(c), Ui.lp(dp(c, 96), 1))
        btns.addView(answer)
        bottom.addView(btns, Ui.lp(Ui.WRAP, Ui.WRAP).apply { topMargin = dp(c, 44) })
        // Wiggle the green button: rotate 0 -> -14 -> 12 -> -8 -> 0 in the last 40% of 1.4 s.
        val circle = (answer as LinearLayout).getChildAt(0)
        animators += ObjectAnimator.ofFloat(circle, View.ROTATION, 0f, 0f, 0f, 0f, 0f, 0f, 0f, -14f, 12f, -8f, 0f).apply {
            duration = 1400; repeatCount = ValueAnimator.INFINITE; interpolator = AccelerateDecelerateInterpolator()
        }
        root.addView(bottom, FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.WRAP_CONTENT,
            Gravity.BOTTOM).apply { bottomMargin = dp(c, 64) })

        animators.forEach { it.start() }
        return root
    }

    private fun roundAction(icon: Int, label: String, sizeDp: Int, color: Int, iconRotation: Float = 0f, onClick: () -> Unit): View {
        val c = this
        val col = Ui.vertical(c).apply { gravity = Gravity.CENTER_HORIZONTAL }
        val circle = FrameLayout(c).apply {
            background = Ui.ripple(GradientDrawable().apply { shape = GradientDrawable.OVAL; setColor(color) })
            isClickable = true; isFocusable = true
            contentDescription = label
            setOnClickListener { onClick() }
        }
        val img = Ui.icon(c, icon, if (sizeDp >= 60) 30 else 22).apply { rotation = iconRotation }
        circle.addView(img, FrameLayout.LayoutParams(img.layoutParams.width, img.layoutParams.height, Gravity.CENTER))
        col.addView(circle, Ui.lp(dp(c, sizeDp), dp(c, sizeDp)))
        col.addView(Ui.text(c, label, 13f, Color.WHITE).margins(t = dp(c, 8)))
        return col
    }

    private fun stopRinging() {
        Ringer.stop(data["tag"])
        data["tag"]?.let { getSystemService(NotificationManager::class.java).cancel(it, Notifier.ID) }
    }

    private fun answer() {
        if (done) return
        done = true
        stopRinging()
        val callId = data["callId"].orEmpty()
        val open = Intent(this, MainActivity::class.java).apply {
            putExtra("url", if (callId.isNotBlank()) "/calls?answer=$callId" else "/calls")
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP)
        }
        // Only answering dismisses the keyguard; declining leaves the phone locked.
        val km = getSystemService(KeyguardManager::class.java)
        if (km.isKeyguardLocked) {
            km.requestDismissKeyguard(this, object : KeyguardManager.KeyguardDismissCallback() {
                override fun onDismissSucceeded() { startActivity(open); finishCall() }
                override fun onDismissCancelled() { startActivity(open); finishCall() }
                override fun onDismissError() { startActivity(open); finishCall() }
            })
        } else {
            startActivity(open)
            finishCall()
        }
    }

    private fun decline() {
        if (done) return
        done = true
        ActionReceiver.declineCall(this, data)
        finishCall()
    }

    private fun message() {
        decline()
        startActivity(Intent(this, MainActivity::class.java).apply {
            putExtra("url", "/chat?new=dm")
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        })
    }

    private fun remind() {
        ActionReceiver.scheduleReminder(this, data)
        decline()
    }

    fun finishCall() {
        stopRinging()
        main.removeCallbacks(timeout)
        finishAndRemoveTask()
    }

    override fun onDestroy() {
        animators.forEach { it.cancel() }
        animators.clear()
        main.removeCallbacks(timeout)
        if (current?.get() === this) current = null
        // Leaving the screen any way other than answer stops the ring (the notification stays for the timeout).
        Ringer.stop(data["tag"])
        super.onDestroy()
    }

    @Deprecated("Back = keep ringing in the notification; the screen just goes away.")
    override fun onBackPressed() {
        moveTaskToBack(true)
    }
}
