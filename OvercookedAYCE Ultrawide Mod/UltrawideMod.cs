using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(AyceUltrawide.UltrawideMod), "AYCE Ultrawide Fix", "3.13.0", "fiodo")]
[assembly: MelonGame("Team17", "Overcooked All You Can Eat")]

namespace AyceUltrawide;

/// <summary>
/// Holds Overcooked! All You Can Eat at 5120x1440 on a 32:9 display.
///
/// Everything before 3.x was REACTIVE: let the game apply its own resolution, then set it
/// back. That guarantees a visible snap every single time, which is the flicker. It also
/// cannot be won at the settings layer -- the game rewrites its own
/// "Screenmanager Resolution Use Native" back to 1 on exit, so it re-picks 3840x2160 (16:9,
/// and taller than the screen) on every launch.
///
/// So 3.x is PREVENTIVE: a Harmony prefix on Screen.SetResolution rewrites the request before
/// Unity ever sees it. The wrong resolution is never applied, so there is nothing to snap back
/// from.
///
/// 3.10.0 (29 September 2026): hover icons: HoverIconCanvas stretched for shape, each icon placed at its object's true x (see
/// FixHoverIcons). 3.5.0 hooked game methods and broke pickup; 3.6.0 retargeted the whole UI to
/// 32:9 and made every HUD element huge. Both approaches are gone. Switch: FixHoverIconAspect.
/// </summary>
public class UltrawideMod : MelonMod
{
    private static int _targetW = 5120;
    private static int _targetH = 1440;
    private static bool _on = true;
    private static bool _chatty = true;
    private static bool _wantBorderless = true;
    private static bool _skipHudCamera = true;
    private static bool _resetAspectOn = true;
    private static MelonLogger.Instance _log;
    private static int _intercepted;

    private MelonPreferences_Category _cfg;
    private MelonPreferences_Entry<bool> _enabled;
    private MelonPreferences_Entry<int> _width;
    private MelonPreferences_Entry<int> _height;
    private MelonPreferences_Entry<bool> _borderless;
    private MelonPreferences_Entry<bool> _forceCameraRect;
    private MelonPreferences_Entry<bool> _verbose;
    private MelonPreferences_Entry<bool> _skipHud;
    private MelonPreferences_Entry<bool> _resetAspect;
    private MelonPreferences_Entry<bool> _fixHover;
    private float _nextUiCheck;

    private float _sweepUntil;
    private float _nextSweep;
    private float _nextGuard;
    private bool _patched;
    private bool _loggedHudSkip;

    public override void OnInitializeMelon()
    {
        _cfg             = MelonPreferences.CreateCategory("AYCEUltrawideFix");
        _enabled         = _cfg.CreateEntry("Enabled", true);
        _width           = _cfg.CreateEntry("TargetWidth", 5120);
        _height          = _cfg.CreateEntry("TargetHeight", 1440);
        _borderless      = _cfg.CreateEntry("UseBorderless", true);
        _forceCameraRect = _cfg.CreateEntry("ForceCameraRect", true);
        _verbose         = _cfg.CreateEntry("Verbose", true);
        // The game letterboxes its HUD camera to the centre 16:9 on purpose (observed rect
        // x=0.25 w=0.50). Forcing that one full-screen stretches every icon. Set false to go
        // back to v1 behaviour of widening it too, without needing a rebuild.
        // Now false: the cooking icons live on HoverIconCanvas, which renders in
        // ScreenSpaceCamera mode THROUGH UICamera. Leaving that camera at the game's centre
        // 50% viewport is precisely what puts the icons in the wrong place. Set true to go
        // back to leaving the HUD camera alone.
        _skipHud         = _cfg.CreateEntry("SkipHudCamera", false);
        // Forcing the rect wide without this leaves the camera's aspect pinned at 16:9, so the
        // projection stays 16:9 and simply stretches -- which is why v1 widened the HUD camera
        // and the icons were still wrong. Both are needed together. Since 3.5.0 this also
        // switches the Camera.set_aspect rewrite and the game-method postfixes.
        _resetAspect     = _cfg.CreateEntry("ResetCameraAspect", true);

        _fixHover        = _cfg.CreateEntry("FixHoverIconAspect", true);

        _log            = LoggerInstance;
        _targetW        = _width.Value;
        _targetH        = _height.Value;
        _on             = _enabled.Value;
        _chatty         = _verbose.Value;
        _wantBorderless = _borderless.Value;
        _skipHudCamera  = _skipHud.Value;
        _resetAspectOn  = _resetAspect.Value;
        _hoverOn        = _fixHover.Value;

        PatchSetResolution();
        if (_hoverOn) HookWillRender();
        ApplyResolution();
    }

