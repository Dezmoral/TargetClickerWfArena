using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace TargetClicker;

internal sealed class MainForm : Form
{
    private const int HotkeyId = 0x4A31;
    private const int FallbackHotkeyId = 0x4A32;
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private const uint ModControl = 0x0002;
    private const uint KeyF8 = 0x77;
    private const uint InputMouse = 0;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const int DetectionRadius = 18;
    private const int ClickIntervalMs = 20;
    private const int ConfirmFrames = 1;
    private const int ClearFramesToRearm = 1;
    private const int TrackSampleStep = 3;
    private const int TrackTimerIntervalMs = 4;
    private const int TrackWindowWidth = 420;
    private const int TrackWindowHeight = 320;
    private const int FullScanIdleIntervalMs = 8;
    private const uint MouseMoveFlag = 0x0001;

    private readonly WebView2 webView = new();
    private readonly byte[] patchPixels = new byte[(DetectionRadius * 2 + 1) * (DetectionRadius * 2 + 1) * 4];
    private Rectangle scanArea;
    private bool hasScanArea;
    private volatile bool running;
    private volatile bool centerAimMode = true;
    private volatile bool trackingMode;
    private volatile bool autoShootMode;
    private bool targetArmed = true;
    private int detectedFrames;
    private int clearFrames;
    private int scanInProgress;
    private long lastStatusAt;
    private long lastClickAt;
    private IntPtr hotkeyWindow;
    private int registeredHotkeyId;
    private string hotkeyLabel = "F8";
    private string? hotkeyProblem;
    private Bitmap? captureBitmap;
    private Graphics? captureGraphics;
    private Bitmap? trackingBitmap;
    private Graphics? trackingGraphics;
    private byte[]? trackingPixels;
    private bool[]? trackingVisited;
    private int[]? trackingQueue;
    private int trackingGridWidth;
    private int trackingGridHeight;
    private Point? lastTrackedPoint;
    private int trackingLostFrames;
    private long lastFullScanAt;
    private bool trackShotArmed = true;
    private int trackingCenteredFrames;
    private long lastTrackingShotAt;
    private System.Threading.Timer? scanTimer;
    private bool timerResolutionActive;
    private bool uiReady;
    private string areaDisplay = "Не выбрана";
    private string statusText = "Выбери игровое поле, затем нажми F8.";

    public MainForm()
    {
        Text = "Aim Assist — тренировка";
        ClientSize = new Size(900, 690);
        MinimumSize = new Size(760, 600);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(11, 16, 27);
        webView.Dock = DockStyle.Fill;
        webView.DefaultBackgroundColor = BackColor;
        Controls.Add(webView);
        Shown += async (_, _) => await InitializeWebUiAsync();
    }

