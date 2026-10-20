package id.co.santos.ipop

import android.Manifest
import android.annotation.SuppressLint
import android.app.Activity
import android.content.ActivityNotFoundException
import android.content.ClipData
import android.content.Context
import android.content.Intent
import android.graphics.Color
import android.net.ConnectivityManager
import android.net.Network
import android.net.Uri
import android.net.http.SslError
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.provider.MediaStore
import android.util.Log
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.webkit.CookieManager
import android.webkit.PermissionRequest
import android.webkit.SslErrorHandler
import android.webkit.ValueCallback
import android.webkit.WebChromeClient
import android.webkit.WebResourceError
import android.webkit.WebResourceRequest
import android.webkit.WebSettings
import android.webkit.WebView
import android.webkit.WebViewClient
import android.widget.FrameLayout
import android.widget.LinearLayout
import androidx.browser.customtabs.CustomTabColorSchemeParams
import androidx.browser.customtabs.CustomTabsIntent
import androidx.core.content.FileProvider
import androidx.core.splashscreen.SplashScreen.Companion.installSplashScreen
import androidx.core.view.ViewCompat
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import id.co.santos.ipop.Ui.dp
import java.io.File
import java.lang.ref.WeakReference

class MainActivity : Activity() {
    companion object {
        private const val REQ_FILE = 11
        private const val REQ_MEDIA = 12
        private var current: WeakReference<MainActivity>? = null

        /** Ongoing-call notification Tutup / Bisukan -> window 'ipop-native' event in the page. */
        fun dispatchNative(c: Context, action: String) {
            val a = current?.get()
            if (a == null) { if (action == "hangup") OngoingCallService.stop(c); return }
            a.runOnUiThread {
                a.web.evaluateJavascript(
                    "window.dispatchEvent(new CustomEvent('ipop-native',{detail:{action:${Ipop.jsString(action)}}}))", null)
            }
        }
    }

    lateinit var web: WebView
    private lateinit var root: FrameLayout
    private var offline: View? = null
    private val main = Handler(Looper.getMainLooper())

    /** Top-level URL, updated on the UI thread; read by the JS bridge (any thread). */
    @Volatile var currentUrl: String? = null

    private var pageLoaded = false
    private var mainFrameFailed = false
    private var filePathCallback: ValueCallback<Array<Uri>>? = null
    private var cameraUri: Uri? = null
    private var pendingMedia: PermissionRequest? = null
    private var netCallback: ConnectivityManager.NetworkCallback? = null