    /// Patches every SetResolution overload. Harmony binds prefix parameters by NAME, and the
    /// overloads differ -- some take `FullScreenMode fullscreenMode`, some take `bool
    /// fullscreen`. Rewriting width/height alone left the game's "windowed" request intact,
    /// which is what made it flip between windowed and fullscreen, so the mode argument has to
    /// be rewritten too and that needs a prefix per shape.
    private void PatchSetResolution()
    {
        HarmonyMethod Pre(string name) => new HarmonyMethod(typeof(UltrawideMod).GetMethod(
            name, BindingFlags.Public | BindingFlags.Static));

        int ok = 0, failed = 0;
        foreach (var m in typeof(Screen).GetMethods(BindingFlags.Public | BindingFlags.Static)
                                        .Where(m => m.Name == "SetResolution"))
        {
            var ps = m.GetParameters();
            HarmonyMethod prefix;
            if (ps.Any(p => p.ParameterType == typeof(FullScreenMode))) prefix = Pre(nameof(PrefixMode));
            else if (ps.Any(p => p.ParameterType == typeof(bool)))       prefix = Pre(nameof(PrefixBool));
            else                                                        prefix = Pre(nameof(SetResolutionPrefix));

            try
            {
                HarmonyInstance.Patch(m, prefix);
                ok++;
                if (_chatty) LoggerInstance.Msg($"patched {m}  via {prefix.method.Name}");
            }
            catch (Exception e)
            {
                failed++;
                LoggerInstance.Warning($"could not patch {m}: {e.Message}");
            }
        }

        // SetResolution is not the only way in. Unity also exposes the mode as plain
        // properties, and a game that flips to windowed through those never touches
        // SetResolution at all -- which is why patching only that still left it switching.
        foreach (var pair in new[] { ("set_fullScreen", nameof(PrefixSetFullScreen)),
                                     ("set_fullScreenMode", nameof(PrefixSetFullScreenMode)) })
        {
            var m = typeof(Screen).GetMethod(pair.Item1, BindingFlags.Public | BindingFlags.Static);
            if (m == null) { LoggerInstance.Warning($"{pair.Item1} not found"); failed++; continue; }
            try
            {
                HarmonyInstance.Patch(m, Pre(pair.Item2));
                ok++;
                if (_chatty) LoggerInstance.Msg($"patched {pair.Item1} via {pair.Item2}");
            }
            catch (Exception e) { failed++; LoggerInstance.Warning($"could not patch {pair.Item1}: {e.Message}"); }
        }

        _patched = ok > 0;
        LoggerInstance.Msg($"SetResolution overloads patched: {ok} ok, {failed} failed. " +
                           (_patched ? "interception active" : "INTERCEPTION UNAVAILABLE - falling back to correcting after the fact"));
    }

    public static void SetResolutionPrefix(ref int width, ref int height)
    {
        if (!_on) return;
        if (width == _targetW && height == _targetH) return;

        _intercepted++;
        if (_chatty && _intercepted <= 20)
            _log?.Msg($"intercepted SetResolution({width}x{height}) -> {_targetW}x{_targetH}");

        width = _targetW;
        height = _targetH;
    }

    public static void PrefixMode(ref int width, ref int height, ref FullScreenMode fullscreenMode)
    {
        SetResolutionPrefix(ref width, ref height);
        if (!_on) return;
        var want = _wantBorderless ? FullScreenMode.FullScreenWindow : FullScreenMode.ExclusiveFullScreen;
        if (fullscreenMode != want)
        {
            if (_chatty && _intercepted <= 20) _log?.Msg($"  ...and mode {fullscreenMode} -> {want}");
            fullscreenMode = want;
        }
    }

    public static void PrefixBool(ref int width, ref int height, ref bool fullscreen)
    {
        SetResolutionPrefix(ref width, ref height);
        if (!_on) return;
        if (!fullscreen)
        {
            if (_chatty && _intercepted <= 20) _log?.Msg("  ...and fullscreen false -> true");
            fullscreen = true;
        }
    }

    private static int _modeBlocked;

    public static void PrefixSetFullScreen(ref bool value)
    {
        if (!_on || value) return;
        _modeBlocked++;
        if (_chatty && _modeBlocked <= 20) _log?.Msg("blocked Screen.fullScreen = false");
        value = true;
    }