    private async Task InitializeWebUiAsync()
    {
        try
        {
            await webView.EnsureCoreWebView2Async();
            var settings = webView.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.IsStatusBarEnabled = false;
            webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            webView.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                uiReady = true;
                PushUiState();
            };
            webView.NavigateToString(UiHtml);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "Для современного интерфейса нужен Microsoft Edge WebView2 Runtime.\n\n" + ex.Message,
                "Не удалось запустить интерфейс", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            var action = root.TryGetProperty("action", out var actionValue) ? actionValue.GetString() : null;
            switch (action)
            {
                case "select-area":
                    SelectArea();
                    break;
                case "toggle":
                    Toggle();
                    break;
                case "setting":
                    ApplyWebSetting(root);
                    break;
            }
        }
        catch (JsonException)
        {
            SetStatus("Не удалось прочитать действие интерфейса.");
        }
    }

    private void ApplyWebSetting(JsonElement root)
    {
        var key = root.TryGetProperty("key", out var keyValue) ? keyValue.GetString() : null;
        var value = root.TryGetProperty("value", out var boolValue) && boolValue.ValueKind == JsonValueKind.True;
        switch (key)
        {
            case "centerAim":
                centerAimMode = value;
                break;
            case "tracking":
                trackingMode = value;
                lastTrackedPoint = null;
                trackingLostFrames = 0;
                trackShotArmed = true;
                trackingCenteredFrames = 0;
                if (!trackingMode)
                    autoShootMode = false;
                if (running)
                    scanTimer?.Change(0, trackingMode ? TrackTimerIntervalMs : 2);
                SetStatus(trackingMode ? "Режим слежения включён." : "Режим попаданий.");
                break;
            case "autoShoot":
                autoShootMode = value && trackingMode;
                break;
        }
        PushUiState();
    }

    private const string UiHtml = """
<!doctype html>
<html lang="ru">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <style>
    :root{color-scheme:dark;--bg:#0b1020;--panel:rgba(20,28,45,.86);--line:rgba(167,190,232,.13);--text:#f2f5fb;--muted:#98a7bf;--blue:#66a3ff;--mint:#52dfbc;--shadow:0 24px 70px rgba(0,0,0,.32)}
    *{box-sizing:border-box}body{margin:0;min-height:100vh;background:radial-gradient(900px 520px at 82% -12%,rgba(44,103,204,.26),transparent 62%),radial-gradient(600px 460px at -10% 100%,rgba(28,177,155,.12),transparent 66%),var(--bg);color:var(--text);font:14px/1.45 Inter,"Segoe UI",system-ui,sans-serif;display:grid;place-items:center;padding:26px}
    #app{width:min(100%,790px);display:grid;gap:18px}.top{display:flex;align-items:center;justify-content:space-between;padding:2px 2px 4px}.brand{display:flex;align-items:center;gap:13px}.logo{width:42px;height:42px;border-radius:14px;background:linear-gradient(145deg,#72b4ff,#3778f6 58%,#3fdbba);display:grid;place-items:center;color:#071425;font-size:18px;font-weight:900;box-shadow:0 8px 30px #438af748}.brand small{display:block;color:#91a3c0;font-size:10px;letter-spacing:.17em;font-weight:800}.brand strong{display:block;font-size:16px;letter-spacing:-.02em;margin-top:2px}.local{border:1px solid rgba(82,223,188,.25);background:rgba(82,223,188,.08);color:#8af1d7;border-radius:999px;padding:7px 11px;font-size:10px;letter-spacing:.12em;font-weight:800}
    .intro{padding:4px 2px 0}.intro h1{font-size:27px;letter-spacing:-.045em;margin:0 0 5px;font-weight:750}.intro p{margin:0;color:var(--muted);font-size:13px}
    .card{border:1px solid var(--line);background:linear-gradient(150deg,rgba(25,35,55,.94),rgba(16,23,38,.96));border-radius:18px;box-shadow:var(--shadow);padding:20px}.card-head{display:flex;justify-content:space-between;align-items:center;margin-bottom:15px}.eyebrow{color:#9eb0cc;font-size:10px;letter-spacing:.14em;font-weight:800}.area-row{display:flex;align-items:center;justify-content:space-between;gap:20px}.area-meta{display:flex;align-items:center;gap:12px;min-width:0}.area-icon{width:42px;height:42px;border-radius:13px;background:#263652;color:#86b7ff;display:grid;place-items:center;font-size:18px}.area-label{font-weight:650;font-size:14px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.area-help{color:#8e9db5;font-size:11px;margin-top:3px}.button{border:0;border-radius:12px;font:700 12px/1 Inter,"Segoe UI",sans-serif;cursor:pointer;transition:transform .15s,background .15s,box-shadow .15s}.button:hover{transform:translateY(-1px)}.button:active{transform:translateY(0)}.secondary{background:#27364d;color:#eaf1fc;padding:14px 17px;border:1px solid #3a4c69}.secondary:hover{background:#304360}
    .settings{display:grid;grid-template-columns:1fr 1fr;gap:12px}.setting{border:1px solid var(--line);border-radius:15px;background:rgba(17,25,41,.72);padding:15px;display:flex;gap:12px;align-items:flex-start;min-height:85px;cursor:pointer;transition:border .15s,background .15s}.setting:hover{border-color:rgba(102,163,255,.4);background:rgba(26,39,62,.9)}.setting.disabled{opacity:.42;cursor:not-allowed}.setting input{position:absolute;opacity:0;pointer-events:none}.switch{width:34px;height:20px;border-radius:999px;background:#39465c;flex:0 0 auto;padding:3px;transition:background .18s;margin-top:1px}.switch:after{content:"";display:block;width:14px;height:14px;border-radius:50%;background:#d9e2f1;transition:transform .18s}.setting.on .switch{background:#397ff4}.setting.on .switch:after{transform:translateX(14px);background:white}.setting strong{display:block;font-size:12px;font-weight:700}.setting span.desc{display:block;color:#91a0b8;font-size:10.5px;line-height:1.4;margin-top:4px}.wide{grid-column:1/-1;min-height:70px}
    .status{display:flex;align-items:center;gap:12px;border:1px solid var(--line);background:rgba(17,25,41,.62);border-radius:14px;padding:13px 15px;color:#a9b8cd;min-height:52px}.pulse{width:9px;height:9px;border-radius:50%;background:#70809a;box-shadow:0 0 0 5px rgba(112,128,154,.12);flex:none}.status.active .pulse{background:var(--mint);box-shadow:0 0 0 5px rgba(82,223,188,.13)}.status-text{white-space:nowrap;overflow:hidden;text-overflow:ellipsis;font-size:11px}
    .start{width:100%;padding:17px;background:linear-gradient(100deg,#4e91ff,#5f7cf7 58%,#685df2);color:white;font-size:13px;letter-spacing:.01em;box-shadow:0 12px 30px rgba(66,117,248,.25)}.start:hover{box-shadow:0 14px 34px rgba(66,117,248,.38)}.start.running{background:linear-gradient(100deg,#dc6472,#c94f67)}.foot{display:flex;justify-content:space-between;color:#75859e;font-size:10px;padding:0 2px}.key{border:1px solid rgba(166,186,218,.18);background:rgba(255,255,255,.04);padding:3px 7px;border-radius:6px;color:#c6d2e5;font-weight:700}
    @media(max-width:620px){body{padding:16px}.settings{grid-template-columns:1fr}.intro h1{font-size:23px}.area-row{align-items:flex-start;flex-direction:column}.secondary{width:100%}.wide{grid-column:auto}}
  </style>
</head>
<body><main id="app">
  <div class="top"><div class="brand"><div class="logo">A</div><div><small>LOCAL TRAINING TOOL</small><strong>Aim Assist</strong></div></div><div class="local">●  ЛОКАЛЬНЫЙ РЕЖИМ</div></div>
  <section class="intro"><h1>Тренировочная панель</h1><p>Выбери игровое поле и настрой поведение помощника.</p></section>
  <section class="card"><div class="card-head"><span class="eyebrow">ОБЛАСТЬ ИГРЫ</span><span class="eyebrow" id="areaBadge">НЕ ВЫБРАНА</span></div><div class="area-row"><div class="area-meta"><div class="area-icon">▧</div><div style="min-width:0"><div class="area-label" id="areaLabel">Не выбрана</div><div class="area-help">Для трекинга выдели игровое поле без HUD</div></div></div><button class="button secondary" onclick="send('select-area')">Выбрать область <span aria-hidden="true">↗</span></button></div></section>
  <section class="settings" aria-label="Настройки">
    <label class="setting" id="centerCard"><input id="centerAim" type="checkbox"><span class="switch"></span><span><strong>Центральный прицел</strong><span class="desc">Поле захвачено мышью, прицел находится по центру.</span></span></label>
    <label class="setting" id="trackingCard"><input id="tracking" type="checkbox"><span class="switch"></span><span><strong>Следить за шаром</strong><span class="desc">Автоматически переводить прицел на ближайшую белую цель.</span></span></label>
    <label class="setting wide" id="autoCard"><input id="autoShoot" type="checkbox"><span class="switch"></span><span><strong>Стрелять после наведения</strong><span class="desc">Кликнуть, когда прицел точно совмещён с найденным шаром.</span></span></label>
  </section>
  <div id="status" class="status"><i class="pulse"></i><span id="statusText" class="status-text">Выбери игровое поле, затем нажми F8.</span></div>
  <button id="start" class="button start" onclick="send('toggle')">Запустить помощника</button>
  <div class="foot"><span id="hotkeyText">Глобальная клавиша быстрого запуска</span><span><kbd id="hotkey" class="key">F8</kbd></span></div>
</main>
<script>
  const send=(action,extra={})=>window.chrome.webview.postMessage({action,...extra});
  const bind=(id,key)=>document.getElementById(id).addEventListener('change',e=>send('setting',{key,value:e.target.checked}));
  bind('centerAim','centerAim');bind('tracking','tracking');bind('autoShoot','autoShoot');
  window.syncState=s=>{
    const set=(id,value)=>{const el=document.getElementById(id);if(el)el.checked=!!value};
    set('centerAim',s.centerAim);set('tracking',s.tracking);set('autoShoot',s.autoShoot);
    document.getElementById('autoShoot').disabled=!s.tracking;
    document.getElementById('autoCard').classList.toggle('disabled',!s.tracking);
    document.getElementById('centerCard').classList.toggle('on',!!s.centerAim);
    document.getElementById('trackingCard').classList.toggle('on',!!s.tracking);
    document.getElementById('autoCard').classList.toggle('on',!!s.autoShoot);
    document.getElementById('areaLabel').textContent=s.area;
    document.getElementById('areaBadge').textContent=s.hasArea?'ГОТОВО':'НЕ ВЫБРАНА';
    document.getElementById('statusText').textContent=s.status;
    document.getElementById('status').classList.toggle('active',!!s.running);
    document.getElementById('start').classList.toggle('running',!!s.running);
    document.getElementById('start').textContent=s.running?'Остановить помощника':'Запустить помощника';
    document.getElementById('hotkey').textContent=s.hotkey;
    document.getElementById('hotkeyText').textContent=s.hotkeyProblem||'Глобальная клавиша быстрого запуска';
  };
</script></body></html>
""";

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        hotkeyWindow = Handle;
        if (RegisterHotKey(hotkeyWindow, HotkeyId, ModNoRepeat, KeyF8))
        {
            registeredHotkeyId = HotkeyId;
            hotkeyLabel = "F8";
            hotkeyProblem = null;
        }
        else if (RegisterHotKey(hotkeyWindow, FallbackHotkeyId, ModNoRepeat | ModControl, KeyF8))
        {
            registeredHotkeyId = FallbackHotkeyId;
            hotkeyLabel = "Ctrl+F8";
            hotkeyProblem = "F8 занята; используйте Ctrl+F8.";
        }
        else
        {
            registeredHotkeyId = 0;
            hotkeyLabel = "кнопка";
            hotkeyProblem = "Не удалось зарегистрировать F8 или Ctrl+F8. Запускайте кнопкой.";
        }
        SetStatus(hotkeyProblem ?? $"{hotkeyLabel} зарегистрирована. Выберите игровую область.");
        PushUiState();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (hotkeyWindow != IntPtr.Zero && registeredHotkeyId != 0)
            UnregisterHotKey(hotkeyWindow, registeredHotkeyId);
        hotkeyWindow = IntPtr.Zero;
        registeredHotkeyId = 0;
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && (m.WParam.ToInt32() == HotkeyId || m.WParam.ToInt32() == FallbackHotkeyId))
        {
            Toggle();
            return;
        }
        base.WndProc(ref m);
    }

    private void SelectArea()
    {
        if (running)
            Toggle();
        Hide();
        using var selector = new AreaSelector();
        if (selector.ShowDialog() == DialogResult.OK && selector.SelectedArea.Width >= 150 && selector.SelectedArea.Height >= 100)
        {
            scanArea = selector.SelectedArea;
            hasScanArea = true;
            lastTrackedPoint = null;
            trackingLostFrames = 0;
            trackShotArmed = true;
            trackingCenteredFrames = 0;
            lastTrackingShotAt = 0;
            areaDisplay = $"{scanArea.Width} × {scanArea.Height} px";
            SetStatus($"Наведи курсор на белый шар; {hotkeyLabel} включает и выключает клики.");
        }
        else
        {
            SetStatus("Область не выбрана или слишком мала.");
        }
        Show();
        Activate();
    }

    private void Toggle()
    {
        if (!running && !hasScanArea)
        {
            SetStatus("Сначала выберите игровую область.");
            return;
        }

        running = !running;
        if (running)
        {
            lastClickAt = 0;
            targetArmed = true;
            detectedFrames = 0;
            clearFrames = 0;
            timeBeginPeriod(1);
            timerResolutionActive = true;
            var period = trackingMode ? TrackTimerIntervalMs : 2;
            scanTimer = new System.Threading.Timer(_ => ScanTimerTick(), null, 0, period);
            SetStatus(trackingMode ? $"Слежение включено. {hotkeyLabel} — стоп." : $"Включено: наведи центр прицела на шар. {hotkeyLabel} — стоп.");
            UpdateControlText();
        }
        else
        {
            StopScanner();
            SetStatus("Остановлено.");
            UpdateControlText();
        }
    }

    private void ScanTimerTick()
    {
        if (!running || Interlocked.Exchange(ref scanInProgress, 1) != 0)
            return;
        try
        {
            ScanAndClick();
        }
        finally
        {
            Volatile.Write(ref scanInProgress, 0);
        }
    }

    private void StopScanner(bool waitForCallback = true)
    {
        running = false;
        scanTimer?.Dispose();
        scanTimer = null;
        if (waitForCallback)
            SpinWait.SpinUntil(() => Volatile.Read(ref scanInProgress) == 0, 500);
        if (timerResolutionActive)
        {
            timeEndPeriod(1);
            timerResolutionActive = false;
        }
    }

    private void ScanAndClick()
    {
        if (!running || scanArea.Width <= 0 || scanArea.Height <= 0)
            return;

        try
        {
            if (trackingMode)
            {
                TrackTarget();
                return;
            }

            Point aimScreen;
            if (centerAimMode)
            {
                aimScreen = new Point(scanArea.Left + scanArea.Width / 2, scanArea.Top + scanArea.Height / 2);
            }
            else
            {
                if (!GetCursorPos(out var cursor) ||
                    cursor.X < scanArea.Left || cursor.X >= scanArea.Right ||
                    cursor.Y < scanArea.Top || cursor.Y >= scanArea.Bottom)
                {
                    SetStatus("Курсор вне выбранной игровой области.");
                    return;
                }
                aimScreen = new Point(cursor.X, cursor.Y);
            }

            var captureRect = new Rectangle(aimScreen.X - DetectionRadius, aimScreen.Y - DetectionRadius,
                DetectionRadius * 2 + 1, DetectionRadius * 2 + 1);
            var screenBounds = SystemInformation.VirtualScreen;
            if (!screenBounds.Contains(captureRect) || !scanArea.Contains(captureRect))
                return;

            EnsureCaptureBuffer();
            captureGraphics!.CopyFromScreen(captureRect.Location, Point.Empty, captureRect.Size, CopyPixelOperation.SourceCopy);

            var localAim = new Point(aimScreen.X - captureRect.Left, aimScreen.Y - captureRect.Top);
            var overTarget = IsWhiteBallUnderCursor(captureBitmap!, localAim, out var whitePercent);
            if (!overTarget)
            {
                detectedFrames = 0;
                clearFrames++;
                if (clearFrames >= ClearFramesToRearm)
                    targetArmed = true;
                SetStatus($"Ищу яркий шар: {whitePercent}% белого. {hotkeyLabel} — стоп.");
                return;
            }

            clearFrames = 0;
            detectedFrames++;
            if (!targetArmed)
            {
                SetStatus("Клик по этому шару уже сделан. Жду смены цели.");
                return;
            }
            if (detectedFrames < ConfirmFrames)
            {
                SetStatus($"Проверяю шар: {whitePercent}% белого…");
                return;
            }

            var now = Environment.TickCount64;
            if (now - lastClickAt < ClickIntervalMs)
                return;

            lastClickAt = now;
            targetArmed = false;
            detectedFrames = 0;
            SendMouseClick();
            SetStatus("Шар подтверждён — один клик.");
        }
        catch (Exception ex)
        {
            StopScanner(waitForCallback: false);
            SetStatus("Остановлено: " + ex.Message);
            UpdateControlText();
        }
    }

    private void TrackTarget()
    {
        Point focusInArea;
        if (centerAimMode)
        {
            focusInArea = new Point(scanArea.Width / 2, scanArea.Height / 2);
        }
        else
        {
            if (!GetCursorPos(out var cursor) || cursor.X < scanArea.Left || cursor.X >= scanArea.Right ||
                cursor.Y < scanArea.Top || cursor.Y >= scanArea.Bottom)
            {
                SetStatus("Курсор вне выбранной области.");
                return;
            }
            focusInArea = new Point(cursor.X - scanArea.Left, cursor.Y - scanArea.Top);
        }

        var fullScan = lastTrackedPoint is null;
        var now = Environment.TickCount64;
        if (fullScan && now - lastFullScanAt < FullScanIdleIntervalMs)
            return;

        Rectangle captureRect;
        if (fullScan)
        {
            captureRect = scanArea;
            lastFullScanAt = now;
        }
        else
        {
            var previous = lastTrackedPoint!.Value;
            var centerX = scanArea.Left + previous.X;
            var centerY = scanArea.Top + previous.Y;
            var width = Math.Min(TrackWindowWidth, scanArea.Width);
            var height = Math.Min(TrackWindowHeight, scanArea.Height);
            var left = Math.Clamp(centerX - width / 2, scanArea.Left, scanArea.Right - width);
            var top = Math.Clamp(centerY - height / 2, scanArea.Top, scanArea.Bottom - height);
            captureRect = new Rectangle(left, top, width, height);
        }

        EnsureTrackingBuffer(captureRect.Width, captureRect.Height);
        trackingGraphics!.CopyFromScreen(captureRect.Location, Point.Empty, captureRect.Size, CopyPixelOperation.SourceCopy);
        var offset = new Point(captureRect.Left - scanArea.Left, captureRect.Top - scanArea.Top);
        var focusInCapture = new Point(focusInArea.X - offset.X, focusInArea.Y - offset.Y);
        Point? previousInCapture = lastTrackedPoint is null ? null : new Point(
            lastTrackedPoint.Value.X - offset.X, lastTrackedPoint.Value.Y - offset.Y);
        var target = FindTrackingTarget(trackingBitmap!, focusInCapture, previousInCapture);
        if (target is null)
        {
            trackingCenteredFrames = 0;
            trackingLostFrames++;
            lastTrackedPoint = null;
            trackShotArmed = true;
            lastFullScanAt = fullScan ? now : 0;
            SetStatus("Слежение: ищу белый шар…");
            return;
        }

        trackingLostFrames = 0;
        var point = new Point(target.Value.X + offset.X, target.Value.Y + offset.Y);
        lastTrackedPoint = point;
        var errorX = point.X - focusInArea.X;
        var errorY = point.Y - focusInArea.Y;
        var centered = errorX * errorX + errorY * errorY <= 25;
        if (centerAimMode)
        {
            var moveX = GetTrackingStep(errorX);
            var moveY = GetTrackingStep(errorY);
            if (moveX != 0 || moveY != 0)
                SendMouseMove(moveX, moveY);
        }
        else
        {
            SetCursorPos(scanArea.Left + point.X, scanArea.Top + point.Y);
        }

        if (!autoShootMode)
        {
            SetStatus("Слежу за белым шаром — стрельба выключена.");
            return;
        }

        if (!trackShotArmed)
        {
            SetStatus("Клик по этой цели уже сделан; жду следующую.");
            return;
        }

        trackingCenteredFrames = centered ? trackingCenteredFrames + 1 : 0;
        if (trackingCenteredFrames >= 2 && Environment.TickCount64 - lastTrackingShotAt >= 120)
        {
            SendMouseClick();
            trackShotArmed = false;
            lastTrackingShotAt = Environment.TickCount64;
            trackingCenteredFrames = 0;
            SetStatus("Прицел совмещён с шаром — клик.");
            return;
        }

        SetStatus(centered ? "Цель в центре — подтверждаю…" : "Слежу за белым шаром…");
    }

    private static int GetTrackingStep(int error)
    {
        var distance = Math.Abs(error);
        if (distance <= 2) return 0;
        var gain = distance > 180 ? 0.72 : distance > 50 ? 0.48 : 0.24;
        var limit = distance > 180 ? 80 : distance > 50 ? 36 : 12;
        return Math.Clamp((int)Math.Round(error * gain), -limit, limit);
    }

    private Point? FindTrackingTarget(Bitmap bitmap, Point focus, Point? previous)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = data.Stride;
            var byteCount = Math.Abs(stride) * height;
            if (trackingPixels is null || byteCount > trackingPixels.Length || trackingVisited is null || trackingQueue is null)
                throw new InvalidOperationException("Область слежения изменилась; выберите её заново.");
            Marshal.Copy(data.Scan0, trackingPixels, 0, byteCount);
            Array.Clear(trackingVisited);

            Point? best = null;
            var bestScore = double.MaxValue;
            var anchor = previous ?? focus;
            for (var gy = 0; gy < trackingGridHeight; gy++)
            for (var gx = 0; gx < trackingGridWidth; gx++)
            {
                var start = gy * trackingGridWidth + gx;
                if (trackingVisited[start] || !MatchesTrackWhite(trackingPixels, stride, gx * TrackSampleStep, gy * TrackSampleStep))
                    continue;

                var head = 0;
                var tail = 0;
                trackingQueue[tail++] = start;
                trackingVisited[start] = true;
                var count = 0;
                var minX = gx;
                var maxX = gx;
                var minY = gy;
                var maxY = gy;
                while (head < tail)
                {
                    var cell = trackingQueue[head++];
                    var x = cell % trackingGridWidth;
                    var y = cell / trackingGridWidth;
                    count++;
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                    for (var oy = -1; oy <= 1; oy++)
                    for (var ox = -1; ox <= 1; ox++)
                    {
                        if (ox == 0 && oy == 0) continue;
                        var nx = x + ox;
                        var ny = y + oy;
                        if (nx < 0 || ny < 0 || nx >= trackingGridWidth || ny >= trackingGridHeight) continue;
                        var next = ny * trackingGridWidth + nx;
                        if (trackingVisited[next] || !MatchesTrackWhite(trackingPixels, stride, nx * TrackSampleStep, ny * TrackSampleStep)) continue;
                        trackingVisited[next] = true;
                        trackingQueue[tail++] = next;
                    }
                }

                var boxWidth = maxX - minX + 1;
                var boxHeight = maxY - minY + 1;
                var ratio = boxWidth / (double)boxHeight;
                var diameter = Math.Max(boxWidth, boxHeight) * TrackSampleStep;
                var fill = count / (double)(boxWidth * boxHeight);
                if (count < 45 || diameter < 24 || diameter > 190 || ratio < 0.68 || ratio > 1.46 || fill < 0.24 || fill > 0.96)
                    continue;

                var center = new Point((minX + maxX + 1) * TrackSampleStep / 2,
                    (minY + maxY + 1) * TrackSampleStep / 2);
                var distance = Math.Sqrt(Math.Pow(center.X - anchor.X, 2) + Math.Pow(center.Y - anchor.Y, 2));
                var shapePenalty = Math.Abs(1.0 - ratio) * 32 + Math.Abs(0.62 - fill) * 22;
                var score = distance + shapePenalty;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = center;
                }
            }
            return best;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private void EnsureTrackingBuffer(int width, int height)
    {
        if (trackingBitmap is not null && trackingBitmap.Width == width && trackingBitmap.Height == height)
            return;
        trackingGraphics?.Dispose();
        trackingBitmap?.Dispose();
        trackingBitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        trackingGraphics = Graphics.FromImage(trackingBitmap);
        trackingPixels = new byte[width * height * 4];
        trackingGridWidth = (width + TrackSampleStep - 1) / TrackSampleStep;
        trackingGridHeight = (height + TrackSampleStep - 1) / TrackSampleStep;
        trackingVisited = new bool[trackingGridWidth * trackingGridHeight];
        trackingQueue = new int[trackingVisited.Length];
    }

    private static bool MatchesTrackWhite(byte[] pixels, int stride, int x, int y)
    {
        var index = y * stride + x * 4;
        var blue = pixels[index];
        var green = pixels[index + 1];
        var red = pixels[index + 2];
        var min = Math.Min(red, Math.Min(green, blue));
        var max = Math.Max(red, Math.Max(green, blue));
        return min >= 135 && max - min <= 90;
    }

    private bool IsWhiteBallUnderCursor(Bitmap bitmap, Point cursor, out int whitePercent)
    {
        // Only inspect the small patch around the actual pointer. This avoids
        // mistaking distant white UI elements for targets and reacts quickly.
        const int radius = DetectionRadius;
        const int step = 2;
        var width = bitmap.Width;
        var height = bitmap.Height;
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = data.Stride;
            var byteCount = Math.Abs(stride) * height;
            if (byteCount > patchPixels.Length)
                throw new InvalidOperationException("Область распознавания неожиданно изменила размер.");
            Marshal.Copy(data.Scan0, patchPixels, 0, byteCount);

            var total = 0;
            var upperLeft = 0;
            var upperRight = 0;
            var lowerLeft = 0;
            var lowerRight = 0;
            var samples = 0;
            for (var y = -radius; y <= radius; y += step)
            for (var x = -radius; x <= radius; x += step)
            {
                var px = cursor.X + x;
                var py = cursor.Y + y;
                if (px < 0 || py < 0 || px >= width || py >= height)
                    continue;
                samples++;
                if (MatchesWhite(patchPixels, stride, px, py))
                {
                    total++;
                    if (x < 0 && y < 0) upperLeft++;
                    else if (x >= 0 && y < 0) upperRight++;
                    else if (x < 0) lowerLeft++;
                    else lowerRight++;
                }
            }

            whitePercent = samples == 0 ? 0 : total * 100 / samples;
            var quadrantMin = Math.Max(10, samples / 24);
            return total >= samples * 0.40 && upperLeft >= quadrantMin && upperRight >= quadrantMin &&
                   lowerLeft >= quadrantMin && lowerRight >= quadrantMin;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static bool MatchesWhite(byte[] pixels, int stride, int x, int y)
    {
        var index = y * stride + x * 4;
        var blue = pixels[index];
        var green = pixels[index + 1];
        var red = pixels[index + 2];
        var min = Math.Min(red, Math.Min(green, blue));
        var max = Math.Max(red, Math.Max(green, blue));
        return min >= 135 && max - min <= 90;
    }

    private void EnsureCaptureBuffer()
    {
        if (captureBitmap is not null)
            return;
        var size = DetectionRadius * 2 + 1;
        captureBitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        captureGraphics = Graphics.FromImage(captureBitmap);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        StopScanner();
        captureGraphics?.Dispose();
        captureBitmap?.Dispose();
        trackingGraphics?.Dispose();
        trackingBitmap?.Dispose();
        base.OnFormClosed(e);
    }

    private void SendMouseClick()
    {
        var inputs = new[]
        {
            new NativeInput { Type = InputMouse, Data = new NativeInputUnion { Mouse = new NativeMouseInput { Flags = MouseLeftDown } } },
            new NativeInput { Type = InputMouse, Data = new NativeInputUnion { Mouse = new NativeMouseInput { Flags = MouseLeftUp } } }
        };
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()) != (uint)inputs.Length)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Не удалось отправить клик");
    }

    private void SendMouseMove(int dx, int dy)
    {
        var input = new[]
        {
            new NativeInput
            {
                Type = InputMouse,
                Data = new NativeInputUnion { Mouse = new NativeMouseInput { Dx = dx, Dy = dy, Flags = MouseMoveFlag } }
            }
        };
        if (SendInput(1, input, Marshal.SizeOf<NativeInput>()) != 1)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Не удалось переместить прицел");
    }

    private void SetStatus(string message)
    {
        statusText = message;
        if (InvokeRequired)
        {
            var now = Environment.TickCount64;
            var urgent = message.Contains("клик", StringComparison.OrdinalIgnoreCase) ||
                         message.StartsWith("Остановлено", StringComparison.OrdinalIgnoreCase);
            var previous = Interlocked.Read(ref lastStatusAt);
            if (!urgent && now - previous < 150)
                return;
            Interlocked.Exchange(ref lastStatusAt, now);
            try { BeginInvoke((Action)PushUiState); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
            return;
        }
        Interlocked.Exchange(ref lastStatusAt, Environment.TickCount64);
        PushUiState();
    }

    private void UpdateControlText()
    {
        PushUiState();
    }

    private void PushUiState()
    {
        if (InvokeRequired)
        {
            try { BeginInvoke((Action)PushUiState); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
            return;
        }
        if (!uiReady || webView.CoreWebView2 is null || IsDisposed)
            return;
        var state = new
        {
            area = areaDisplay,
            hasArea = hasScanArea,
            status = statusText,
            running,
            centerAim = centerAimMode,
            tracking = trackingMode,
            autoShoot = autoShootMode,
            hotkey = hotkeyLabel,
            hotkeyProblem
        };
        var json = JsonSerializer.Serialize(state);
        _ = webView.CoreWebView2.ExecuteScriptAsync($"window.syncState({json});");
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, NativeInput[] inputs, int inputSize);
    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public NativeInputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct NativeInputUnion
    {
        [FieldOffset(0)] public NativeMouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }
}

internal sealed class AreaSelector : Form
{
    private Point dragStart;
    private Point dragEnd;
    private bool dragging;

    public Rectangle SelectedArea { get; private set; }

    public AreaSelector()
    {
        FormBorderStyle = FormBorderStyle.None;
        Bounds = SystemInformation.VirtualScreen;
        TopMost = true;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        BackColor = Color.Black;
        Opacity = 0.42;
        Cursor = Cursors.Cross;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel; };
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        dragStart = PointToScreen(e.Location);
        dragEnd = dragStart;
        dragging = true;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!dragging) return;
        dragEnd = PointToScreen(e.Location);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!dragging || e.Button != MouseButtons.Left) return;
        dragging = false;
        Capture = false;
        dragEnd = PointToScreen(e.Location);
        SelectedArea = Rectangle.FromLTRB(
            Math.Min(dragStart.X, dragEnd.X), Math.Min(dragStart.Y, dragEnd.Y),
            Math.Max(dragStart.X, dragEnd.X), Math.Max(dragStart.Y, dragEnd.Y));
        DialogResult = DialogResult.OK;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.DrawString("Протяните мышью по игровому полю. Esc — отмена.",
            new Font("Segoe UI", 16, FontStyle.Bold), Brushes.White, 24, 24);
        if (!dragging) return;
        var a = PointToClient(dragStart);
        var b = PointToClient(dragEnd);
        var rect = Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        using var pen = new Pen(Color.Lime, 3);
        e.Graphics.DrawRectangle(pen, rect);
    }
}