    @SuppressLint("SetJavaScriptEnabled", "JavascriptInterface")
    override fun onCreate(savedInstanceState: Bundle?) {
        installSplashScreen()
        super.onCreate(savedInstanceState)
        current = WeakReference(this)

        // Edge-to-edge with a forest-green status bar (light icons); the WebView is padded by the system
        // bar insets, so the page's env(safe-area-inset-*) resolve to 0 and nothing sits under the bars.
        WindowCompat.setDecorFitsSystemWindows(window, false)
        @Suppress("DEPRECATION")
        window.statusBarColor = Color.TRANSPARENT
        WindowCompat.getInsetsController(window, window.decorView).isAppearanceLightStatusBars = false

        root = FrameLayout(this).apply { setBackgroundColor(getColor(R.color.forest)) }
        web = WebView(this)
        root.addView(web, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
        setContentView(root)
        ViewCompat.setOnApplyWindowInsetsListener(root) { v, insets ->
            val bars = insets.getInsets(WindowInsetsCompat.Type.systemBars() or WindowInsetsCompat.Type.displayCutout())
            val ime = insets.getInsets(WindowInsetsCompat.Type.ime())
            v.setPadding(bars.left, bars.top, bars.right, maxOf(bars.bottom, ime.bottom))
            WindowInsetsCompat.CONSUMED
        }

        setupWebView()
        if (savedInstanceState != null) web.restoreState(savedInstanceState)
        if (web.url == null) web.loadUrl(Ipop.absolute(intent.urlExtra()) ?: Ipop.START)

        watchNetwork()
        Push.refreshConfig(this) // refresh FCM config + token; registration waits for onSignedIn
        Updater.checkOnStart(this)
        if (BuildConfig.DEBUG && intent.getBooleanExtra("debug_update_sheet", false)) {
            // QA only: preview the update sheet without a published release.
            Updater.show(this, Updater.Latest(3, "2.1.0", 3_100_000, "/download/ipop.apk",
                listOf("Telepon tetap berdering saat HP terkunci", "Balas chat langsung dari notifikasi"),
                intent.getIntExtra("debug_min_supported", 0)))
        }
        maybeFirstRun()
    }

    private fun Intent.urlExtra(): String? = getStringExtra("url")
        ?: data?.toString()?.takeIf { Ipop.isIpop(it) || it.startsWith("/") }

    @SuppressLint("SetJavaScriptEnabled", "JavascriptInterface")
    private fun setupWebView() {
        if (BuildConfig.DEBUG) WebView.setWebContentsDebuggingEnabled(true)
        with(web.settings) {
            javaScriptEnabled = true
            domStorageEnabled = true
            databaseEnabled = true
            mediaPlaybackRequiresUserGesture = false
            setSupportMultipleWindows(false)
            javaScriptCanOpenWindowsAutomatically = false
            allowFileAccess = false
            allowContentAccess = true
            mixedContentMode = WebSettings.MIXED_CONTENT_NEVER_ALLOW
            setGeolocationEnabled(false)
            userAgentString = "$userAgentString iPopAndroid/${BuildConfig.VERSION_NAME}"
        }
        CookieManager.getInstance().setAcceptCookie(true)
        CookieManager.getInstance().setAcceptThirdPartyCookies(web, false)
        web.addJavascriptInterface(Bridge(this), "iPopAndroid")
        web.setDownloadListener { url, ua, cd, mime, _ -> Downloads.start(this, url, ua, cd, mime) }

        web.webViewClient = object : WebViewClient() {
            override fun shouldOverrideUrlLoading(view: WebView, request: WebResourceRequest): Boolean {
                val url = request.url.toString()
                if (Ipop.isIpop(url)) return false
                if (url.startsWith("about:") || url.startsWith("blob:") || url.startsWith("data:")) return false
                openExternal(request.url)
                return true
            }

            override fun onPageStarted(view: WebView, url: String?, favicon: android.graphics.Bitmap?) {
                currentUrl = url
                mainFrameFailed = false
            }

            override fun doUpdateVisitedHistory(view: WebView, url: String?, isReload: Boolean) {
                currentUrl = url // SPA navigations (Blazor pushState)
            }

            override fun onPageFinished(view: WebView, url: String?) {
                currentUrl = url
                if (!mainFrameFailed) {
                    pageLoaded = true
                    hideOffline()
                    if (Ipop.isIpop(url)) view.evaluateJavascript(Downloads.JS_SHIM, null)
                }
            }

            override fun onReceivedError(view: WebView, request: WebResourceRequest, error: WebResourceError) {
                if (!request.isForMainFrame) return
                Log.w(Ipop.TAG, "main frame error ${error.errorCode}: ${error.description}")
                mainFrameFailed = true
                showOffline()
            }

            override fun onReceivedSslError(view: WebView, handler: SslErrorHandler, error: SslError) {
                val ok = SjaTrust.shouldProceed(this@MainActivity, error)
                Log.i(Ipop.TAG, "ssl error primary=${error.primaryError} on ${Uri.parse(error.url).host}: ${if (ok) "proceed (SJA root)" else "cancel"}")
                if (ok) handler.proceed() else handler.cancel()
            }

            override fun onRenderProcessGone(view: WebView, detail: android.webkit.RenderProcessGoneDetail): Boolean {
                // Renderer crashed or was killed for memory: rebuild the activity rather than crash the app.
                recreate()
                return true
            }
        }

        web.webChromeClient = object : WebChromeClient() {
            override fun onShowFileChooser(view: WebView, callback: ValueCallback<Array<Uri>>, params: FileChooserParams): Boolean {
                filePathCallback?.onReceiveValue(null)
                filePathCallback = callback
                openFileChooser(params)
                return true
            }

            override fun onPermissionRequest(request: PermissionRequest) {
                runOnUiThread { handleMediaPermission(request) }
            }

            override fun onPermissionRequestCanceled(request: PermissionRequest) {
                if (pendingMedia === request) pendingMedia = null
            }
        }
    }

    // ── navigation from notifications / deep links ──
    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        val abs = Ipop.absolute(intent.urlExtra()) ?: return
        navigate(abs)
    }