    public static void PrefixSetFullScreenMode(ref FullScreenMode value)
    {
        if (!_on) return;
        var want = _wantBorderless ? FullScreenMode.FullScreenWindow : FullScreenMode.ExclusiveFullScreen;
        if (value == want) return;
        _modeBlocked++;
        if (_chatty && _modeBlocked <= 20) _log?.Msg($"blocked Screen.fullScreenMode = {value} -> {want}");
        value = want;
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        if (!_enabled.Value) return;
        _sweepUntil = Time.realtimeSinceStartup + 8f;
        _nextSweep = 0f;
        _dumped = false;
        _hoverDumped = false;
        _probes = 0;
        _nextUiCheck = 0f;
        ApplyResolution();
    }

    /// The hover icons (the rings and prompts above counters and on the floor) live on
    /// 'HoverIconCanvas', a 16:9 box drawn over a 32:9 world. Somewhere in their hierarchy the
    /// game applies a horizontal scale of 16:9 / screen aspect (0.5 here) to keep them "16:9",
    /// which squeezes them to half width. Undo exactly that factor on exactly those transforms:
    /// only where localScale.x / localScale.y equals it, only under HoverIconCanvas, applied just
    /// before canvases render so the game's own LateUpdate cannot win. Nothing else is touched.
    private static readonly List<Canvas> _hoverCanvases = new();
    private static bool _hoverOn = true;
    private static int _hoverFixes;
    private static bool _hoverDumped;
    private static bool _hoverHooked;

    private void FindHoverCanvases()
    {
        _hoverCanvases.Clear();
        var all = UnityEngine.Object.FindObjectsOfType<Canvas>();
        if (all == null) return;
        foreach (var c in all)
            if (c != null && c.name == "HoverIconCanvas") _hoverCanvases.Add(c);
    }

    private void HookWillRender()
    {
        try
        {
            Canvas.willRenderCanvases += Il2CppInterop.Runtime.DelegateSupport
                .ConvertDelegate<Canvas.WillRenderCanvases>(new Action(FixHoverIcons));
            _hoverHooked = true;
            LoggerInstance.Msg("hover-icon fix runs in Canvas.willRenderCanvases");
        }
        catch (Exception e)
        {
            LoggerInstance.Warning($"willRenderCanvases unavailable ({e.Message}); hover-icon fix runs in OnLateUpdate");
        }
    }

    public override void OnLateUpdate()
    {
        if (!_hoverHooked) FixHoverIcons();
    }

    private static void FixHoverIcons()
    {
        if (!_on || !_hoverOn || _hoverCanvases.Count == 0) return;
        try
        {
            foreach (var c in _hoverCanvases)
            {
                if (c == null) continue;
                var rts = c.GetComponentsInChildren<RectTransform>(false);
                if (rts == null) continue;
                if (!_hoverDumped && rts.Length > 3) { _hoverDumped = true; DumpHover(rts); }
                // The canvas is a centred 16:9 box on a 32:9 screen, and the game places and sizes
                // every icon inside it as if it spanned the screen: positions are pulled toward the
                // centre and widths halved (3.8.0 widened icons 1.5x and they were still narrow and
                // away from the pots). Stretch this one canvas horizontally about its centre by
                // screen aspect / box aspect (2.0 here): positions and widths are corrected together.
                var crt = c.GetComponent<RectTransform>();
                if (crt == null) continue;
                var box = crt.rect;
                if (box.width < 1f || box.height < 1f) continue;
                float k = ((float)Screen.width / Mathf.Max(1, Screen.height)) / (box.width / box.height);
                if (k < 1.02f) continue;
                var cs = crt.localScale;
                if (Mathf.Abs(cs.x - k * cs.y) >= 0.001f)
                {
                    crt.localScale = new Vector3(k * cs.y, cs.y, cs.z);
                    _hoverFixes++;
                    if (_chatty && _hoverFixes <= 5)
                        _log?.Msg($"HoverIconCanvas {box.width:F1}x{box.height:F1} pivot {crt.pivot} stretched x{k:F3}, localScale {cs} -> {crt.localScale}");
                }

                // 3.9.0 showed the stretch fixes the SHAPE only: the game places each icon itself,
                // at half the true distance from the centre (icon spacing was half the pot
                // spacing, and the stretch just shifted every icon left by the same amount).
                // So place it here: horizontal position = where the followed object really is on
                // screen through the main camera; vertical position stays the game's. On a
                // screen-space overlay canvas, world position is screen pixels.
                ProbeIcons(c);
                AlignIcons(c);
            }
        }
        catch (Exception e)
        {
            _hoverOn = false;
            _log?.Warning($"hover-icon fix disabled: {e.GetType().Name}: {e.Message}");
        }
    }

