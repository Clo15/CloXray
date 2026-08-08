using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace LiquidWobbleMPB
{
    /// <summary>
    /// Coexistence with KPlug (kPlug.Katarsys), which OWNS the male's penis materials in the main game.
    ///
    /// THE CONFLICT. KPlug's DickBehavior configures the dick by MATERIAL COUNT AND INDEX:
    ///
    /// foreach (Renderer item in list) // renderers named o_dankon / o_dan_f
    /// if (item.materials.Length == 2) {
    /// MatchCusBodyMatProperties(cha, item.materials[0], PartType.Dick);
    /// MatchCusBodyMatProperties(cha, item.materials[1], PartType.Knob);
    /// item.materials[1].SetTexture("_MainTex", Core.knobTxt);
    /// item.materials[1].mainTextureOffset = KnobTxtOffset(KnobColFromSkin(skinMainColor));
    /// } else
    /// MatchCusBodyMatProperties(cha, item.material, PartType.Ball);
    ///
    /// and MatchCusBodyMatProperties force-sets the shader (vanilla Shader Forge/main_skin outside
    /// Studio) plus _MainTex, _overtex1 and a per-part texture OFFSET - Dick (-0.5, 0.5) vs Ball
    /// (0, 0.5), half a texture apart.
    ///
    /// Our x-ray adds MaterialEditor copies to that renderer, so the count and the indices KPlug
    /// addresses by both change. It then configures the wrong material as the wrong part and never
    /// touches the knob at all, which leaves it untextured - the reported WHITE penis. Toggling the
    /// womb off restores the count and KPlug fixes itself, which is exactly what the reporter saw.
    ///
    /// THE FIX. Re-run KPlug's own branch over the materials it would have seen, i.e. with our
    /// .MECopy materials filtered out, by calling its own public statics. The result is by
    /// construction identical to what KPlug produces when we are not installed - we are not guessing
    /// at its intent, we are handing it the material set it expects.
    ///
    /// Inert when KPlug is absent, and inert when KPlug is not managing the dick (Core.useCustomDick
    /// false). If any piece of the reflection is missing the whole thing fails LOUD and applies
    /// NOTHING - a half-configured dick is worse than an un-x-rayed one.
    /// </summary>
    internal static class KPlugBridge
    {
        private static bool _init, _ok, _absentLogged;
        private static Type _dickBehavior, _core, _partType;
        private static MethodInfo _match, _knobOffset, _knobColor;
        private static FieldInfo _useCustomDick, _knobTxt;
        private static object _ptDick, _ptKnob, _ptBall;

        private static void Init()
        {
            if (_init) return;
            _init = true;
            try
            {
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        var t = a.GetType("kPlug.CmpChara.DickBehavior", false);
                        if (t != null) { _dickBehavior = t; _core = a.GetType("kPlug.Core", false); break; }
                    }
                    catch { }
                }
                if (_dickBehavior == null) return;   // KPlug not installed - the normal case, stay silent

                _partType = _dickBehavior.GetNestedType("PartType");
                _match      = _dickBehavior.GetMethod("MatchCusBodyMatProperties", BindingFlags.Static | BindingFlags.Public);
                _knobOffset = _dickBehavior.GetMethod("KnobTxtOffset", BindingFlags.Static | BindingFlags.Public);
                _knobColor  = _dickBehavior.GetMethod("KnobColFromSkin", BindingFlags.Static | BindingFlags.Public);
                _useCustomDick = _core != null ? _core.GetField("useCustomDick", BindingFlags.Static | BindingFlags.Public) : null;
                _knobTxt       = _core != null ? _core.GetField("knobTxt",       BindingFlags.Static | BindingFlags.Public) : null;

                if (_partType == null || _match == null || _useCustomDick == null || _knobTxt == null)
                {
                    LiquidWobbleMPBPlugin._logger?.LogError("CloXray: KPlug is installed but its dick API does not look the way we expect ("
                        + "PartType=" + (_partType != null) + " Match=" + (_match != null)
                        + " useCustomDick=" + (_useCustomDick != null) + " knobTxt=" + (_knobTxt != null)
                        + "). NOT re-applying its material setup — the penis may render untextured while a womb is active. "
                        + "This needs the bridge updating for this KPlug version.");
                    return;
                }
                _ptDick = Enum.Parse(_partType, "Dick");
                _ptKnob = Enum.Parse(_partType, "Knob");
                _ptBall = Enum.Parse(_partType, "Ball");
                _ok = true;
                InstallMatchWatch();
                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: KPlug detected — its dick material setup will be re-applied after ours, "
                    + "so our material copies cannot shift the parts it addresses by index.");
            }
            catch (Exception e)
            {
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: KPlug bridge init failed: " + e.GetType().Name + ": " + e.Message
                    + " — NOT re-applying its material setup.");
            }
        }

        public static bool Present { get { Init(); return _dickBehavior != null; } }

        // WATCH KPLUG'S OWN WRITE. A one-shot sync at apply time cannot hold: KPlug re-applies its
        // shader+offset package on its own schedule (pose changes, body reloads), always AFTER us. The
        // b891 log shows exactly that - our copies synced to off=(0.00,0.00), then KPlug moved the
        // original to (0.00,0.50) and they were stale again. So hook the method that does it and re-sync
        // the instant it returns, putting the CAPTURED good state back. Their event, not
        // our timer. Installed lazily, once, only when KPlug is actually present.
        private static Harmony _harmony;
        private static bool _watchInstalled;
        private static void InstallMatchWatch()
        {
            if (_watchInstalled || !_ok) return;
            _watchInstalled = true;
            try
            {
                _harmony = new Harmony("Clo.LiquidWobbleMPB.kplugdick");
                _harmony.Patch(_match, postfix: new HarmonyMethod(
                    typeof(KPlugBridge).GetMethod(nameof(MatchPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: hooked KPlug's dick material setup — our copies re-sync the moment it re-applies.");
            }
            catch (Exception e)
            {
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: could not hook KPlug's MatchCusBodyMatProperties (" + e.Message
                    + ") — his penis copies will go stale whenever KPlug re-applies, which renders it white.");
            }
        }

        private static void MatchPostfix(object refCha)
        {
            try
            {
                var cha = refCha as Component;
                if (cha != null) MEBridge.RestorePenisLook(cha, false);
            }
            catch { /* never throw inside another plugin's call */ }
        }

        /// <summary>
        /// True when KPlug has taken the male's dick materials over. While that is the case the shader,
        /// its textures and their OFFSETS are one package KPlug owns, and touching any part of it in
        /// isolation produces a combination neither plugin intended.
        ///
        /// Measured: KPlug's MatchCusBodyMatProperties sets Shader Forge/main_skin AND _MainTex offset
        /// (0, 0.5) together. Our body-edit repair then restored the character's KKUTS shader and left
        /// the offset behind, so KKUTS sampled half a texture off - white outside the body, correct
        /// through the window where our own copies draw at offset (0,0). Dump 8 of the local repro caught
        /// exactly that: original at off=(0.00,0.50) with our copies at (0.00,0.00).
        /// </summary>
        public static bool OwnsDick(Component male)
        {
            Init();
            if (!_ok || male == null) return false;
            try { return (bool)_useCustomDick.GetValue(null); }
            catch { return false; }
        }

        /// <summary>
        /// Hand KPlug back the material set it expects and let it re-configure. Call AFTER our copies
        /// are on the renderer — the whole point is that it runs on the post-copy state.
        /// </summary>
        public static void ReassertDickMaterials(Component male)
        {
            Init();
            if (!_ok || male == null) return;
            try
            {
                // Not yet managing this dick - nothing of KPlug's to hand back. It may take over later,
                // which is why the SNAPSHOT is taken on Present rather than waiting for this to be true.
                if (!(bool)_useCustomDick.GetValue(null)) return;

                int fixedUp = 0;
                foreach (var r in male.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null) continue;
                    string rn = r.name;
                    if (rn != "o_dankon" && rn != "o_dan_f") continue;

                    // The material set KPlug would have seen without us.
                    var all = r.sharedMaterials;
                    var real = new System.Collections.Generic.List<Material>();
                    foreach (var m in all)
                        if (m != null && !m.name.Contains(".MECopy")) real.Add(m);
                    if (real.Count == 0) continue;

                    // KPlug's own branch, verbatim, on that set.
                    if (real.Count == 2)
                    {
                        _match.Invoke(null, new object[] { male, real[0], _ptDick });
                        _match.Invoke(null, new object[] { male, real[1], _ptKnob });
                        var knob = _knobTxt.GetValue(null) as Texture;
                        if (knob != null) real[1].SetTexture("_MainTex", knob);
                        ApplyKnobOffset(male, real[1]);
                    }
                    else
                    {
                        _match.Invoke(null, new object[] { male, real[0], _ptBall });
                    }
                    fixedUp++;
                    if (AutoBodyReveal.Debug)
                        LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: KPlug re-apply on '" + rn + "': "
                            + real.Count + " real material(s) of " + all.Length + " (" + (all.Length - real.Count) + " of ours filtered out).");
                }
                if (fixedUp > 0)
                    LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: KPlug dick setup re-applied on " + fixedUp
                        + " renderer(s) of '" + male.name + "' — our copies are hidden from its index-based lookup.");
            }
            catch (Exception e)
            {
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: KPlug dick re-apply FAILED on '" + male.name + "': "
                    + e.GetType().Name + ": " + e.Message + " — his penis may render untextured while a womb is active.");
            }
        }

        // The knob's own texture offset is skin-tone derived. Optional by design: KPlug skips it in
        // chara maker, and if we cannot read the skin colour the knob keeps whatever offset it had
        // rather than getting a wrong one.
        private static void ApplyKnobOffset(Component male, Material knobMat)
        {
            if (_knobOffset == null || _knobColor == null || knobMat == null) return;
            try
            {
                var chaFile = male.GetType().GetProperty("chaFile", BindingFlags.Instance | BindingFlags.Public)?.GetValue(male, null);
                if (chaFile == null) return;
                var custom = chaFile.GetType().GetField("custom", BindingFlags.Instance | BindingFlags.Public)?.GetValue(chaFile);
                var body   = custom?.GetType().GetField("body", BindingFlags.Instance | BindingFlags.Public)?.GetValue(custom);
                var skinF  = body?.GetType().GetField("skinMainColor", BindingFlags.Instance | BindingFlags.Public);
                if (skinF == null) return;
                object col = _knobColor.Invoke(null, new object[] { skinF.GetValue(body) });
                knobMat.mainTextureOffset = (Vector2)_knobOffset.Invoke(null, new object[] { col });
            }
            catch { /* offset only — the knob is already textured by the caller */ }
        }
    }
}
