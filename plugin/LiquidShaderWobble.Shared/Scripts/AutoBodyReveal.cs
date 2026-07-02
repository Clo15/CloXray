using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace LiquidWobbleMPB
{
    /// <summary>
    /// Auto-applies the CloXray "BodyReveal" stencil-writer to a character's body by DRIVING
    /// MaterialEditor's API (reflection, no hard dependency — same style as <see cref="BPBridge"/>).
    /// Replicates the manual workflow: body o_body_a / cf_m_body -> ME "Copy Material" (cf_m_body.MECopy)
    /// -> shader "CloXray/BodyReveal" -> _StencilRef. MaterialEditor then PERSISTS + re-applies the copy
    /// on its own reloads, so we only have to (re)assert it when a fresh/swapped character appears.
    ///
    /// Trigger model is fully EVENT-DRIVEN (no per-frame poll):
    ///   - KKAPI CharacterApi.CharacterReloaded -> re-apply to the reloaded character (the swap/load case).
    ///   - A manual hotkey -> apply to every character that has a CloXray womb near its vagina
    ///     (covers initial placement + the rare "drag an existing womb onto another character").
    /// The stencil is read from the nearest womb's own organ material (_StencilBody), so the
    /// body/organ pair stays matched. Per-body: only characters a womb resolves to are touched.
    /// </summary>
    internal static class MEBridge
    {
        public const string BodyRevealShader = "CloXray/BodyReveal";
        public const string BodyVeilShader   = "CloXray/BodyRevealExtra";
        public const string OrgInsideShader  = "CloXray/OrgInside";   // applied to a male penis material so it x-rays through the body
        private const string PenisMat        = "cm_m_dankon";         // the male penis material name
        private const int   BodyVeilQueue    = 3504;   // after the WHOLE womb stack (organ 3500, interior 3502, cum 3503) — XrayAlpha = master fade

        private static bool _tried;
        private static Type _ctrlType;            // KK_Plugins.MaterialEditor.MaterialEditorCharaController
        private static Type _objType;             // nested ObjectType enum
        private static object _otCharacter;       // ObjectType.Character (boxed)
        private static object _otClothing;        // ObjectType.Clothing (boxed; resolved by name scan)
        private static MethodInfo _mCopyRemove, _mSetShader, _mSetFloat, _mSetQueue;

        public static bool Available { get { Init(); return _ctrlType != null; } }

        private static void Init()
        {
            if (_tried) return;
            _tried = true;

            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = a.GetType("KK_Plugins.MaterialEditor.MaterialEditorCharaController", false);
                    if (t != null) { _ctrlType = t; break; }
                }
                catch { }
            }
            if (_ctrlType == null)
            {
                LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: MaterialEditorCharaController not found (MaterialEditor not installed?).");
                return;
            }

            try
            {
                _objType = _ctrlType.GetNestedType("ObjectType");
                _otCharacter = Enum.Parse(_objType, "Character");
                // Clothing member name varies in capitalization across ME builds — resolve by scan.
                foreach (var n in Enum.GetNames(_objType))
                    if (n.ToLowerInvariant().Contains("cloth")) { _otClothing = Enum.Parse(_objType, n); break; }
                _mCopyRemove = _ctrlType.GetMethod("MaterialCopyRemove",
                    new[] { typeof(int), _objType, typeof(Material), typeof(GameObject) });
                _mSetShader = _ctrlType.GetMethod("SetMaterialShader",
                    new[] { typeof(int), _objType, typeof(Material), typeof(string), typeof(GameObject), typeof(bool) });
                _mSetFloat = _ctrlType.GetMethod("SetMaterialFloatProperty",
                    new[] { typeof(int), _objType, typeof(Material), typeof(string), typeof(float), typeof(GameObject), typeof(bool) });
                // Queue setter (for the veil copy, which must land at 3502). Signature varies across
                // ME versions — bind by name and fill args by parameter type at call time.
                try { _mSetQueue = _ctrlType.GetMethod("SetMaterialShaderRenderQueue"); } catch { _mSetQueue = null; }
            }
            catch (Exception e)
            {
                LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: failed to bind ME methods: " + e.Message);
                _ctrlType = null;
                return;
            }

            LiquidWobbleMPBPlugin._logger?.LogInfo(
                "MEBridge: hooked MaterialEditor. ObjectType=" + (_objType != null) +
                " copyRemove=" + (_mCopyRemove != null) + " setShader=" + (_mSetShader != null) +
                " setFloat=" + (_mSetFloat != null));
        }

        // Finds the visible body SMR (o_body_a in KK; o_body_cf/o_body_cm on some uncensors).
        private static SkinnedMeshRenderer FindBodyRenderer(Component cc)
        {
            SkinnedMeshRenderer fallback = null;
            foreach (var r in cc.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (r == null) continue;
                if (r.name == "o_body_a") return r;
                if (fallback == null && r.name.StartsWith("o_body")) fallback = r;
            }
            return fallback;
        }

        private static Material FindByShader(SkinnedMeshRenderer r, string shaderName)
        {
            foreach (var m in r.sharedMaterials)
                if (m != null && m.shader != null && m.shader.name == shaderName) return m;
            return null;
        }

        private static Material FindByName(SkinnedMeshRenderer r, string exact)
        {
            foreach (var m in r.sharedMaterials)
                if (m != null && m.name == exact) return m;
            return null;
        }

        // A FRESH (not-yet-configured) ME copy: name matches, but no CloXray shader assigned yet.
        // Skipping configured copies keeps the stamp copy and the veil copy from stealing each
        // other's slot when both exist on the body.
        private static Material FindCopy(SkinnedMeshRenderer r)
        {
            foreach (var m in r.sharedMaterials)
                if (m != null && m.name.StartsWith("cf_m_body.MECopy") &&
                    (m.shader == null || !m.shader.name.StartsWith("CloXray/"))) return m;
            return null;
        }

        /// <summary>
        /// Idempotently ensure the body has a BodyReveal copy at the given stencil. cc = the ChaControl
        /// (treated as a UnityEngine.Component). Logs loudly; no-ops if ME is absent.
        /// </summary>
        // True once this body's BodyReveal MECopy is present — i.e. MaterialEditor has restored (or the hotkey
        // created) it. The deferred on-load coroutine polls this: once true the saved copy is ADOPTED (never
        // re-stamped); if it never becomes true within the cap, the character is genuinely fresh -> create.
        public static bool MERestoredFor(Component cc)
        {
            if (cc == null) return false;
            try { var bodyR = FindBodyRenderer(cc); return bodyR != null && FindByShader(bodyR, BodyRevealShader) != null; }
            catch { return false; }
        }

        public static bool EnsureBodyReveal(Component cc, int stencil, bool debug, bool overwriteExisting)
        {
            Init();
            if (cc == null) return false;
            if (_ctrlType == null) { if (debug) LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: ME unavailable; cannot auto-apply BodyReveal."); return false; }

            try
            {
                var me = cc.GetComponent(_ctrlType);
                if (me == null) { if (debug) LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: no MaterialEditor controller on '" + cc.name + "'."); return false; }

                var bodyR = FindBodyRenderer(cc);
                if (bodyR == null) { if (debug) LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: body renderer (o_body*) not found on '" + cc.name + "'."); return false; }

                // GameObject ME expects for body edits = ChaControl.objBody (field OR property,
                // varies by game build); fall back to the renderer's GO (ME accepts it).
                GameObject go = GetBodyGo(cc) ?? bodyR.gameObject;

                // Already applied? On the HOTKEY (overwriteExisting) keep the stencil synced to the womb. On the
                // on-LOAD re-apply (overwriteExisting=false) LEAVE it: the saved copy is authoritative, and the womb's
                // _StencilBody may not be MaterialEditor-restored yet — reading it early on a non-default pair would
                // stamp the stale default 4 over the user's saved 8 (the load-reset bug). Mirrors how the veil already
                // protects its XrayAlpha slider from a re-press.
                var existing = FindByShader(bodyR, BodyRevealShader);
                if (existing != null)
                {
                    if (overwriteExisting && existing.HasProperty("_StencilRef") && Mathf.RoundToInt(existing.GetFloat("_StencilRef")) != stencil)
                    {
                        _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, existing, "StencilRef", (float)stencil, go, true });
                        if (debug) LiquidWobbleMPBPlugin._logger?.LogInfo("MEBridge: '" + cc.name + "' BodyReveal stencil updated -> " + stencil + ".");
                    }
                    else if (debug) LiquidWobbleMPBPlugin._logger?.LogInfo("MEBridge: '" + cc.name + "' already has BodyReveal (stencil " + stencil + "). No-op.");
                    return true;
                }

                // Find or create the copy slot (MaterialCopyRemove is a toggle -> only call it to CREATE).
                var copy = FindCopy(bodyR);
                if (copy == null)
                {
                    var body = FindByName(bodyR, "cf_m_body") ?? (bodyR.sharedMaterials.Length > 0 ? bodyR.sharedMaterials[0] : null);
                    if (body == null) { if (debug) LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: cf_m_body not found on '" + cc.name + "'."); return false; }
                    _mCopyRemove.Invoke(me, new object[] { 0, _otCharacter, body, go });
                    copy = FindCopy(bodyR);
                    if (copy == null) { LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: copy slot not created on '" + cc.name + "' (ME MaterialCopyRemove had no effect)."); return false; }
                }

                _mSetShader.Invoke(me, new object[] { 0, _otCharacter, copy, BodyRevealShader, go, true });
                _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, copy, "StencilRef", (float)stencil, go, true });
                LiquidWobbleMPBPlugin._logger?.LogInfo("MEBridge: applied BodyReveal to '" + cc.name + "' (copy='" + copy.name + "', shader='" + (copy.shader ? copy.shader.name : "?") + "', stencil=" + stencil + ").");
                return true;
            }
            catch (Exception e)
            {
                LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: EnsureBodyReveal failed on '" + (cc ? cc.name : "?") + "': " + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// Convert a SELECTED male's penis material (cm_m_dankon) to CloXray/OrgInside with OutsideOfBodyAlpha=1
        /// so the penis x-rays through the body (the "apply OrgInside to a penis" case). Changes the material's
        /// shader IN PLACE via ME (persists with the scene/card); idempotent. No-ops if ME is absent or the
        /// selected character has no cm_m_dankon (not a male / no penis mesh).
        /// </summary>
        public static bool EnsurePenisOrgInside(Component cc, int stencil, bool debug)
        {
            Init();
            if (cc == null) return false;
            if (_ctrlType == null) { if (debug) LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: ME unavailable; cannot apply penis OrgInside."); return false; }
            try
            {
                var me = cc.GetComponent(_ctrlType);
                if (me == null) { if (debug) LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: no MaterialEditor controller on '" + cc.name + "'."); return false; }

                Renderer penisR = null; Material dankon = null;
                foreach (var r in cc.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || r.sharedMaterials == null) continue;
                    foreach (var m in r.sharedMaterials)
                        if (m != null && IsPenisMat(m.name)) { penisR = r; dankon = m; break; }
                    if (dankon != null) break;
                }
                if (dankon == null) { if (debug) LiquidWobbleMPBPlugin._logger?.LogInfo("MEBridge: no '" + PenisMat + "' material on '" + cc.name + "' (not a male penis?) -> skip."); return false; }

                GameObject go = GetBodyGo(cc) ?? penisR.gameObject;
                bool already = dankon.shader != null && dankon.shader.name == OrgInsideShader;
                if (!already)
                    _mSetShader.Invoke(me, new object[] { 0, _otCharacter, dankon, OrgInsideShader, go, true });
                // Stencil pair MUST match the FEMALE body the penis is seen through (the same value EnsureBodyReveal
                // stamps on her body via womb.OrganStencil()). Without this the OrgInside depth-clear only fires for a
                // default pair-A womb (4/5); on any other pair (8/9,12/13,16/17) the penis stays hidden behind skin.
                // OutsideOfBodyAlpha=1 keeps the outside-body part visible (already the OrgInside default; re-asserted
                // in case a card lowered it).
                _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, dankon, "StencilBody",        (float)stencil,       go, true });
                _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, dankon, "StencilBody_Plus_1", (float)(stencil + 1), go, true });
                _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, dankon, "OutsideOfBodyAlpha", 1f,                  go, true });
                LiquidWobbleMPBPlugin._logger?.LogInfo("MEBridge: penis x-ray on '" + cc.name + "': '" + dankon.name + "' -> " + OrgInsideShader + " (stencil " + stencil + "/" + (stencil + 1) + ", OutsideOfBodyAlpha=1)" + (already ? " [shader already set]" : "") + ".");
                return true;
            }
            catch (Exception e)
            {
                LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: EnsurePenisOrgInside failed on '" + (cc ? cc.name : "?") + "': " + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        // Match cm_m_dankon, tolerating runtime " (Instance)" suffix(es) (like the clothes path does).
        private static bool IsPenisMat(string n)
        {
            if (string.IsNullOrEmpty(n)) return false;
            int sp = n.IndexOf(" (");
            if (sp > 0) n = n.Substring(0, sp);
            return n == PenisMat;
        }

        // Does this character carry a real penis material (cm_m_dankon)? Used to gate the penis features so the
        // shared k_f_dan FK bones — which KK_AdditionalFKNodes ALSO adds to females — don't trigger on a normal female.
        public static bool HasPenisMaterial(Component cc)
        {
            if (cc == null) return false;
            foreach (var r in cc.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || r.sharedMaterials == null) continue;
                foreach (var m in r.sharedMaterials)
                    if (m != null && IsPenisMat(m.name)) return true;
            }
            return false;
        }

        /// <summary>
        /// Idempotently ensure the body ALSO has the BodyRevealExtra "skin veil" copy (the cosmetic
        /// translucent-skin layer over the organ window, with the user-facing XrayAlpha slider).
        /// A SECOND ME copy is required because one material has ONE render queue: the stamp must
        /// draw before the organ (2500), the veil after it (3502). Run AFTER EnsureBodyReveal.
        /// </summary>
        public static bool EnsureBodyVeil(Component cc, int stencilPlus1, bool debug, bool overwriteExisting)
        {
            Init();
            if (cc == null || _ctrlType == null) return false;

            try
            {
                var me = cc.GetComponent(_ctrlType);
                var bodyR = me != null ? FindBodyRenderer(cc) : null;
                if (bodyR == null) return false;

                GameObject go = GetBodyGo(cc) ?? bodyR.gameObject;

                // Already applied? -> just keep the pair stencil correct.
                var existing = FindByShader(bodyR, BodyVeilShader);
                if (existing != null)
                {
                    if (overwriteExisting && existing.HasProperty("_StencilBody_Plus_1") && Mathf.RoundToInt(existing.GetFloat("_StencilBody_Plus_1")) != stencilPlus1)
                    {
                        _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, existing, "StencilBody_Plus_1", (float)stencilPlus1, go, true });
                        if (debug) LiquidWobbleMPBPlugin._logger?.LogInfo("MEBridge: '" + cc.name + "' veil stencil updated -> " + stencilPlus1 + ".");
                    }
                    else if (debug) LiquidWobbleMPBPlugin._logger?.LogInfo("MEBridge: '" + cc.name + "' already has the skin veil. No-op.");
                    EnsureVeilQueue(me, existing, go);
                    return true;
                }

                // Fresh copy. MaterialCopyRemove on the SOURCE adds a copy; verify one actually
                // appeared (toggle semantics differ across ME versions — fail loud, never guess).
                var copy = FindCopy(bodyR);
                if (copy == null)
                {
                    var body = FindByName(bodyR, "cf_m_body") ?? (bodyR.sharedMaterials.Length > 0 ? bodyR.sharedMaterials[0] : null);
                    if (body == null) return false;
                    int before = CountCopies(bodyR);
                    _mCopyRemove.Invoke(me, new object[] { 0, _otCharacter, body, go });
                    copy = FindCopy(bodyR);
                    if (copy == null || CountCopies(bodyR) <= before)
                    {
                        LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: veil copy slot not created on '" + cc.name + "' (second MaterialCopyRemove had no effect — ME version may not support multiple copies).");
                        return false;
                    }
                }

                _mSetShader.Invoke(me, new object[] { 0, _otCharacter, copy, BodyVeilShader, go, true });
                _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, copy, "StencilBody_Plus_1", (float)stencilPlus1, go, true });
                // Initial x-ray strength — set ONLY on creation (a re-press never clobbers the
                // user's per-scene slider tweaks; the existing-copy path above doesn't touch it).
                _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, copy, "XrayAlpha", LiquidWobbleMPBPlugin.CfgVeilAlpha, go, true });
                EnsureVeilQueue(me, copy, go);
                LiquidWobbleMPBPlugin._logger?.LogInfo("MEBridge: applied skin veil to '" + cc.name + "' (copy='" + copy.name + "', stencil+1=" + stencilPlus1 + ", q=" + copy.renderQueue + "). XrayAlpha slider on that material controls x-ray strength.");
                return true;
            }
            catch (Exception e)
            {
                LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: EnsureBodyVeil failed on '" + (cc ? cc.name : "?") + "': " + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        // ChaControl.objBody — field or property depending on the game build.
        private static GameObject GetBodyGo(Component cc)
        {
            const BindingFlags ANY = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var f = cc.GetType().GetField("objBody", ANY);
            if (f != null) { var g = f.GetValue(cc) as GameObject; if (g != null) return g; }
            var p = cc.GetType().GetProperty("objBody", ANY);
            if (p != null) { try { return p.GetValue(cc, null) as GameObject; } catch { } }
            return null;
        }

        private static int CountCopies(SkinnedMeshRenderer r)
        {
            int n = 0;
            foreach (var m in r.sharedMaterials)
                if (m != null && m.name.StartsWith("cf_m_body.MECopy")) n++;
            return n;
        }

        // KK clothes kinds stamped by EnsureClothesReveal: top, bot, bra, shorts, panst.
        // (Gloves/socks/shoes never cover the womb; skipping them keeps ME's record count down.)
        private static readonly int[] ClothesKinds = { 0, 1, 2, 3, 5 };

        /// <summary>
        /// Idempotently stamp every WORN (active) torso garment with a BodyReveal copy at the given
        /// stencil — the womb then x-rays through the clothes, and the out-of-body bleed disappears
        /// at the stamped pixels (validated in-game: ANALYSIS_depth_containment.md EXP 10).
        /// Mirrors the manual ME recipe: per garment material, Copy Material -> CloXray/BodyReveal
        /// -> StencilRef. ME persists the copies in the scene/card like the body ones.
        /// </summary>
        public static bool EnsureClothesReveal(Component cc, int stencil, bool debug)
        {
            Init();
            if (cc == null || _ctrlType == null) return false;
            if (_otClothing == null)
            {
                LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: ME ObjectType has no Clothing member — clothes stamping unavailable.");
                return false;
            }

            try
            {
                var me = cc.GetComponent(_ctrlType);
                if (me == null) return false;

                // objClothes is a FIELD in some game builds and a PROPERTY in others (this install:
                // property — the field lookup came back null in-game). Try both, any visibility.
                GameObject[] slots = null;
                const BindingFlags ANY = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var fC = cc.GetType().GetField("objClothes", ANY);
                if (fC != null) slots = fC.GetValue(cc) as GameObject[];
                if (slots == null)
                {
                    var pC = cc.GetType().GetProperty("objClothes", ANY);
                    if (pC != null) slots = pC.GetValue(cc, null) as GameObject[];
                }
                if (slots == null)
                {
                    LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: ChaControl.objClothes not found (field NOR property) on '" + cc.name + "' — clothes stamping unavailable.");
                    return false;
                }

                int stamped = 0, updated = 0;
                if (debug)
                    LiquidWobbleMPBPlugin._logger?.LogInfo("[clothes] '" + cc.name + "': objClothes.Length=" + slots.Length +
                        " otClothing='" + _otClothing + "' kinds=" + string.Join(",", Array.ConvertAll(ClothesKinds, k => k.ToString())));
                foreach (int kind in ClothesKinds)
                {
                    if (kind >= slots.Length) continue;
                    var go = slots[kind];
                    if (go == null || !go.activeInHierarchy)
                    {
                        if (debug) LiquidWobbleMPBPlugin._logger?.LogInfo("[clothes] kind " + kind + ": " + (go == null ? "empty slot" : "inactive ('" + go.name + "') — clothes OFF") + " -> skip");
                        continue;   // only clothes that are ON
                    }
                    if (debug)
                    {
                        var rs = go.GetComponentsInChildren<Renderer>(false);
                        LiquidWobbleMPBPlugin._logger?.LogInfo("[clothes] kind " + kind + " '" + go.name + "': " + rs.Length + " active renderer(s)");
                    }

                    foreach (var r in go.GetComponentsInChildren<Renderer>(false))
                    {
                        if (r == null) continue;
                        var smr = r as SkinnedMeshRenderer;
                        if (smr != null && smr.sharedMesh != null && r.sharedMaterials.Length > smr.sharedMesh.subMeshCount)
                            LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: clothes renderer '" + r.name + "' already has more materials than submeshes — a new copy may only cover the LAST submesh (known ME/Unity limit).");

                        // Snapshot the SOURCE materials first (ME appends copies while we work).
                        var sources = new System.Collections.Generic.List<Material>();
                        foreach (var m in r.sharedMaterials)
                        {
                            if (m == null) continue;
                            if (m.name.Contains(".MECopy")) continue;                                   // a copy, not a source
                            if (m.shader != null && m.shader.name.StartsWith("CloXray/")) continue;     // already ours
                            sources.Add(m);
                        }

                        if (debug)
                        {
                            string names = "";
                            foreach (var m in r.sharedMaterials) names += (m == null ? "(null)" : m.name + "[" + (m.shader ? m.shader.name : "?") + "]") + "; ";
                            LiquidWobbleMPBPlugin._logger?.LogInfo("[clothes]   rend '" + r.name + "': " + sources.Count + " source(s) of " + r.sharedMaterials.Length + " mat(s): " + names);
                        }

                        foreach (var src in sources)
                        {
                            // ME names copies from the SANITIZED base name: runtime clothes materials
                            // are instanced ("cf_m_bot_skirt01 (Instance) (Instance)") but the copy is
                            // "cf_m_bot_skirt01.MECopy1" — strip the suffixes before matching.
                            string baseName = src.name;
                            while (baseName.EndsWith(" (Instance)"))
                                baseName = baseName.Substring(0, baseName.Length - " (Instance)".Length);

                            // Existing BodyReveal copy for this source? -> just keep the stencil right.
                            Material existing = null;
                            foreach (var m in r.sharedMaterials)
                                if (m != null && m.name.StartsWith(baseName + ".MECopy") &&
                                    m.shader != null && m.shader.name == BodyRevealShader) { existing = m; break; }
                            if (existing != null)
                            {
                                if (existing.HasProperty("_StencilRef") && Mathf.RoundToInt(existing.GetFloat("_StencilRef")) != stencil)
                                {
                                    _mSetFloat.Invoke(me, new object[] { kind, _otClothing, existing, "StencilRef", (float)stencil, go, true });
                                    updated++;
                                }
                                continue;
                            }

                            // Adopt a leftover unconfigured copy first (e.g. from a run where the
                            // name matching failed) before asking ME for a new one.
                            Material copy = null;
                            foreach (var m in r.sharedMaterials)
                                if (m != null && m.name.StartsWith(baseName + ".MECopy") &&
                                    (m.shader == null || !m.shader.name.StartsWith("CloXray/"))) { copy = m; break; }
                            if (copy == null)
                            {
                                _mCopyRemove.Invoke(me, new object[] { kind, _otClothing, src, go });
                                foreach (var m in r.sharedMaterials)
                                    if (m != null && m.name.StartsWith(baseName + ".MECopy") &&
                                        (m.shader == null || !m.shader.name.StartsWith("CloXray/"))) { copy = m; break; }
                            }
                            if (copy == null)
                            {
                                string after = "";
                                foreach (var m in r.sharedMaterials) after += (m == null ? "(null)" : m.name) + "; ";
                                LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: clothes copy not created for '" + src.name + "' on '" + r.name + "' (ME MaterialCopyRemove had no effect). Mats after call: " + after);
                                continue;
                            }
                            _mSetShader.Invoke(me, new object[] { kind, _otClothing, copy, BodyRevealShader, go, true });
                            _mSetFloat.Invoke(me, new object[] { kind, _otClothing, copy, "StencilRef", (float)stencil, go, true });
                            stamped++;
                        }
                    }
                }

                if (stamped > 0 || updated > 0 || debug)
                    LiquidWobbleMPBPlugin._logger?.LogInfo("MEBridge: clothes x-ray on '" + cc.name + "': " + stamped + " garment material(s) stamped, " + updated + " restenciled (stencil " + stencil + ").");
                return true;
            }
            catch (Exception e)
            {
                LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: EnsureClothesReveal failed on '" + (cc ? cc.name : "?") + "': " + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        // The veil MUST sit at queue 3502 (after the organ, 3500) or it silently does nothing.
        // Assigning the shader normally adopts its tag queue (3502); if ME kept a different queue,
        // push it via ME's queue API (persisted) + set it directly (immediate), and say so.
        private static void EnsureVeilQueue(object me, Material veil, GameObject go)
        {
            if (veil == null || veil.renderQueue == BodyVeilQueue) return;
            LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: veil copy queue is " + veil.renderQueue + " (want " + BodyVeilQueue + ") — correcting.");
            if (_mSetQueue != null)
            {
                try
                {
                    var ps = _mSetQueue.GetParameters();
                    var args = new object[ps.Length];
                    bool slotFilled = false;
                    for (int i = 0; i < ps.Length; i++)
                    {
                        var pt = ps[i].ParameterType;
                        if (pt == typeof(int))            { args[i] = slotFilled ? (object)BodyVeilQueue : (object)0; slotFilled = true; }
                        else if (pt == _objType)          args[i] = _otCharacter;
                        else if (pt == typeof(Material))  args[i] = veil;
                        else if (pt == typeof(GameObject))args[i] = go;
                        else if (pt == typeof(bool))      args[i] = true;
                        else args[i] = null;
                    }
                    _mSetQueue.Invoke(me, args);
                }
                catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: ME queue set failed (" + e.Message + ") — applying direct (non-persisted) queue."); }
            }
            veil.renderQueue = BodyVeilQueue;
        }
    }

    // Drives the NodesConstraints plugin (Joan6694, GUID com.joan6694.illusionplugins.nodesconstraints) by
    // reflection — soft dependency. Links the male penis FK bones to the female vagina + our womb's penis_target,
    // the same mechanism BetterPenetration's Studio component uses. NodesConstraints.AddConstraint is idempotent
    // (skips an existing pair) and persists constraints with the scene via KKAPI ExtendedSave, so one AddConstraint
    // call is enough for the link to save/load.
    internal static class NodeConstraintBridge
    {
        private const string NcGuid = "com.joan6694.illusionplugins.nodesconstraints";
        private const BindingFlags BF = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static bool _tried;
        private static Type _ncType;
        private static MethodInfo _addConstraint;   // the 10-arg convenience overload
        private static FieldInfo _fConstraints;     // NodesConstraints._constraints (List<Constraint>)
        private static FieldInfo _fChildTransform;  // Constraint.childTransform
        private static FieldInfo _fParentTransform; // Constraint.parentTransform
        private static Harmony _ncHarmony;          // patches the NC pairing-change methods (add / enable-disable / delete)
        private static bool _pairingHooksTried;     // install-once guard for the pairing hooks

        // DIAG: dump every existing constraint (parent/child name + world position) so we can read what the user
        // set up by hand and learn the correct target.
        public static void DumpConstraints(string tag)
        {
            Init();
            var inst = Instance();
            if (inst == null) return;
            try
            {
                if (_fConstraints == null) _fConstraints = _ncType.GetField("_constraints", BF);
                var list = (_fConstraints != null ? _fConstraints.GetValue(inst) : null) as System.Collections.IList;
                if (list == null) return;
                LiquidWobbleMPBPlugin._logger?.LogInfo("NodeConstraintBridge: " + tag + " — " + list.Count + " constraint(s):");
                foreach (var c in list)
                {
                    if (c == null) continue;
                    if (_fParentTransform == null) _fParentTransform = c.GetType().GetField("parentTransform", BF);
                    if (_fChildTransform == null) _fChildTransform = c.GetType().GetField("childTransform", BF);
                    var pt = _fParentTransform != null ? _fParentTransform.GetValue(c) as Transform : null;
                    var ct = _fChildTransform != null ? _fChildTransform.GetValue(c) as Transform : null;
                    LiquidWobbleMPBPlugin._logger?.LogInfo("    parent '" + (pt != null ? pt.name : "?") + "' @" + (pt != null ? pt.position.ToString("F2") : "?") + "   ->   child '" + (ct != null ? ct.name : "?") + "' @" + (ct != null ? ct.position.ToString("F2") : "?"));
                }
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("NodeConstraintBridge: dump failed: " + e.Message); }
        }

        public static bool Available { get { Init(); return _addConstraint != null && Instance() != null; } }

        // Is `child` already the CHILD of any existing constraint? Dedup by child (not just the parent/child PAIR
        // NodesConstraints itself checks), so re-adding can never stack a second link on the same penis bone even
        // if the parent transform differs. Mirrors how BP keys its dan constraints by childTransform.
        public static bool HasConstraintForChild(Transform child)
        {
            Init();
            var inst = Instance();
            if (inst == null || child == null) return false;
            try
            {
                if (_fConstraints == null) _fConstraints = _ncType.GetField("_constraints", BF);
                var list = (_fConstraints != null ? _fConstraints.GetValue(inst) : null) as System.Collections.IList;
                if (list == null) return false;
                foreach (var c in list)
                {
                    if (c == null) continue;
                    if (_fChildTransform == null) _fChildTransform = c.GetType().GetField("childTransform", BF);
                    if (_fChildTransform == null) return false;
                    if ((_fChildTransform.GetValue(c) as Transform) == child) return true;
                }
            }
            catch { }
            return false;
        }

        // Is `node` already wired into ANY existing constraint — as the driven CHILD or as a driving PARENT?
        // The hotkey uses this to NEVER reassign a dan node the user has already targeted by hand (e.g. dan_entry/
        // dan_end aimed at their own spheres), regardless of which way they wired it.
        public static bool HasConstraintForNode(Transform node)
        {
            Init();
            var inst = Instance();
            if (inst == null || node == null) return false;
            try
            {
                if (_fConstraints == null) _fConstraints = _ncType.GetField("_constraints", BF);
                var list = (_fConstraints != null ? _fConstraints.GetValue(inst) : null) as System.Collections.IList;
                if (list == null) return false;
                foreach (var c in list)
                {
                    if (c == null) continue;
                    if (_fChildTransform == null)  _fChildTransform  = c.GetType().GetField("childTransform", BF);
                    if (_fParentTransform == null) _fParentTransform = c.GetType().GetField("parentTransform", BF);
                    if (_fChildTransform != null  && (_fChildTransform.GetValue(c)  as Transform) == node) return true;
                    if (_fParentTransform != null && (_fParentTransform.GetValue(c) as Transform) == node) return true;
                }
            }
            catch { }
            return false;
        }

        // Resolve the live NodesConstraints plugin by GUID (robust vs a bare type-name scan that could collide).
        private static object Instance()
        {
            try
            {
                var infos = BepInEx.Bootstrap.Chainloader.PluginInfos;
                BepInEx.PluginInfo pi;
                if (infos != null && infos.TryGetValue(NcGuid, out pi) && pi != null) return pi.Instance;
            }
            catch { }
            return null;
        }

        private static void Init()
        {
            if (_tried) return;
            _tried = true;
            var inst = Instance();
            if (inst == null) { LiquidWobbleMPBPlugin._logger?.LogWarning("NodeConstraintBridge: NodesConstraints plugin (" + NcGuid + ") not present — penis constraints unavailable."); return; }
            _ncType = inst.GetType();
            foreach (var m in _ncType.GetMethods(BF))
                if (m.Name == "AddConstraint" && m.GetParameters().Length == 10) { _addConstraint = m; break; }
            if (_addConstraint == null) LiquidWobbleMPBPlugin._logger?.LogWarning("NodeConstraintBridge: AddConstraint(10-arg) not found on " + _ncType.FullName + ".");
            else LiquidWobbleMPBPlugin._logger?.LogInfo("NodeConstraintBridge: hooked NodesConstraints (" + NcGuid + ").");
        }

        // Position-only link: parentTransform drives childTransform. Idempotent (NodesConstraints returns null if
        // the pair already exists, either direction). Returns true if added OR already present; false on failure.
        // FAIL-LOUD: a NodesConstraints link only DRIVES if an endpoint resolves to a registered Studio GuideObject
        // at add time (cached, not re-resolved per frame). If a freshly-added one resolved NEITHER, warn — it
        // persists and looks added but won't move anything (the silent-fallback case to surface).
        public static bool AddPositionLink(Transform parentTransform, Transform childTransform, string alias)
        {
            Init();
            if (_addConstraint == null || parentTransform == null || childTransform == null) return false;
            var inst = Instance();
            if (inst == null) { LiquidWobbleMPBPlugin._logger?.LogWarning("NodeConstraintBridge: no live NodesConstraints instance."); return false; }
            try
            {
                object res = _addConstraint.Invoke(inst, new object[] {
                    true, parentTransform, childTransform, true, Vector3.zero, false, Quaternion.identity, false, Vector3.zero, alias });
                string pair = "'" + parentTransform.name + "' -> '" + childTransform.name + "'";
                if (res == null) { LiquidWobbleMPBPlugin._logger?.LogInfo("NodeConstraintBridge: constraint " + pair + " already present."); return true; }
                // Will it actually DRIVE? A link only moves a bone if an endpoint is a live Studio node (GuideObject).
                bool? gp = GuideObjectBridge.IsGuideObject(parentTransform);
                bool? gc = GuideObjectBridge.IsGuideObject(childTransform);
                if (gp == false && gc == false)
                    LiquidWobbleMPBPlugin._logger?.LogWarning("NodeConstraintBridge: added " + pair + " BUT neither endpoint is a live Studio node (GuideObject) -> it will NOT drive (parentGuideObj=" + gp + " childGuideObj=" + gc + "). Activate the node (select penis_target / enable the penis FK via KK_AdditionalFKNodes).");
                else
                    LiquidWobbleMPBPlugin._logger?.LogInfo("NodeConstraintBridge: added " + pair + " (parentGuideObj=" + gp + " childGuideObj=" + gc + " -> drives).");
                return true;
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("NodeConstraintBridge: AddConstraint failed (" + parentTransform.name + " -> " + childTransform.name + "): " + e.Message); return false; }
        }

        // ── NC PAIRING HOOKS ──────────────────────────────────────────────────────────────────────────────────
        // Harmony-postfix the THREE NodesConstraints methods that can change a k_f_dan_entry constraint — AddConstraint
        // (create; the GUI "Add", scene/XML load, copy, and our own AddPositionLink all funnel through it), the
        // SetConstraintEnabled toggle (the GUI checkbox, the "Link" toggle, and the Timeline interpolable all route
        // through it), and RemoveConstraintAt (delete). Each fires RequestRepair so every womb re-pairs on its next
        // post-IK frame. THIS is what lets WombExpandEffect run pairing fully event-driven with NO fallback poll: a
        // seated womb's entry is NC-pinned, so it only moves via these discrete operations + character (re)load (also
        // wired to RequestRepair). Lazy + install-once + fail-loud per hook: if NodesConstraints renames a method on a
        // future version we LOG it and that one event won't fire instantly (diagnosable, not silently masked by a poll).
        // Caller-gated to a womb being present (InstallWombHooks). NodesConstraints loads at BepInEx startup, so by the
        // time a womb/character exists it is resolvable.
        public static void InstallPairingHooks()
        {
            if (_pairingHooksTried) return;
            Init();
            if (_ncType == null || Instance() == null) return;   // NodesConstraints not loaded yet -> retry on a later call
            _pairingHooksTried = true;
            if (_ncHarmony == null) _ncHarmony = new Harmony("Clo.LiquidWobbleMPB.ncpairing");
            var cstr = _ncType.GetNestedType("Constraint", BindingFlags.NonPublic | BindingFlags.Public);

            // enable/disable toggle: SetConstraintEnabled(Constraint, bool)
            var mToggle = (cstr != null) ? _ncType.GetMethod("SetConstraintEnabled", BF, null, new[] { cstr, typeof(bool) }, null) : null;
            PatchOrWarn(mToggle, nameof(OnSetConstraintEnabled), "SetConstraintEnabled (enable/disable)");

            // create: the AddConstraint overload that actually appends to _constraints is the one with the MOST
            // parameters (the shorter overloads delegate to it); its return value is the new Constraint.
            MethodInfo mAdd = null; int maxP = -1;
            foreach (var m in _ncType.GetMethods(BF))
                if (m.Name == "AddConstraint" && m.GetParameters().Length > maxP) { maxP = m.GetParameters().Length; mAdd = m; }
            PatchOrWarn(mAdd, nameof(OnAddConstraint), "AddConstraint (create)");

            // delete: RemoveConstraintAt(int)
            var mDel = _ncType.GetMethod("RemoveConstraintAt", BF, null, new[] { typeof(int) }, null);
            PatchOrWarn(mDel, nameof(OnRemoveConstraint), "RemoveConstraintAt (delete)");
        }

        // Each hook is resolved, patched + logged INDEPENDENTLY (its own try/catch) so one failing target can't abort the
        // others, and every outcome is logged (fail-loud, no silent give-up). The install-once guard above prevents double-patch.
        private static void PatchOrWarn(MethodInfo target, string postfixName, string label)
        {
            if (target == null) { LiquidWobbleMPBPlugin._logger?.LogWarning("NodeConstraintBridge: NC " + label + " method not found (NodesConstraints version changed?) — that pairing change will NOT update instantly."); return; }
            try
            {
                _ncHarmony.Patch(target, postfix: new HarmonyMethod(typeof(NodeConstraintBridge).GetMethod(postfixName, BindingFlags.Static | BindingFlags.NonPublic)));
                LiquidWobbleMPBPlugin._logger?.LogInfo("NodeConstraintBridge: hooked " + label + " -> instant womb<->penis re-pair.");
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("NodeConstraintBridge: failed to hook NC " + label + " (" + e.Message + ") — that pairing change will NOT update instantly."); }
        }

        // Postfixes. __0 = the Constraint arg (toggle); __result = the new Constraint (add; null if NC deduped it).
        // Both re-pair only when the touched constraint's child/parent is k_f_dan_entry. Delete can't read the removed
        // constraint after the fact, so it re-pairs unconditionally (a delete is rare and FindByEntry is cheap + sticky).
        private static void OnSetConstraintEnabled(object __0)  { RepairIfDanEntry(__0); }
        private static void OnAddConstraint(object __result)    { RepairIfDanEntry(__result); }
        private static void OnRemoveConstraint()
        {
            if (WombExpandEffect.EffectiveActive) WombExpandEffect.RequestRepair();
        }

        private static void RepairIfDanEntry(object constraint)
        {
            if (!WombExpandEffect.EffectiveActive || constraint == null) return;   // mod off / no womb / deduped add -> ignore
            try
            {
                if (_fChildTransform == null)  _fChildTransform  = constraint.GetType().GetField("childTransform", BF);
                if (_fParentTransform == null) _fParentTransform = constraint.GetType().GetField("parentTransform", BF);
                var ct = _fChildTransform  != null ? _fChildTransform.GetValue(constraint)  as Transform : null;
                var pt = _fParentTransform != null ? _fParentTransform.GetValue(constraint) as Transform : null;
                if ((ct != null && ct.name == "k_f_dan_entry") || (pt != null && pt.name == "k_f_dan_entry"))
                    WombExpandEffect.RequestRepair();   // the pairing anchor changed -> every womb rechecks next post-IK frame
            }
            catch { }
        }
    }

    // Stops BetterPenetration stacking a DUPLICATE dan constraint on every scene load.
    //
    // BP's reload re-add (KK_Studio_BetterPenetration: ReinitializeControllers -> AddDanConstraints, fired ~1s
    // after load via its resetDelay counter) re-resolves the saved dan-bone parent BY NAME inside the female's
    // hierarchy (Core_BetterPenetration.BetterPenetrationController.AddDanConstraints -> Tools.GetTransformOfChaControl).
    // When two same-named studio items (e.g. two spheres) target the dan bones and one is parented under the
    // female, that by-name lookup matches the WRONG item, so BP adds a second 'X -> k_f_dan_end' link each load
    // (NodesConstraints dedups only by the full parent+child PAIR, so a mismatched-parent pair is accepted).
    // BP's "Auto-Target" setting does NOT gate this path — which is why turning Auto-Target Off doesn't stop it.
    //
    // We hook ONLY that by-name path: AddDanConstraints is called with NULL parents on the reload re-add and the
    // "Enable BP Controller" toggle, but with EXPLICIT parents when Auto-Target re-aims (CheckAutoTarget, which
    // also removes the old links first). So we skip ONLY when both parents are null AND the male's k_f_dan_end is
    // ALREADY constrained in NodesConstraints (the re-add is then redundant). That leaves Auto-Target re-aiming,
    // first-time setup, manual NC edits, and our own hotkey untouched, and is a complete no-op when BP isn't installed.
    internal static class BPDanReaddGuard
    {
        private const string BpTypeName = "Core_BetterPenetration.BetterPenetrationController";
        private static bool _applied;
        private static Harmony _harmony;

        // Idempotent + LAZY. BP's Core_BetterPenetration assembly is loaded only when the first BP character
        // appears (after every plugin's Awake), so an Awake-time attempt no-ops; we therefore ALSO call this on
        // each CharacterReloaded and actually patch once the type resolves. Cheap: returns immediately after a
        // successful install or a hard failure (never spins, never spams the log).
        public static void TryApply()
        {
            if (_applied) return;
            try
            {
                var bpType = AccessTools.TypeByName(BpTypeName);
                if (bpType == null) return;   // BP not loaded yet — retry on the next CharacterReloaded
                var m = AccessTools.Method(bpType, "AddDanConstraints");
                if (m == null)
                {
                    _applied = true;          // renamed/removed in this BP version — stop retrying, warn once
                    LiquidWobbleMPBPlugin._logger?.LogWarning("BPDanReaddGuard: " + BpTypeName + ".AddDanConstraints not found (BP version changed?) — dan-dup guard NOT installed.");
                    return;
                }
                if (_harmony == null) _harmony = new Harmony("Clo.LiquidWobbleMPB.bpdanguard");
                _harmony.Patch(m, prefix: new HarmonyMethod(typeof(BPDanReaddGuard).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic)));
                _applied = true;
                LiquidWobbleMPBPlugin._logger?.LogInfo("BPDanReaddGuard: patched " + BpTypeName + ".AddDanConstraints (prevents duplicate dan constraint on load).");
            }
            catch (Exception e)
            {
                _applied = true;              // don't spin on a hard error — warn once, leave BP as-is
                LiquidWobbleMPBPlugin._logger?.LogWarning("BPDanReaddGuard: failed to install guard (" + e.Message + ") — BP will behave as before (may re-add a duplicate on load).");
            }
        }

        // Return false to SKIP BP's AddDanConstraints. Only for the by-name re-add path (both parents null) when
        // the male's k_f_dan_end is already constrained — i.e. the re-add is redundant (and can mis-bind by name).
        private static bool Prefix(object __instance, Transform danEntryParent, Transform danEndParent)
        {
            if (!WombExpandEffect.EffectiveActive) return true;   // mod off or no CloXray womb -> never touch BP's dan re-add
            // Auto-Target re-aim (and any explicit-target call) passes real parents — never interfere with it.
            if (danEntryParent != null || danEndParent != null) return true;
            try
            {
                Transform danEnd = ResolveDanEnd(__instance);
                if (danEnd != null && NodeConstraintBridge.HasConstraintForNode(danEnd))
                {
                    LiquidWobbleMPBPlugin._logger?.LogInfo("BPDanReaddGuard: '" + danEnd.name + "' already constrained in NodesConstraints — skipping BP's by-name dan re-add (would duplicate).");
                    return false;   // skip BP's re-add
                }
            }
            catch (Exception e)
            {
                LiquidWobbleMPBPlugin._logger?.LogWarning("BPDanReaddGuard: guard check failed (" + e.Message + ") — letting BP run (may duplicate).");
            }
            return true;   // run BP's AddDanConstraints unchanged
        }

        // This controller's male k_f_dan_end, found under its ChaControl (no reliance on BP internal fields).
        private static Transform ResolveDanEnd(object controller)
        {
            if (controller == null) return null;
            var ccProp = controller.GetType().GetProperty("ChaControl",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var cc = ccProp?.GetValue(controller, null) as Component;
            return FindDanEnd(cc);
        }

        // A male's k_f_dan_end (real penis tip), found under its ChaControl. Shared with the on-load penis-bend
        // re-assert gate (PenisFKBridge path) so a deliberately hand-FK-posed penis can be left alone.
        public static Transform FindDanEnd(Component chaControl)
        {
            if (chaControl == null) return null;
            foreach (var t in chaControl.GetComponentsInChildren<Transform>(true))
                if (t != null && t.name == "k_f_dan_end") return t;
            return null;
        }
    }

    // Reads Studio's GuideObjectManager (Singleton) to tell whether a Transform is a live guide object — the
    // precondition for a NodesConstraints link to actually DRIVE that bone. Reflection (no hard Studio ref).
    internal static class GuideObjectBridge
    {
        private const BindingFlags BF = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static bool _tried;
        private static Type _gomType;
        private static PropertyInfo _instProp;
        private static FieldInfo _instField;
        private static FieldInfo _dicField;

        private static void Init()
        {
            if (_tried) return;
            _tried = true;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            { try { var t = a.GetType("Studio.GuideObjectManager", false); if (t != null) { _gomType = t; break; } } catch { } }
            if (_gomType == null)
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                { try { foreach (var t in a.GetTypes()) if (t != null && t.Name == "GuideObjectManager") { _gomType = t; break; } } catch { } if (_gomType != null) break; }
            if (_gomType == null) return;
            for (var t = _gomType; t != null && _instProp == null && _instField == null; t = t.BaseType)   // Singleton<GuideObjectManager> owns the static Instance
            {
                _instProp = t.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                _instField = t.GetField("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            }
            _dicField = _gomType.GetField("dicGuideObject", BF);
        }

        private static object Instance()
        {
            try { if (_instProp != null) return _instProp.GetValue(null, null); if (_instField != null) return _instField.GetValue(null); } catch { }
            return null;
        }

        // True/false if `t` is a live Studio guide object; null if it can't be determined.
        public static bool? IsGuideObject(Transform t)
        {
            Init();
            if (_gomType == null || _dicField == null || t == null) return null;
            try
            {
                object inst = Instance();
                if (inst == null) return null;
                var dic = _dicField.GetValue(inst) as System.Collections.IDictionary;
                if (dic == null) return null;
                return dic.Contains(t);
            }
            catch { return null; }
        }
    }

    internal static class AutoBodyReveal
    {
        private static bool _subscribed;
        private const string VaginaBone = "cf_J_Vagina_root";   // BP/uncensor-provided female vagina bone (primary match)
        private const string FallbackBone = "cf_j_kokan";       // vanilla female crotch bone (lowercase j is intentional — NOT cf_J_Kokan, a different aibu bone); fallback when no character has the BP bone
        private const float  PenisWombRange = 0.5f;             // a penis is attached to a womb only if its tip (k_f_dan_end) is within this of penis_target — stops a lone male being yanked to a DIFFERENT female's womb (real couple ~0.1m; wrong ~1.2m)
        private const int    HotkeyBuild = 15;                  // bump per plugin build so the log identifies the loaded DLL

        public static bool Enabled { get; set; } = true;
        public static bool Debug { get; set; } = false;
        // World-distance within which a womb's entrance counts as "inside this character's vagina".
        // Tunable via config. A womb placed in the vagina sits a few cm from cf_J_Vagina_root; a
        // separately-spawned character is far -> excluded.
        public static float MaxRange = 0.15f;

        // Subscribe once to KKAPI CharacterApi.CharacterReloaded on EVERY loaded copy (the install
        // can have more than one KKAPI assembly; subscribing to all + idempotent apply is safe).
        public static void Init()
        {
            if (_subscribed) return;
            _subscribed = true;
            int hooks = 0;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t;
                try { t = a.GetType("KKAPI.Chara.CharacterApi", false); }
                catch { t = null; }
                if (t == null) continue;
                try
                {
                    var ev = t.GetEvent("CharacterReloaded", BindingFlags.Static | BindingFlags.Public);
                    if (ev == null) continue;
                    var mi = typeof(AutoBodyReveal).GetMethod("OnCharacterReloaded", BindingFlags.Static | BindingFlags.NonPublic);
                    Delegate del = null;
                    try { del = Delegate.CreateDelegate(ev.EventHandlerType, mi); }      // relaxed (contravariant args)
                    catch { try { del = Delegate.CreateDelegate(ev.EventHandlerType, null, mi); } catch { } }
                    if (del == null) continue;
                    ev.AddEventHandler(null, del);
                    hooks++;
                }
                catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("AutoBodyReveal: subscribe failed: " + e.Message); }
            }
            if (hooks > 0) LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: subscribed to CharacterReloaded (" + hooks + " hook(s)).");
            else LiquidWobbleMPBPlugin._logger?.LogWarning("AutoBodyReveal: CharacterApi.CharacterReloaded not found (KKAPI missing?). Swap auto-apply disabled; manual hotkey still works.");
        }

        // EventHandler<CharacterReloadedEventArgs> via relaxed binding (param typed as the base EventArgs).
        // Install the two BP-interop Harmony patches (dan-dup guard + penis-FK enforcer) once a CloXray womb is
        // actually present. Both are idempotent (a private _applied guard patches at most once) and BP-lazy (no-op
        // until BP's assembly loads, then they take). Called from the womb's OnEnable, the scene-load penis-bend
        // coroutine, and a womb-gated CharacterReloaded — whichever first sees both a womb and BP. Their prefix/postfix
        // additionally early-out on WombExpandEffect.AnyActive, so removing the last womb makes them inert again.
        internal static void InstallWombHooks()
        {
            BPDanReaddGuard.TryApply();
            PenisFKEnforcer.TryApply();
            NodeConstraintBridge.InstallPairingHooks();   // instant womb<->penis re-pair on any k_f_dan_entry NodesConstraint add/enable/disable/delete
        }

        private static void OnCharacterReloaded(object sender, EventArgs e)
        {
            // A (re)load can change the vagina-root list AND the womb<->penis pairing candidates -> refresh both event-driven.
            WombExpandEffect.InvalidateVaginaRoots();
            WombExpandEffect.RequestRepair();
            // The BP-interop hooks are CloXray-womb-scoped: install them only while a womb is in the scene, so a
            // no-CloXray scene is never patched. (They also early-out on AnyActive at runtime, so a leftover patch is
            // inert once the womb is gone.)
            if (WombExpandEffect.AnyActive) InstallWombHooks();
            if (!Enabled) return;
            try
            {
                var p = e.GetType().GetProperty("ReloadedCharacter");
                var cc = p != null ? p.GetValue(e, null) as Component : null;
                // DEFER: run the re-apply AFTER MaterialEditor has restored this body's saved BodyReveal copy
                // (ME's own CharacterReloaded controller has no ordering guarantee vs ours). Deferred, we ADOPT the
                // saved copy (zero stencil writes — the load-reset fix); only a copy that never appears (a genuinely
                // fresh/swapped-in character) is created. See WobbleSceneController.DeferApply.
                if (cc != null) WobbleSceneController.DeferApply(cc);
            }
            catch (Exception ex) { LiquidWobbleMPBPlugin._logger?.LogWarning("AutoBodyReveal.OnCharacterReloaded: " + ex.Message); }
        }

        // On character load/change: if a CloXray womb sits IN this character's vagina (entrance within
        // MaxRange of cf_J_Vagina_root), (re)apply BodyReveal. Proximity-based, NO in-memory arming — so it
        // survives a scene save/load: load a scene whose char has a womb in the vagina -> applies; change
        // that character -> the womb is still in the (new) vagina -> re-applies. A freshly spawned character
        // lands away from the womb -> not applied (the over-trigger fix).
        // The actual worker — run DEFERRED (via WobbleSceneController.DeferApply) so it executes after ME restore.
        public static void ApplyForCharacterNow(Component cc)
        {
            if (!LiquidWobbleMPBPlugin.CfgEnabled) return;   // master toggle OFF -> never stamp materials
            if (cc == null) return;
            Transform vagina = FindChild(cc.transform, VaginaBone);
            if (vagina == null) { if (Debug) LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: '" + cc.name + "' has no " + VaginaBone + " (non-BP body?); skipping."); return; }

            WombExpandEffect best = null; float bestSq = float.MaxValue;
            foreach (var w in UnityEngine.Object.FindObjectsOfType<WombExpandEffect>())
            {
                if (w == null) continue;
                float d = (w.EntranceWorld() - vagina.position).sqrMagnitude;
                if (d < bestSq) { bestSq = d; best = w; }
            }
            float dist = best != null ? Mathf.Sqrt(bestSq) : 999f;
            if (best == null || dist > MaxRange)
            {
                if (Debug) LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: '" + cc.name + "' nearest womb " + (best == null ? "none" : dist.ToString("F3") + "m") + " > range " + MaxRange.ToString("F2") + "m; not its womb (e.g. fresh spawn) -> skip.");
                return;
            }
            int st = best.OrganStencil();
            // overwriteExisting=false: on LOAD/reload, ensure the reveal exists but NEVER re-stamp an existing copy's
            // stencil — the womb's _StencilBody may not be MaterialEditor-restored yet, so a re-derive would clobber
            // the user's saved non-default pair (8/12/16) with the stale default 4. The hotkey (ApplyAll) uses true.
            if (MEBridge.EnsureBodyReveal(cc, st, Debug, false))
                best.OnBodyRevealApplied();   // restore out-of-body interior+cum (new-spawn default hides them)
            if (LiquidWobbleMPBPlugin.CfgBodyVeil) MEBridge.EnsureBodyVeil(cc, st + 1, Debug, false);
            if (LiquidWobbleMPBPlugin.CfgClothesReveal) MEBridge.EnsureClothesReveal(cc, st, Debug);
        }

        // Manual hotkey: apply now to every character that has a womb within MaxRange of its vagina
        // (covers the initial placement, where no reload event fires).
        public static void ApplyAll()
        {
            if (!LiquidWobbleMPBPlugin.CfgEnabled) return;   // master toggle OFF -> hotkey does nothing
            AttachLiquidWobbleSelected();      // bottles etc.: attach the wobble driver to the SELECTED item(s) only
            var wombs = UnityEngine.Object.FindObjectsOfType<WombExpandEffect>();
            LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: manual apply — " + wombs.Length + " womb(s), MaxRange=" + MaxRange.ToString("F3") + "m.");
            if (wombs.Length == 0) { LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: manual apply - no CloXray wombs in scene."); return; }
            if (Debug) NodeConstraintBridge.DumpConstraints("constraints BEFORE apply");
            foreach (var w in wombs)
            {
                if (w == null) continue;
                Vector3 _ew = w.EntranceWorld();
                if (Debug) LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: womb '" + w.name + "' entranceWorld=" + _ew.ToString("F3") + " itemRoot=" + w.transform.position.ToString("F3") + (_ew == w.transform.position ? "  (!! cf_j_kokan NOT found -> using item root)" : ""));
                Component cc = FindNearestCharacter(_ew, MaxRange);
                if (cc != null)
                {
                    int st = w.OrganStencil();
                    // overwriteExisting=true: the HOTKEY is an explicit user action — sync the body/veil stencil to the
                    // womb's CURRENT pair (this is how you re-stamp after changing a womb's StencilBody).
                    if (MEBridge.EnsureBodyReveal(cc, st, true, true))
                        w.OnBodyRevealApplied();   // restore out-of-body interior+cum (new-spawn default hides them)
                    if (LiquidWobbleMPBPlugin.CfgBodyVeil) MEBridge.EnsureBodyVeil(cc, st + 1, true, true);
                    if (LiquidWobbleMPBPlugin.CfgClothesReveal) MEBridge.EnsureClothesReveal(cc, st, true);
                }
                else LiquidWobbleMPBPlugin._logger?.LogWarning("AutoBodyReveal: manual apply - no character within " + MaxRange.ToString("F2") + "m of womb '" + w.name + "'.");
                // AUTO penis: x-ray + aim the PENETRATOR (the OTHER character that has a penis) at THIS womb. No
                // selection needed, so it can't grab the receiver's own penis or duplicate across both partners.
                ApplyPenisForWomb(w, cc);
            }
            WombExpandEffect.RequestRepair();   // the hotkey may have added/aimed NC links -> re-pair every womb to its penis now
        }

        // Attach the wobble driver to ONE item's CloXray/Liquid renderer. Used by the selected-item hotkey
        // AND by the scene-load re-attach (WobbleSceneController). Idempotent: skips if the item already has
        // one or has no CloXray/Liquid mesh.
        public static void AttachWobbleTo(GameObject go)
        {
            if (go == null || go.GetComponentInChildren<LiquidWobbleMPBEffect>(true) != null) return;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || r.sharedMaterials == null) continue;
                foreach (var m in r.sharedMaterials)
                    if (m != null && m.shader != null && m.shader.name == "CloXray/Liquid")
                    {
                        r.gameObject.AddComponent<LiquidWobbleMPBEffect>();
                        LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: attached wobble driver to '" + go.name + "'.");
                        return;
                    }
            }
        }

        // Hotkey path: attach the wobble to the SELECTED Studio item(s) only — no scene-wide scan. The
        // attachment persists with the scene via WobbleSceneController (KKAPI ExtensibleSave).
        public static void AttachLiquidWobbleSelected()
        {
            try
            {
                foreach (var oci in KKAPI.Studio.StudioAPI.GetSelectedObjects())
                    if (oci != null && oci.guideObject != null && oci.guideObject.transformTarget != null)
                        AttachWobbleTo(oci.guideObject.transformTarget.gameObject);
            }
            catch (System.Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("AutoBodyReveal: selected-wobble attach failed (Studio/KKAPI?): " + e.Message); }
        }

        // For ONE womb: x-ray the PENETRATOR's penis and aim it at this womb. Penetrator = the nearest character
        // (OTHER than the womb's owner `receiver`) that carries a real penis (cm_m_dankon) — fully automatic, no
        // selection, so it can't grab the receiver's OWN penis (both partners may be cm_m_dankon) or duplicate. The
        // penis -> CloXray/OrgInside (stencil matched to this womb) + two position NodesConstraints: k_f_dan_entry ->
        // the receiver's vagina, k_f_dan_end -> THIS womb's penis_target. Idempotent (dedup by child) + scene-persistent.
        private static void ApplyPenisForWomb(WombExpandEffect w, Component receiver)
        {
            try
            {
                Transform target = FindChild(w.transform, "penis_target");
                if (target == null) { LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: womb '" + w.name + "' has no penis_target bone."); return; }
                var all = UnityEngine.Object.FindObjectsOfType<Transform>();
                Component penetrator;
                Transform end = NearestPenisEnd(all, target.position, receiver, out penetrator);   // the OTHER character's penis
                if (penetrator == null || end == null)
                {
                    LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: womb '" + w.name + "': no penetrator (a cm_m_dankon character other than the receiver) found.");
                    return;
                }
                // Is that nearest penis actually IN this womb? With one male and several females-each-with-a-womb,
                // every womb finds the SAME lone male — only the womb he's really in should claim him, else he gets
                // dragged to a far female's vagina. Real couple ~0.1m; a male across the room ~1.2m.
                float penisDist = Vector3.Distance(end.position, target.position);
                if (penisDist > PenisWombRange)
                {
                    LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: womb '" + w.name + "': nearest penis ('" + penetrator.name + "') is " + penisDist.ToString("F2") + "m from penis_target (> " + PenisWombRange.ToString("F2") + "m) -> that male is in a DIFFERENT womb; skipping penis x-ray + aiming for this one.");
                    return;
                }
                Transform entry  = FindChild(penetrator.transform, "k_f_dan_entry");
                Transform vagina = receiver != null ? (FindChild(receiver.transform, VaginaBone) ?? FindChild(receiver.transform, FallbackBone)) : null;
                int st = w.OrganStencil();
                LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: womb '" + w.name + "' -> receiver='" + (receiver != null ? receiver.name : "none") + "' penetrator='" + penetrator.name + "' vagina=" + (vagina != null ? "'" + vagina.name + "'" : "NONE") + " stencil=" + st + ".");
                if (Debug)
                {
                    // POSITION READOUT: is penis_target actually AT the womb? target<->entrance small = good.
                    Vector3 ewp = w.EntranceWorld();
                    LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal:   POS penis_target=" + target.position.ToString("F2") + " wombEntrance=" + ewp.ToString("F2") + " (target<->entrance=" + Vector3.Distance(target.position, ewp).ToString("F2") + "m)  penisEnd=" + end.position.ToString("F2") + " (end<->target=" + Vector3.Distance(end.position, target.position).ToString("F2") + "m)" + (vagina != null ? "  vagina=" + vagina.position.ToString("F2") : ""));
                }
                // penis_target is BAKED at the womb's tube centre in the mod -> the plugin just constrains the penis
                // to it. A childCount>0 bone is the OLD load-bearing penis_target (stale womb build) -> warn loud.
                if (target.childCount > 0)
                    LiquidWobbleMPBPlugin._logger?.LogWarning("AutoBodyReveal: penis_target has " + target.childCount + " child bone(s) -> OLD load-bearing bone; rebuild the womb with the centred leaf aim bone.");
                else if (Debug)
                {
                    Vector3 tfoot;
                    if (w.SnapToTubeCenter(target.position, out tfoot))
                        LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal:   penis_target offCentre=" + ((target.position - tfoot).magnitude * 1000f).ToString("F1") + "mm from tube centre.");
                    LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal:   GUIDE-OBJ? penis_target=" + GuideObjectBridge.IsGuideObject(target) + " vagina=" + (vagina != null ? GuideObjectBridge.IsGuideObject(vagina) : null) + " k_f_dan_entry=" + (entry != null ? GuideObjectBridge.IsGuideObject(entry) : null) + " k_f_dan_end=" + GuideObjectBridge.IsGuideObject(end) + "  (blank = couldn't read)");
                }
                MEBridge.EnsurePenisOrgInside(penetrator, st, true);   // x-ray the penetrator's penis, matched to THIS womb
                if (NodeConstraintBridge.Available)
                {
                    // NEVER reassign a dan node the user already targeted by hand (wired EITHER direction — e.g. aimed at their own spheres).
                    if (entry != null && vagina != null)
                    {
                        if (NodeConstraintBridge.HasConstraintForNode(entry)) LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: k_f_dan_entry already has a constraint -> leaving your target, not reassigning.");
                        else NodeConstraintBridge.AddPositionLink(vagina, entry, "");   // empty alias -> shows raw "parent -> child" like a manual one
                    }
                    if (NodeConstraintBridge.HasConstraintForNode(end)) LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: k_f_dan_end already has a constraint -> leaving your target, not reassigning.");
                    else NodeConstraintBridge.AddPositionLink(target, end, "");
                }
                else LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: NodesConstraints not present -> penis x-rayed but not aimed.");
            }
            catch (System.Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("AutoBodyReveal: penis-for-womb failed on '" + (w != null ? w.name : "?") + "': " + e.Message); }
        }

        // Nearest k_f_dan_end under a ChaControl that carries a cm_m_dankon penis, OTHER than `exclude` (the receiver).
        private static Transform NearestPenisEnd(Transform[] all, Vector3 pos, Component exclude, out Component owner)
        {
            Transform best = null; float bsq = float.MaxValue; owner = null;
            foreach (var t in all)
            {
                if (t == null || t.name != "k_f_dan_end") continue;
                Component cc = FindChaControlOf(t);
                if (cc == null || cc == exclude || !MEBridge.HasPenisMaterial(cc)) continue;
                float d = (t.position - pos).sqrMagnitude; if (d < bsq) { bsq = d; best = t; owner = cc; }
            }
            return best;
        }

        private static Component FindChaControlOf(Transform t)
        {
            for (var c = t; c != null; c = c.parent) { var cc = c.GetComponent("ChaControl"); if (cc != null) return cc; }
            return null;
        }

        // Nearest character (ChaControl) to a world point, within maxRange. Walks up to the ChaControl via
        // GetComponent("ChaControl") (string overload) so there is no ChaControl type reference. Matches the
        // BP vagina bone first; if NO character has it (BP not set up on the female), falls back to the vanilla
        // cf_j_kokan crotch bone (female-gated) so the x-ray still applies without BP. BP scenes are unaffected
        // — the first pass finds candidates and the fallback never runs.
        private static Component FindNearestCharacter(Vector3 pos, float maxRange = float.MaxValue)
        {
            int cand;
            Component best = FindNearestByBone(pos, maxRange, VaginaBone, false, out cand);
            if (cand == 0)
            {
                LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal:   no character has '" + VaginaBone + "' (BP not set up?) -> fallback to vanilla '" + FallbackBone + "' (female-only).");
                best = FindNearestByBone(pos, maxRange, FallbackBone, true, out cand);
            }
            return best;
        }

        // Nearest ChaControl owning a bone named `boneName` to `pos`, within maxRange. femaleOnly restricts to
        // female characters (vanilla cf_j_kokan exists on males too, so the fallback must not grab one).
        // candCount = qualifying candidates found (0 -> the caller may try a fallback bone).
        private static Component FindNearestByBone(Vector3 pos, float maxRange, string boneName, bool femaleOnly, out int candCount)
        {
            Component best = null; float bestSq = float.MaxValue; int cand = 0; string bestName = "none";
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
            {
                if (t == null || t.name != boneName) continue;
                if (t.GetComponentInParent<WombExpandEffect>() != null) continue;   // NEVER the womb ITEM's own reused cf_j_kokan bone (it can be parented under a character -> would self-match at dist~0)
                Component cc = null;
                for (var c = t; c != null; c = c.parent) { cc = c.GetComponent("ChaControl"); if (cc != null) break; }
                if (cc == null) continue;   // not under a character (e.g. a free-standing womb item) -> skip
                if (femaleOnly && !IsFemale(cc)) { LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal:   skip '" + cc.name + "' (not female) for fallback bone."); continue; }
                cand++;
                float d = (t.position - pos).sqrMagnitude;
                LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal:   candidate '" + cc.name + "' " + boneName + "@" + t.position.ToString("F3") + " dist=" + Mathf.Sqrt(d).ToString("F3") + "m");   // DIAG
                if (d < bestSq) { bestSq = d; best = cc; bestName = cc.name; }
            }
            candCount = cand;
            float bestDist = best != null ? Mathf.Sqrt(bestSq) : -1f;   // DIAG
            LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal:   -> " + cand + " '" + boneName + "' candidate(s) under a ChaControl; nearest '" + bestName + "' dist=" + (bestDist >= 0f ? bestDist.ToString("F3") + "m" : "n/a") + " vs maxRange=" + maxRange.ToString("F3") + "m" + (bestDist >= 0f && bestDist > maxRange ? "  -> REJECTED (raise the range or move the womb)" : ""));   // DIAG
            if (best != null && maxRange < float.MaxValue && Mathf.Sqrt(bestSq) > maxRange) return null;
            return best;
        }

        // Female? Read the character's sex by reflection (no hard ChaControl ref). In KK the value lives on
        // ChaFileParameter, NOT directly on ChaControl, so try cc.fileParam.sex then cc.chaFile.parameter.sex.
        // It is ChaFileDefine.Sex: 0 = male, 1 = female (do NOT flip this). Unknown -> treat as female: the
        // fallback bone is female-only anyway and the nearest+range gate still favours the womb's own character.
        private static bool IsFemale(Component cc)
        {
            object sexVal = null;
            object fp = TryGetMember(cc, "fileParam"); if (fp != null) sexVal = TryGetMember(fp, "sex");
            if (sexVal == null) { object f = TryGetMember(cc, "chaFile"); object pr = f != null ? TryGetMember(f, "parameter") : null; if (pr != null) sexVal = TryGetMember(pr, "sex"); }
            if (sexVal == null)
            {
                LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal:   (could not read sex of '" + cc.name + "' -> treating as female)");
                return true;
            }
            try { return System.Convert.ToInt32(sexVal) == 1; } catch { return true; }
        }

        private static object TryGetMember(object o, string name)
        {
            if (o == null) return null;
            const System.Reflection.BindingFlags BF = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            try
            {
                var t = o.GetType();
                var fi = t.GetField(name, BF); if (fi != null) return fi.GetValue(o);
                var pi = t.GetProperty(name, BF); if (pi != null) return pi.GetValue(o, null);
            }
            catch { }
            return null;
        }

        private static Transform FindChild(Transform root, string childName)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t != null && t.name == childName) return t;
            return null;
        }
    }
}