    private static int _placed;

    /// 3.12.0: the probe (3.11.0) showed GetScreenPos is right (it equals the main camera's view
    /// of the object) but the icon's root stays in the canvas corner and the visible ring is drawn
    /// through its children, so neither a position write (3.10.0) nor a formula can be trusted.
    /// Feedback instead: after the game has laid the icon out, measure where it is DRAWN (all its
    /// rects' world corners) and move the root sideways so the drawn centre sits on the object.
    /// Recomputed from the fresh layout every frame, so it cannot accumulate. Height untouched.
    private static readonly Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3> _wc = new(4);

    private static void AlignIcons(Canvas c)
    {
        var cam = Camera.main;
        if (cam == null) return;
        var ctls = c.GetComponentsInChildren<Il2Cpp.HoverIconUIController>(false);
        if (ctls == null) return;
        foreach (var ctl in ctls)
        {
            if (ctl == null) continue;
            var ft = ctl.m_followTransform;
            if (ft == null) continue;
            var sp = cam.WorldToScreenPoint(ft.position + ctl.m_followOffset);
            if (sp.z <= 0f) continue;

            // 3.12.0 measured the drawn extent as -35..35 every frame: at this point the icon root
            // sits at world x 0 and the game adds its own placement AFTER this callback. That
            // placement is x = k * object x with k = (16/9) / screen aspect (0.5 on 32:9),
            // measured against the pots in his 3.12.0 screenshot (3.12.0 added the full object x
            // and icons landed at ~1.5x). So the root gets the missing (1 - k) share, absolute,
            // never accumulated.
            float k = (16f / 9f) / ((float)Screen.width / Mathf.Max(1, Screen.height));
            if (k > 0.98f) continue;
            var t = ctl.transform;
            float want = (1f - k) * sp.x;
            float dx = want - t.position.x;
            if (Mathf.Abs(dx) < 0.5f) continue;
            t.position = new Vector3(want, t.position.y, t.position.z);
            _placed++;
            if (_chatty && _placed <= 8)
                _log?.Msg($"align '{t.name}' on '{ft.name}': object x {sp.x:F0}, root x {t.position.x - dx:F0} -> {want:F0} (k {k:F3})");
        }
    }
    private static float _nextProbe;
    private static int _probes;

    /// Measurement only (3.11.0): v3.10.0 wrote rt.position and the game re-drove it every frame
    /// through RectTransformExtension anchors (reads were always 0,0,0), which put icons right of
    /// their objects. Nothing is written here. Every 2 s, up to 8 times per scene, log for 3 icons
    /// what the game asks for (GetScreenPos, anchors, offsets), where the icon really is (world
    /// corners), and where each camera sees the followed object - so the next fix is computed,
    /// not guessed.
    private static void ProbeIcons(Canvas c)
    {
        float now = Time.realtimeSinceStartup;
        if (!_chatty || _probes >= 8 || now < _nextProbe) return;
        var ctls = c.GetComponentsInChildren<Il2Cpp.HoverIconUIController>(false);
        if (ctls == null || ctls.Length == 0) return;
        _nextProbe = now + 2f;
        _probes++;
        var crt = c.GetComponent<RectTransform>();
        var cornersArr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
        crt.GetWorldCorners(cornersArr);
        _log?.Msg($"--- ICON PROBE {_probes}: canvas '{c.name}' rect {crt.rect} scale {crt.localScale} corners x {cornersArr[0].x:F0}..{cornersArr[2].x:F0} y {cornersArr[0].y:F0}..{cornersArr[2].y:F0}, screen {Screen.width}x{Screen.height}");
        int n = 0;
        foreach (var ctl in ctls)
        {
            if (ctl == null) continue;
            if (++n > 8) break;
            try
            {
                var rt = ctl.GetComponent<RectTransform>();
                var ft = ctl.m_followTransform;
                var wc = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
                rt.GetWorldCorners(wc);
                string gsp; try { gsp = ctl.GetScreenPos().ToString(); } catch (Exception e) { gsp = e.GetType().Name; }
                _log?.Msg($"  icon {n} on '{(ft != null ? ft.name : "-")}': GetScreenPos {gsp}, drawn centre ({(wc[0].x + wc[2].x) / 2f:F0}, {(wc[0].y + wc[2].y) / 2f:F0}), " +
                          $"anchors {rt.anchorMin}..{rt.anchorMax} anchoredPos {rt.anchoredPosition} pivot {rt.pivot} localPos {rt.localPosition}, ctl anchorOffset {ctl.m_anchorOffset} followOffset {ctl.m_followOffset}");
                var rte = ctl.GetComponent<Il2Cpp.RectTransformExtension>();
                if (rte != null)
                    _log?.Msg($"    RTE behaviour {rte.m_scalingBehaviour} fixedAnchors {rte.m_fixedRatioAnchorMin}..{rte.m_fixedRatioAnchorMax} AnchorOffset {rte.AnchorOffset} PixelOffset {rte.PixelOffset} scale {rte.m_scale}");
                if (ft != null)
                {
                    var wp = ft.position + ctl.m_followOffset;
                    foreach (var cam in Camera.allCameras)
                    {
                        if (cam == null) continue;
                        var tt = cam.targetTexture;
                        _log?.Msg($"    cam '{cam.name}' sees it at {cam.WorldToScreenPoint(wp)} (aspect {cam.aspect:F3}, rect {cam.rect}, pixels {cam.pixelWidth}x{cam.pixelHeight}, target {(tt != null ? tt.width + "x" + tt.height : "screen")}, main {cam == Camera.main})");
                    }
                }
            }
            catch (Exception e) { _log?.Warning($"  icon {n} probe failed: {e.GetType().Name}: {e.Message}"); }
        }
    }