    private fun navigate(abs: String) {
        if (!pageLoaded || !Ipop.isIpop(currentUrl) || mainFrameFailed) { web.loadUrl(abs); return }
        // In-app SPA navigation: Blazor router if present, else pushState + popstate, else full load.
        val path = Ipop.jsString(Ipop.pathOf(abs))
        web.evaluateJavascript(
            "(function(p){try{if(window.Blazor&&Blazor.navigateTo){Blazor.navigateTo(p);return 1}" +
                "history.pushState(null,'',p);dispatchEvent(new PopStateEvent('popstate',{state:null}));return 1}catch(e){return 0}})($path)"
        ) { r -> if (r != "1") web.loadUrl(abs) }
    }

    private fun openExternal(uri: Uri) {
        val scheme = uri.scheme ?: ""
        try {
            if (scheme == "http" || scheme == "https") {
                CustomTabsIntent.Builder()
                    .setDefaultColorSchemeParams(CustomTabColorSchemeParams.Builder().setToolbarColor(getColor(R.color.forest)).build())
                    .setShowTitle(true)
                    .build().launchUrl(this, uri)
            } else {
                // tel:, mailto:, intent: etc.
                val i = if (scheme == "intent") Intent.parseUri(uri.toString(), Intent.URI_INTENT_SCHEME).apply {
                    addCategory(Intent.CATEGORY_BROWSABLE); component = null; selector = null
                } else Intent(Intent.ACTION_VIEW, uri)
                startActivity(i)
            }
        } catch (_: ActivityNotFoundException) {
            try { startActivity(Intent(Intent.ACTION_VIEW, uri)) } catch (_: Exception) { }
        } catch (_: Exception) { }
    }

    /** The page's current bearer token via window.iPopNativeAuth(), falling back to the stored session token. */
    fun withPageToken(cb: (String?) -> Unit) {
        if (!Ipop.isIpop(currentUrl)) { cb(Session.token(this)); return }
        web.evaluateJavascript("(function(){try{return window.iPopNativeAuth?window.iPopNativeAuth():null}catch(e){return null}})()") { raw ->
            val t = raw?.takeIf { it.length > 2 && it != "null" }?.let {
                try { org.json.JSONArray("[$it]").optString(0) } catch (_: Exception) { null }
            }?.takeIf { it.isNotBlank() }
            cb(t ?: Session.token(this))
        }
    }

