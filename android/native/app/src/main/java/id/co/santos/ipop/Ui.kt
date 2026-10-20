package id.co.santos.ipop

import android.content.Context
import android.content.res.ColorStateList
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.graphics.drawable.RippleDrawable
import android.util.TypedValue
import android.view.Gravity
import android.view.View
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView

/** Programmatic view helpers (no XML layouts, no AppCompat) for the few native screens. */
object Ui {
    fun dp(c: Context, v: Float): Int = TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, v, c.resources.displayMetrics).toInt()
    fun dp(c: Context, v: Int): Int = dp(c, v.toFloat())

    fun round(color: Int, radiusPx: Float, stroke: Int = 0, strokeColor: Int = 0) = GradientDrawable().apply {
        setColor(color)
        cornerRadius = radiusPx
        if (stroke > 0) setStroke(stroke, strokeColor)
    }

    fun oval(color: Int) = GradientDrawable().apply { shape = GradientDrawable.OVAL; setColor(color) }

    fun ripple(bg: GradientDrawable, rippleColor: Int = 0x33FFFFFF) =
        RippleDrawable(ColorStateList.valueOf(rippleColor), bg, null)

    fun text(c: Context, s: CharSequence, sizeSp: Float, color: Int, bold: Boolean = false) = TextView(c).apply {
        text = s
        setTextSize(TypedValue.COMPLEX_UNIT_SP, sizeSp)
        setTextColor(color)
        if (bold) typeface = Typeface.create("sans-serif-medium", Typeface.BOLD)
    }

    fun icon(c: Context, res: Int, sizeDp: Int, tint: Int = Color.WHITE) = ImageView(c).apply {
        setImageResource(res)
        imageTintList = ColorStateList.valueOf(tint)
        layoutParams = LinearLayout.LayoutParams(dp(c, sizeDp), dp(c, sizeDp))
    }

    /** A filled, rounded, full-width primary button (mockup .btn). */
    fun button(c: Context, label: String, bg: Int, fg: Int = Color.WHITE, heightDp: Int = 50, radiusDp: Int = 14) = TextView(c).apply {
        text = label
        gravity = Gravity.CENTER
        setTextColor(fg)
        setTextSize(TypedValue.COMPLEX_UNIT_SP, 16f)
        typeface = Typeface.create("sans-serif-medium", Typeface.BOLD)
        background = ripple(round(bg, dp(c, radiusDp).toFloat()), if (bg == Color.TRANSPARENT) 0x22000000 else 0x33FFFFFF)
        isClickable = true
        isFocusable = true
        layoutParams = LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, dp(c, heightDp))
    }

    fun vertical(c: Context) = LinearLayout(c).apply { orientation = LinearLayout.VERTICAL }
    fun horizontal(c: Context) = LinearLayout(c).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }

    fun lp(w: Int, h: Int, weight: Float = 0f) = LinearLayout.LayoutParams(w, h, weight)
    const val MATCH = LinearLayout.LayoutParams.MATCH_PARENT
    const val WRAP = LinearLayout.LayoutParams.WRAP_CONTENT

    fun View.margins(l: Int = 0, t: Int = 0, r: Int = 0, b: Int = 0): View {
        val p = (layoutParams as? LinearLayout.LayoutParams) ?: LinearLayout.LayoutParams(WRAP, WRAP)
        p.setMargins(l, t, r, b)
        layoutParams = p
        return this
    }

    /** Two-letter initials, as the web's avatar tiles ("RM Bagastyo" -> "RB"). */
    fun initials(name: String?): String {
        val parts = (name ?: "").trim().split(Regex("\\s+")).filter { it.isNotEmpty() && it[0].isLetterOrDigit() }
        return when {
            parts.isEmpty() -> "?"
            parts.size == 1 -> parts[0].take(2).uppercase()
            else -> (parts.first().take(1) + parts.last().take(1)).uppercase()
        }
    }

    private val tileColors = intArrayOf(0xFFE07A5F.toInt(), 0xFF5E5CE6.toInt(), 0xFF0A84FF.toInt(), 0xFF2E9E5B.toInt(), 0xFFFF9F0A.toInt(), 0xFF0F6E5A.toInt())
    fun tileColor(seed: String?): Int = tileColors[((seed ?: "").hashCode() and 0x7fffffff) % tileColors.size]
}