    /// Evidence for the next step if placement is still off: where each icon is versus where
    /// its followed object projects through the main camera.
    private static void LogIconPositions(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<RectTransform> rts)
    {
        var cam = Camera.main;
        int n = 0;
        foreach (var rt in rts)
        {
            if (rt == null || !rt.name.StartsWith("TutorialIconUI")) continue;
            if (++n > 4) break;
            string target = "-";
            try
            {
                var ctl = rt.GetComponent<Il2Cpp.HoverIconUIController>();
                var ft = ctl != null ? ctl.m_followTransform : null;
                if (ft != null && cam != null)
                    target = $"'{ft.name}' projects to {cam.WorldToScreenPoint(ft.position)} via '{cam.name}' (rect {cam.rect}, aspect {cam.aspect:F3})";
            }
            catch (Exception e) { target = e.GetType().Name; }
            _log?.Msg($"  icon at {rt.position}, follow {target}");
        }
    }

    /// One-off: where the squash actually lives, if the scale rule above does not catch it.
    private static void DumpHover(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<RectTransform> rts)
    {
        _log?.Msg($"--- HOVER ICON DUMP ({rts.Length} transforms, screen {Screen.width}x{Screen.height})");
        int n = 0;
        foreach (var rt in rts)
        {
            if (rt == null) continue;
            if (++n > 40) break;
            string comps = "";
            try { foreach (var co in rt.GetComponents<Component>()) if (co != null) comps += co.GetIl2CppType().Name + " "; } catch { }
            string parent = rt.parent != null ? rt.parent.name : "-";
            _log?.Msg($"  '{rt.name}' <- '{parent}' size {rt.rect.width:F1}x{rt.rect.height:F1} local {rt.localScale} lossy {rt.lossyScale} [{comps.Trim()}]");
        }
    }

    private bool _dumped;

    /// The cooking icons sit on world-space canvases, which are positioned by projecting
    /// through whatever camera the canvas points at. If that camera's viewport does not match
    /// the one the world is drawn with, the icons land off-target and distorted. Log which
    /// camera each canvas actually uses before changing anything.
    private void DumpCanvases()
    {
        var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
        if (canvases == null || canvases.Length == 0) return;

        LoggerInstance.Msg($"--- CANVAS DUMP ({canvases.Length} canvases)");
        foreach (var c in canvases)
        {
            if (c == null) continue;
            string cam;
            try { cam = c.worldCamera != null ? $"{c.worldCamera.name} rect={c.worldCamera.rect} aspect={c.worldCamera.aspect:F3}" : "<none>"; }
            catch { cam = "<error>"; }

            string scale = "";
            try { var rt = c.GetComponent<RectTransform>(); if (rt != null) scale = $" rect={rt.rect.width}x{rt.rect.height} scale={rt.localScale}"; }
            catch { }

            LoggerInstance.Msg($"  '{c.name}' mode={c.renderMode} sortOrder={c.sortingOrder} camera={cam}{scale}");
        }
    }