    // ── file upload: camera + gallery/documents, multiple ──
    private fun openFileChooser(params: WebChromeClient.FileChooserParams) {
        val accept = params.acceptTypes.filter { it.isNotBlank() }.flatMap { it.split(',') }.map { it.trim() }
        val mimes = accept.map { t ->
            if (t.startsWith(".")) android.webkit.MimeTypeMap.getSingleton().getMimeTypeFromExtension(t.substring(1)) ?: "*/*" else t
        }.distinct()
        val content = Intent(Intent.ACTION_GET_CONTENT).apply {
            addCategory(Intent.CATEGORY_OPENABLE)
            type = if (mimes.size == 1) mimes[0] else "*/*"
            if (mimes.size > 1) putExtra(Intent.EXTRA_MIME_TYPES, mimes.toTypedArray())
            putExtra(Intent.EXTRA_ALLOW_MULTIPLE, params.mode == WebChromeClient.FileChooserParams.MODE_OPEN_MULTIPLE)
        }
        val wantsImages = mimes.isEmpty() || mimes.any { it == "*/*" || it.startsWith("image/") }
        val extras = mutableListOf<Intent>()
        cameraUri = null
        if (wantsImages && packageManager.hasSystemFeature(android.content.pm.PackageManager.FEATURE_CAMERA_ANY)) {
            try {
                val dir = File(cacheDir, "camera").apply { mkdirs() }
                val f = File.createTempFile("ipop_", ".jpg", dir)
                val uri = FileProvider.getUriForFile(this, "$packageName.files", f)
                cameraUri = uri
                extras += Intent(MediaStore.ACTION_IMAGE_CAPTURE).apply {
                    putExtra(MediaStore.EXTRA_OUTPUT, uri)
                    addFlags(Intent.FLAG_GRANT_WRITE_URI_PERMISSION or Intent.FLAG_GRANT_READ_URI_PERMISSION)
                    clipData = ClipData.newRawUri("", uri)
                }
            } catch (e: Exception) { Log.w(Ipop.TAG, "camera intent: ${e.message}") }
        }
        val chooser = Intent.createChooser(content, getString(R.string.choose_file)).apply {
            if (extras.isNotEmpty()) putExtra(Intent.EXTRA_INITIAL_INTENTS, extras.toTypedArray())
        }
        val directCamera = params.isCaptureEnabled && extras.isNotEmpty()
        try {
            startActivityForResult(if (directCamera) extras[0] else chooser, REQ_FILE)
        } catch (_: ActivityNotFoundException) {
            filePathCallback?.onReceiveValue(null); filePathCallback = null
        }
    }