    public override void OnUpdate()
    {
        if (!_enabled.Value) return;
        var now = Time.realtimeSinceStartup;

        // Fallback only. With interception working the window is never wrong, so this never
        // fires; if the patch failed it still corrects, just visibly.
        if (now >= _nextGuard)
        {
            _nextGuard = now + 1f;
            if (TryGetRealSize(out int cw, out int ch) && (cw != _targetW || ch != _targetH))
            {
                if (_chatty) LoggerInstance.Msg($"guard: real window {cw}x{ch} -> correcting");
                ApplyResolution();
            }
        }

        if (now >= _nextUiCheck)
        {
            _nextUiCheck = now + 1f;
            try { FindHoverCanvases(); }
            catch (Exception e) { LoggerInstance.Warning($"hover canvas lookup failed: {e.Message}"); }
        }

        if (now > _sweepUntil || now < _nextSweep) return;
        _nextSweep = now + 0.25f;

        if (!_dumped)
        {
            _dumped = true;
            try { DumpCanvases(); } catch (Exception e) { LoggerInstance.Warning($"canvas dump failed: {e.Message}"); }
        }

        try { FixCameras(); }
        catch (Exception e) { LoggerInstance.Warning($"camera sweep failed: {e.Message}"); }
    }

    private void ApplyResolution()
    {
        var mode = _borderless.Value ? FullScreenMode.FullScreenWindow : FullScreenMode.ExclusiveFullScreen;
        Screen.SetResolution(_targetW, _targetH, mode);
    }

    /// Widen every camera EXCEPT the HUD one. A cullingMask of 0 means the camera renders no
    /// layers at all -- those are the screen fillers (FullscreenAspectCamera, BackCamera) and
    /// they must still be widened or loading screens stop covering the display. Only a camera
    /// that renders the UI layer and nothing else is the HUD camera the game letterboxes.
    private static bool IsHudCamera(Camera cam)
    {
        int mask = cam.cullingMask;
        if (mask == 0) return false;
        return (mask & ~(1 << 5)) == 0;          // layer 5 = builtin UI
    }

    private void FixCameras()
    {
        if (!_forceCameraRect.Value) return;
        var cams = Camera.allCameras;
        if (cams == null) return;

        foreach (var cam in cams)
        {
            if (cam == null) continue;
            if (_skipHudCamera && IsHudCamera(cam))
            {
                if (_chatty && !_loggedHudSkip)
                {
                    _loggedHudSkip = true;
                    LoggerInstance.Msg($"HUD camera '{cam.name}' (mask 0x{cam.cullingMask:x}, rect {cam.rect}) left as the game set it");
                }
                continue;
            }
            var r = cam.rect;
            if (Mathf.Abs(r.x) > 0.0001f || Mathf.Abs(r.y) > 0.0001f ||
                Mathf.Abs(r.width - 1f) > 0.0001f || Mathf.Abs(r.height - 1f) > 0.0001f)
            {
                cam.rect = new Rect(0f, 0f, 1f, 1f);
                if (_chatty) LoggerInstance.Msg($"camera '{cam.name}' rect -> full");
            }

            if (_resetAspectOn)
            {
                float screenAspect = (float)Screen.width / Mathf.Max(1, Screen.height);
                if (Mathf.Abs(cam.aspect - screenAspect) > 0.01f)
                {
                    cam.ResetAspect();
                    if (_chatty) LoggerInstance.Msg($"camera '{cam.name}' aspect {cam.aspect:F3} (was pinned) -> screen {screenAspect:F3}");
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT r);

    // Unity's Screen.width reports the last size that was REQUESTED, not the real window, so
    // the guard has to measure the window itself or it never notices anything is wrong.
    private static bool TryGetRealSize(out int width, out int height)
    {
        width = height = 0;
        var hwnd = GetActiveWindow();
        if (hwnd == IntPtr.Zero) return false;
        if (!GetClientRect(hwnd, out var r)) return false;
        width = r.Right - r.Left;
        height = r.Bottom - r.Top;
        return width > 100 && height > 100;
    }

    public override void OnApplicationQuit()
    {
        LoggerInstance.Msg($"SetResolution intercepted: {_intercepted}, mode changes blocked: {_modeBlocked}, " +
                           $"hover canvas stretches: {_hoverFixes}, icon alignments: {_placed}");
    }
}