    @Deprecated("Framework Activity: no ActivityResult API without AndroidX activity")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode != REQ_FILE) return
        val cb = filePathCallback ?: return
        filePathCallback = null
        if (resultCode != RESULT_OK) { cb.onReceiveValue(null); return }
        val uris = mutableListOf<Uri>()
        data?.clipData?.let { cd -> for (i in 0 until cd.itemCount) cd.getItemAt(i).uri?.let { uris += it } }
        if (uris.isEmpty()) data?.data?.let { uris += it }
        if (uris.isEmpty()) cameraUri?.let { cam ->
            // Camera wrote to our file; empty file = cancelled.
            if (contentResolver.openFileDescriptor(cam, "r")?.use { it.statSize } ?: 0 > 0) uris += cam
        }
        cb.onReceiveValue(if (uris.isEmpty()) null else uris.toTypedArray())
    }

    // ── mic / camera for WebRTC + LiveKit ──
    private fun handleMediaPermission(request: PermissionRequest) {
        if (!Ipop.isIpop(request.origin.toString())) { request.deny(); return }
        val needed = mutableListOf<String>()
        for (r in request.resources) when (r) {
            PermissionRequest.RESOURCE_AUDIO_CAPTURE -> needed += Manifest.permission.RECORD_AUDIO
            PermissionRequest.RESOURCE_VIDEO_CAPTURE -> needed += Manifest.permission.CAMERA
        }
        val missing = needed.filter { !Perms.granted(this, it) }
        if (missing.isEmpty()) { grantMedia(request); return }
        pendingMedia?.deny()
        pendingMedia = request
        Perms.markAsked(this, *missing.toTypedArray())
        requestPermissions(missing.toTypedArray(), REQ_MEDIA)
    }

    private fun grantMedia(request: PermissionRequest) {
        val ok = request.resources.filter {
            (it == PermissionRequest.RESOURCE_AUDIO_CAPTURE && Perms.granted(this, Manifest.permission.RECORD_AUDIO)) ||
                (it == PermissionRequest.RESOURCE_VIDEO_CAPTURE && Perms.granted(this, Manifest.permission.CAMERA))
        }
        if (ok.isEmpty()) request.deny() else request.grant(ok.toTypedArray())
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == REQ_MEDIA) pendingMedia?.let { grantMedia(it); pendingMedia = null }
    }

    // ── offline screen (mockup frame 6) ──
    private fun showOffline() {
        if (offline == null) offline = buildOffline().also { root.addView(it) }
        offline?.visibility = View.VISIBLE
        web.visibility = View.INVISIBLE
    }

    private fun hideOffline() {
        offline?.visibility = View.GONE
        web.visibility = View.VISIBLE
    }

    private fun retry() {
        val target = currentUrl?.takeIf { Ipop.isIpop(it) } ?: Ipop.START
        mainFrameFailed = false
        web.loadUrl(target)
    }

    private fun buildOffline(): View {
        val c = this
        val box = Ui.vertical(c).apply {
            gravity = Gravity.CENTER
            setBackgroundColor(getColor(R.color.bg))
            setPadding(dp(c, 34), 0, dp(c, 34), 0)
            isClickable = true
        }
        val ic = FrameLayout(c).apply { background = Ui.round(getColor(R.color.fill), dp(c, 20).toFloat()) }
        ic.addView(Ui.icon(c, R.drawable.ic_wifi_off, 30, getColor(R.color.ink3)),
            FrameLayout.LayoutParams(dp(c, 30), dp(c, 30), Gravity.CENTER))
        box.addView(ic, Ui.lp(dp(c, 64), dp(c, 64)).apply { bottomMargin = dp(c, 14) })
        box.addView(Ui.text(c, getString(R.string.off_title), 19f, getColor(R.color.ink), bold = true).apply { gravity = Gravity.CENTER })
        box.addView(Ui.text(c, getString(R.string.off_body), 14f, getColor(R.color.ink3)).apply { gravity = Gravity.CENTER },
            Ui.lp(Ui.WRAP, Ui.WRAP).apply { topMargin = dp(c, 6); bottomMargin = dp(c, 18) })
        box.addView(Ui.button(c, getString(R.string.off_retry), getColor(R.color.accent), heightDp = 48).apply {
            setOnClickListener { retry() }
        }, LinearLayout.LayoutParams(dp(c, 180), dp(c, 48)))
        return box.apply { layoutParams = FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT) }
    }

    private fun watchNetwork() {
        val cm = getSystemService(ConnectivityManager::class.java)
        val cb = object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: Network) {
                main.post { if (mainFrameFailed || offline?.visibility == View.VISIBLE) retry() }
            }
        }
        try { cm.registerDefaultNetworkCallback(cb); netCallback = cb } catch (_: Exception) { }
        if (cm.activeNetwork == null) showOffline()
    }

    // ── first run (mockup frame 3) ──
    private fun maybeFirstRun() {
        // Checked on every cold start (consumes the exit record even when first-run shows instead).
        val forceStopped = Perms.wasForceStopped(this)
        if (forceStopped) Log.i(Ipop.TAG, "previous exit was a user force stop")
        if (forceStopped && !intent.hasExtra("debug_update_sheet")) { showForceStopNotice(); return }
        if (App.firstRunShownThisProcess) return
        val first = !Ipop.prefs(this).getBoolean("first_run_done", false)
        if (first || Perms.needsAttention(this)) {
            App.firstRunShownThisProcess = true
            startActivity(Intent(this, FirstRunActivity::class.java))
        }
    }

    /** Result of a native action started by the page (sendTestNotification): toast + 'ipop-native' event. */
    fun nativeResult(action: String, status: Int) {
        val ok = status in 200..299
        android.widget.Toast.makeText(this,
            if (ok) getString(R.string.test_sent) else getString(R.string.test_failed, if (status == 0) "offline" else "HTTP $status"),
            android.widget.Toast.LENGTH_LONG).show()
        web.evaluateJavascript(
            "window.dispatchEvent(new CustomEvent('ipop-native',{detail:{action:${Ipop.jsString(action)},status:$status}}))", null)
    }

    /** One-time sheet after a Force stop: notifications were blocked while the app was stopped. */
    private fun showForceStopNotice() {
        val c = this
        val d = android.app.Dialog(c, R.style.Theme_IPop_Sheet)
        val sheet = Ui.vertical(c).apply {
            background = android.graphics.drawable.GradientDrawable().apply {
                setColor(Color.WHITE)
                val r = dp(c, 22).toFloat()
                cornerRadii = floatArrayOf(r, r, r, r, 0f, 0f, 0f, 0f)
            }
            setPadding(dp(c, 20), dp(c, 10), dp(c, 20), dp(c, 26))
        }
        sheet.addView(View(c).apply { background = Ui.round(getColor(R.color.fill), dp(c, 3).toFloat()) },
            Ui.lp(dp(c, 38), dp(c, 5)).apply { gravity = Gravity.CENTER_HORIZONTAL; bottomMargin = dp(c, 14) })
        val ic = FrameLayout(c).apply { background = Ui.round(0xFFFF9F0A.toInt(), dp(c, 12).toFloat()) }
        ic.addView(Ui.icon(c, R.drawable.ic_bell, 24), FrameLayout.LayoutParams(dp(c, 24), dp(c, 24), Gravity.CENTER))
        sheet.addView(ic, Ui.lp(dp(c, 44), dp(c, 44)).apply { bottomMargin = dp(c, 12) })
        sheet.addView(Ui.text(c, getString(R.string.fs_title), 19f, getColor(R.color.ink), bold = true))
        sheet.addView(Ui.text(c, getString(R.string.fs_body), 14f, getColor(R.color.ink2)),
            Ui.lp(Ui.MATCH, Ui.WRAP).apply { topMargin = dp(c, 6); bottomMargin = dp(c, 18) })
        sheet.addView(Ui.button(c, getString(R.string.fs_ok), getColor(R.color.accent), heightDp = 48).apply {
            setOnClickListener { d.dismiss() }
        }, Ui.lp(Ui.MATCH, dp(c, 48)))
        d.setContentView(sheet)
        d.window?.apply {
            setBackgroundDrawable(android.graphics.drawable.ColorDrawable(Color.TRANSPARENT))
            setLayout(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT)
            setGravity(Gravity.BOTTOM)
        }
        d.show()
    }

    // ── lifecycle ──
    @Deprecated("Framework Activity back handling; WebView history first, then finish.")
    override fun onBackPressed() {
        if (offline?.visibility == View.VISIBLE) { finish(); return }
        if (web.canGoBack()) web.goBack() else {
            @Suppress("DEPRECATION") super.onBackPressed()
        }
    }

    override fun onResume() {
        super.onResume()
        web.onResume()
        // A token refresh may have happened while we were away; register if one is waiting.
        if (Push.hasPendingRegistration(this)) withPageToken { t -> if (t != null) Push.register(this, t) }
    }

    override fun onPause() {
        web.onPause()
        CookieManager.getInstance().flush()
        super.onPause()
    }

    override fun onSaveInstanceState(outState: Bundle) {
        super.onSaveInstanceState(outState)
        web.saveState(outState)
    }

    override fun onDestroy() {
        netCallback?.let { try { getSystemService(ConnectivityManager::class.java).unregisterNetworkCallback(it) } catch (_: Exception) { } }
        if (current?.get() === this) current = null
        root.removeView(web)
        web.destroy()
        super.onDestroy()
    }

}
