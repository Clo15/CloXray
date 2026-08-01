using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace LiquidWobbleMPB
{
    /// Auto-applies the CloXray "BodyReveal" stencil-writer to a character's body by DRIVING
    /// MaterialEditor's API (reflection, no hard dependency.
    internal static class MEBridge
    {
        public const string BodyRevealShader = "CloXray/BodyReveal";
        public const string BodyVeilShader   = "CloXray/BodyRevealExtra";
        public const string OrgInsideShader  = "CloXray/OrgInside";   // applied to a male penis material so it x-rays through the body.
        private const string PenisMat        = "cm_m_dankon";   // the male penis material name.
        private const string BallsMat        = "cm_m_dan_f";   // the male balls material (o_dan_f).
        private const int   BodyVeilQueue    = 3504;   // after the whole womb stack (organ 3500, interior 3502, cum 3503).

        private static bool _tried;
        private static Type _ctrlType;   // KK_Plugins.MaterialEditor.MaterialEditorCharaController.
        private static Type _objType;   // nested ObjectType enum.
        private static object _otCharacter;   // ObjectType.Character (boxed).
        private static object _otClothing;   // ObjectType.Clothing (boxed; resolved by name scan).
        private static object _otAccessory;   // ObjectType.Accessory (boxed; resolved by name scan).
        private static MethodInfo _mCopyRemove, _mSetShader, _mSetFloat, _mSetQueue;
        private static MethodInfo _mRemoveShader, _mRemoveShaderQueue;   // ME's reset: restores the ORIGINAL shader and deletes the persisted edit.
        private static FieldInfo _fCopyList;   // MaterialEditor's own record of the copies it created.
        private static FieldInfo _fCopyName, _fCopySource;

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
                // Clothing/Accessory member names vary in capitalization across ME builds.
                foreach (var n in Enum.GetNames(_objType))
                {
                    string ln = n.ToLowerInvariant();
                    if (_otClothing == null && ln.Contains("cloth")) _otClothing = Enum.Parse(_objType, n);
                    if (_otAccessory == null && ln.Contains("acces")) _otAccessory = Enum.Parse(_objType, n);
                }
                _mCopyRemove = _ctrlType.GetMethod("MaterialCopyRemove",
                    new[] { typeof(int), _objType, typeof(Material), typeof(GameObject) });
                _mSetShader = _ctrlType.GetMethod("SetMaterialShader",
                    new[] { typeof(int), _objType, typeof(Material), typeof(string), typeof(GameObject), typeof(bool) });
                _mSetFloat = _ctrlType.GetMethod("SetMaterialFloatProperty",
                    new[] { typeof(int), _objType, typeof(Material), typeof(string), typeof(float), typeof(GameObject), typeof(bool) });
                // Queue setter (for the veil copy, which must land at 3502).
                try { _mSetQueue = _ctrlType.GetMethod("SetMaterialShaderRenderQueue");
                _mRemoveShader      = _ctrlType.GetMethod("RemoveMaterialShader", new Type[] { typeof(int), _objType, typeof(Material), typeof(GameObject), typeof(bool) });
                _mRemoveShaderQueue = _ctrlType.GetMethod("RemoveMaterialShaderRenderQueue", new Type[] { typeof(int), _objType, typeof(Material), typeof(GameObject), typeof(bool) }); } catch { _mSetQueue = null; }
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

        // The existing-check for the reveal/veil stamps: the shader counts as applied only when it sits on a
        // .MECopy.
        private static Material FindConfiguredCopy(SkinnedMeshRenderer r, string shaderName)
        {
            foreach (var m in r.sharedMaterials)
                if (m != null && m.shader != null && m.shader.name == shaderName && BaseName(m).Contains(".MECopy")) return m;
            return null;
        }

        // MaterialEditor records every copy it creates in its MaterialCopyList as (MaterialName ->
        // MaterialCopyName).
        private static System.Collections.IList CopyRows(object me)
        {
            if (_fCopyList == null) _fCopyList = _ctrlType.GetField("MaterialCopyList", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return _fCopyList != null ? _fCopyList.GetValue(me) as System.Collections.IList : null;
        }

        private static string CopyRowName(object row, bool source)
        {
            if (row == null) return null;
            var ty = row.GetType();
            if (source)
            {
                if (_fCopySource == null) _fCopySource = ty.GetField("MaterialName");
                return _fCopySource != null ? _fCopySource.GetValue(row) as string : null;
            }
            if (_fCopyName == null) _fCopyName = ty.GetField("MaterialCopyName");
            return _fCopyName != null ? _fCopyName.GetValue(row) as string : null;
        }

        // Creates a copy of srcBody through ME (so it is persisted) and returns exactly that copy.
        private static Material CreateCopyTracked(object me, SkinnedMeshRenderer bodyR, Material srcBody, GameObject go, string what)
        {
            string src = BaseName(srcBody);
            var rows = CopyRows(me);
            if (rows == null)
            { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: MaterialEditor's MaterialCopyList is unreadable - cannot create the " + what + " copy safely (ME version changed?)."); return null; }
            var before = new System.Collections.Generic.HashSet<string>();
            foreach (var r in rows) { string n = CopyRowName(r, false); if (n != null) before.Add(n); }

            _mCopyRemove.Invoke(me, new object[] { 0, _otCharacter, srcBody, go });

            rows = CopyRows(me);
            string made = null;
            if (rows != null)
                foreach (var r in rows)
                {
                    string n = CopyRowName(r, false);
                    if (n == null || before.Contains(n)) continue;
                    if (CopyRowName(r, true) != src) continue;   // a copy of a different material.
                    made = n;   // last new row for this source wins.
                }
            if (made == null)
            { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: MaterialEditor created no " + what + " copy of '" + src + "' (its MaterialCopyList gained no row) - the " + what + " was NOT applied."); return null; }
            var mat = FindByName(bodyR, made);
            if (mat == null)
            { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: MaterialEditor recorded the " + what + " copy '" + made + "' but it is not on renderer '" + bodyR.name + "' - the " + what + " was NOT applied."); return null; }
            if (!BaseName(mat).Contains(".MECopy"))
            { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: refusing to configure '" + mat.name + "' as the " + what + " - it is not a copy."); return null; }
            return mat;
        }

        // Corrupted-save repair: the ORIGINAL body material carrying a CloXray shader draws no skin
        // (stencil-writer passes write no color).
        private static void RepairOriginalBodyMaterial(object me, SkinnedMeshRenderer r, GameObject go, Component cc)
        {
            foreach (var m in r.sharedMaterials)
            {
                if (m == null || m.shader == null || !m.shader.name.StartsWith("CloXray/") || BaseName(m).Contains(".MECopy")) continue;
                if (_mRemoveShader == null)
                { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: ORIGINAL body material '" + m.name + "' on '" + cc.name + "' carries " + m.shader.name + " but ME RemoveMaterialShader is unavailable - reset that material's shader in the MaterialEditor UI."); continue; }
                LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: ORIGINAL body material '" + m.name + "' on '" + cc.name + "' carried " + m.shader.name + " (corrupted save state - this is what makes the body invisible). Restoring its original shader via MaterialEditor.");
                try
                {
                    _mRemoveShader.Invoke(me, new object[] { 0, _otCharacter, m, go, true });
                    if (_mRemoveShaderQueue != null) _mRemoveShaderQueue.Invoke(me, new object[] { 0, _otCharacter, m, go, true });
                    // ME's remove only clears rows under the current coordinate index; the poison was
                    // written per coordinate index; sweeping every index covers any layout MaterialEditor used.
                    int purged = PurgePersistedCloRows(me, BaseName(m));
                    if (purged > 0) LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: purged " + purged + " persisted CloXray row(s) for '" + BaseName(m) + "' across all outfits.");
                }
                catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: original-shader restore failed on '" + m.name + "': " + e.Message); }
            }
        }

        private static object GetMember(object obj, string name)
        {
            if (obj == null) return null;
            const BindingFlags BI = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var tp = obj.GetType();
            var pr = tp.GetProperty(name, BI);
            if (pr != null) { try { return pr.GetValue(obj, null); } catch { return null; } }
            var fi = tp.GetField(name, BI);
            return fi != null ? fi.GetValue(obj) : null;
        }

        // Deletes persisted ME rows that put a CloXray shader (or a CloXray-range queue) on the named
        // NON-copy material, on every outfit coordinate.
        private static int PurgePersistedCloRows(object me, string matName)
        {
            int purged = 0;
            foreach (var listName in new[] { "MaterialShaderList", "MaterialFloatPropertyList" })
            {
                var list = GetMember(me, listName) as System.Collections.IList;
                if (list == null) continue;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var row = list[i];
                    if (row == null || (GetMember(row, "MaterialName") as string) != matName) continue;
                    bool poison = false;
                    if (listName == "MaterialShaderList")
                    {
                        string sn = GetMember(row, "ShaderName") as string;
                        object rq = GetMember(row, "RenderQueue");
                        if (sn != null && sn.StartsWith("CloXray/")) poison = true;
                        else if (string.IsNullOrEmpty(sn) && rq != null)
                        { try { int q = Convert.ToInt32(rq); if (q >= 3490 && q <= 3505) poison = true; } catch { } }
                    }
                    else
                    {
                        string pr = GetMember(row, "Property") as string;
                        if (pr == "StencilRef" || pr == "XrayAlpha" || pr == "StencilBody_Plus_1") poison = true;
                    }
                    if (poison) { list.RemoveAt(i); purged++; }
                }
            }
            return purged;
        }

        public static void DumpBodyState(Component cc, string tag)
        {
            try
            {
                var bodyR = FindBodyRenderer(cc);
                if (bodyR == null) { LiquidWobbleMPBPlugin._logger?.LogInfo("BODY-DUMP[" + tag + "] '" + cc.name + "': no o_body_* renderer."); return; }
                var sb = new System.Text.StringBuilder();
                sb.Append("BODY-DUMP[").Append(tag).Append("] '").Append(cc.name).Append("' renderer='").Append(bodyR.name)
                  .Append("' enabled=").Append(bodyR.enabled).Append(" active=").Append(bodyR.gameObject.activeInHierarchy)
                  .Append(" mats=").Append(bodyR.sharedMaterials.Length);
                foreach (var m in bodyR.sharedMaterials)
                {
                    if (m == null) { sb.Append("\n  <null material>"); continue; }
                    sb.Append("\n  '").Append(m.name).Append("' shader='").Append(m.shader != null ? m.shader.name : "<none>")
                      .Append("' q=").Append(m.renderQueue);
                    if (m.HasProperty("_StencilRef")) sb.Append(" stencilRef=").Append(m.GetFloat("_StencilRef"));
                    if (m.HasProperty("_Color")) sb.Append(" colA=").Append(m.GetColor("_Color").a.ToString("F2"));
                }
                LiquidWobbleMPBPlugin._logger?.LogInfo(sb.ToString());
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("BODY-DUMP failed on '" + (cc ? cc.name : "?") + "': " + e.Message); }
        }

        // Unity appends " (Instance)" to a material's name every time something instantiates the renderer's
        // material array (each ME operation does), so runtime names drift to "cf_m_body (Instance) (Instance)..." while ME keeps naming copies from the clean base ("cf_m_body.MECopy1").
        private static string BaseName(Material m)
        {
            string n = m != null ? m.name : "";
            const string suf = " (Instance)";
            while (n.EndsWith(suf)) n = n.Substring(0, n.Length - suf.Length);
            return n;
        }

        private static Material FindByName(SkinnedMeshRenderer r, string exact)
        {
            foreach (var m in r.sharedMaterials)
                if (m != null && BaseName(m) == exact) return m;
            return null;
        }

        // A FRESH (not-yet-configured) ME copy: name matches, but no CloXray shader assigned yet.
        internal static int RemoveXrayCopies(Component cc, bool debug)
        {
            Init();   // same lazy resolve the Ensure* apply paths do.
            // _ctrlType null -> GetComponent((Type)null) THROWS, and this runs inside the toggle-off of the
            // nudge-bake respawn coroutine.
            if (cc == null || _ctrlType == null || _mCopyRemove == null)
            {
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: cannot remove the x-ray copies from '"
                    + (cc != null ? cc.name : "?") + "' — MaterialEditor is not resolved (type="
                    + (_ctrlType != null) + ", copyRemove=" + (_mCopyRemove != null) + "). Nothing removed.");
                return 0;
            }
            var me = cc.GetComponent(_ctrlType);
            if (me == null)
            {
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: cannot remove the x-ray copies from '"
                    + (cc != null ? cc.name : "?") + "' — MaterialEditor's MaterialCopyRemove is unavailable. Nothing removed.");
                return 0;
            }
            int removed = 0;
            // CLOTHES first. ME addresses a garment by its slot index + the Clothing object type; giving it
            // the Character type (as the body/penis pass does) makes it look in the wrong list and drop nothing.
            if (_otClothing != null)
            {
                GameObject[] slots = null;
                const BindingFlags ANY2 = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var fC2 = cc.GetType().GetField("objClothes", ANY2);
                if (fC2 != null) slots = fC2.GetValue(cc) as GameObject[];
                if (slots == null)
                {
                    var pC2 = cc.GetType().GetProperty("objClothes", ANY2);
                    if (pC2 != null) slots = pC2.GetValue(cc, null) as GameObject[];
                }
                if (slots != null)
                    foreach (int kind in ClothesKinds)
                    {
                        if (kind >= slots.Length || slots[kind] == null) continue;
                        foreach (var r in slots[kind].GetComponentsInChildren<Renderer>(true))
                            removed += RemoveCopiesOn(me, r, kind, _otClothing, slots[kind], cc.name);
                    }
            }
            foreach (var r in cc.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                removed += RemoveCopiesOn(me, r, 0, _otCharacter, GetBodyGo(cc) ?? (r != null ? r.gameObject : null), cc.name);
            }
            if (debug || removed > 0)
                LiquidWobbleMPBPlugin._logger?.LogInfo("MEBridge: removed " + removed + " x-ray copy/copies from '" + cc.name + "'.");
            return removed;
        }

        // Hand ME the COPY itself: MaterialCopyRemove branches on the name it is given.
        private static int RemoveCopiesOn(object me, Renderer r, int slot, object objType, GameObject go, string who)
        {
            if (r == null || r.sharedMaterials == null || go == null) return 0;
            var copies = new System.Collections.Generic.List<Material>();
            foreach (var m in r.sharedMaterials)
                if (m != null && m.shader != null && m.shader.name.StartsWith("CloXray/")
                    && BaseName(m).Contains(".MECopy")) copies.Add(m);
            int n = 0;
            foreach (var copy in copies)
            {
                int before = RowCount(me);
                _mCopyRemove.Invoke(me, new object[] { slot, objType, copy, go });
                if (RowCount(me) >= before)
                {
                    LiquidWobbleMPBPlugin._logger?.LogError("CloXray: MaterialEditor did not drop the copy row for '"
                        + copy.name + "' on '" + who + "' — leaving the rest in place.");
                    break;
                }
                n++;
            }
            return n;
        }

        private static int RowCount(object me)
        { var rows = CopyRows(me); return rows == null ? -1 : rows.Count; }

        private static Material FindCopy(SkinnedMeshRenderer r, string srcName)
        {
            foreach (var m in r.sharedMaterials)
                if (m != null && BaseName(m).StartsWith(srcName + ".MECopy") &&
                    (m.shader == null || !m.shader.name.StartsWith("CloXray/"))) return m;
            return null;
        }

        /// Idempotently ensure the body has a BodyReveal copy at the given stencil.
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
            // The reveal/veil/clothes stamps dress the womb's WEARER.
            if (MainGameWomb.IsMaleChara(cc)) { if (debug) LiquidWobbleMPBPlugin._logger?.LogInfo("MEBridge: '" + cc.name + "' is male - the body reveal stamp only applies to the wearer."); return false; }
            if (_ctrlType == null) { if (debug) LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: ME unavailable; cannot auto-apply BodyReveal."); return false; }

            try
            {
                var me = cc.GetComponent(_ctrlType);
                if (me == null) { if (debug) LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: no MaterialEditor controller on '" + cc.name + "'."); return false; }

                var bodyR = FindBodyRenderer(cc);
                if (bodyR == null) { if (debug) LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: body renderer (o_body*) not found on '" + cc.name + "'."); return false; }

                // GameObject ME expects for body edits = ChaControl.objBody (field OR property, varies by
                // game build); fall back to the renderer's GO (ME accepts it).
                GameObject go = GetBodyGo(cc) ?? bodyR.gameObject;

                // Already applied? On the HOTKEY (overwriteExisting) keep the stencil synced to the womb.
                RepairOriginalBodyMaterial(me, bodyR, go, cc);
                var existing = FindConfiguredCopy(bodyR, BodyRevealShader);
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
                var srcBody = FindByName(bodyR, "cf_m_body") ?? (bodyR.sharedMaterials.Length > 0 ? bodyR.sharedMaterials[0] : null);
                if (srcBody == null) { if (debug) LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: no body material on '" + cc.name + "'."); return false; }
                var copy = FindCopy(bodyR, BaseName(srcBody)) ?? CreateCopyTracked(me, bodyR, srcBody, go, "body reveal");
                if (copy == null) return false;

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

        /// Convert a SELECTED male's penis material (cm_m_dankon) to CloXray/OrgInside with
        /// OutsideOfBodyAlpha=1 so the penis x-rays through the body (the "apply OrgInside to a penis" case).
        internal static bool EnsureBallsOrgInside(Component cc, int stencil, bool debug)
        {
            Init();
            if (cc == null || _ctrlType == null || _mSetShader == null || _mSetFloat == null) return false;
            try
            {
                var me = cc.GetComponent(_ctrlType);
                if (me == null) return false;
                SkinnedMeshRenderer ballsR = null; Material src = null;
                foreach (var r in cc.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (r == null || r.sharedMaterials == null) continue;
                    foreach (var m in r.sharedMaterials)
                        if (m != null && BaseName(m) == BallsMat) { ballsR = r; src = m; break; }
                    if (src != null) break;
                }
                if (src == null) { if (debug) LiquidWobbleMPBPlugin._logger?.LogInfo("MEBridge: no '" + BallsMat + "' on '" + cc.name + "' — balls x-ray skipped."); return false; }

                GameObject go = GetBodyGo(cc) ?? ballsR.gameObject;
                Material copy = null;
                foreach (var m in ballsR.sharedMaterials)
                    if (m != null && m.shader != null && m.shader.name == OrgInsideShader && m.name.Contains(".MECopy")) { copy = m; break; }
                if (copy == null) copy = CreateCopyTracked(me, ballsR, src, go, "balls x-ray");
                if (copy == null) return false;

                _mSetShader.Invoke(me, new object[] { 0, _otCharacter, copy, OrgInsideShader, go, true });
                _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, copy, "StencilBody",        (float)stencil,       go, true });
                _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, copy, "StencilBody_Plus_1", (float)(stencil + 1), go, true });
                _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, copy, "OutsideOfBodyAlpha", 0f,                   go, true });
                _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, copy, "OutsideShieldDepth", 1f,                   go, true });
                LiquidWobbleMPBPlugin._logger?.LogInfo("MEBridge: balls x-ray on '" + cc.name + "' — '" + copy.name + "' -> CloXray/OrgInside (stencil " + stencil + "/" + (stencil + 1) + ").");
                return true;
            }
            catch (System.Exception e)
            {
                LiquidWobbleMPBPlugin._logger?.LogError("MEBridge: balls x-ray failed on '" + (cc ? cc.name : "?") + "': " + e.Message);
                return false;
            }
        }

        public static bool EnsurePenisOrgInside(Component cc, int stencil, bool debug)
        {
            return EnsurePenisOrgInside(cc, stencil, debug, 1f, -1f);   // Studio defaults: outside visible, BottomWindow untouched (ME owns it there).
        }

        public static bool EnsurePenisOrgInside(Component cc, int stencil, bool debug, float outsideAlpha)
        {
            return EnsurePenisOrgInside(cc, stencil, debug, outsideAlpha, -1f);
        }

        public static bool EnsurePenisOrgInside(Component cc, int stencil, bool debug, float outsideAlpha, float bottomWindow)
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

                // ORIGINAL material.
                if (!already)
                {
                    if (_mCopyRemove == null)
                    {
                        LiquidWobbleMPBPlugin._logger?.LogError("MEBridge: penis x-ray NOT applied on '" + cc.name + "' — ME MaterialCopyRemove unavailable (ME version?). Fix the ME bridge; the penis material is never converted in place.");
                        return false;
                    }
                    Material copy = null;
                    foreach (var m in penisR.sharedMaterials)
                        if (m != null && m.shader != null && m.shader.name == OrgInsideShader && m.name.Contains(".MECopy")) { copy = m; break; }
                    if (copy == null)
                    {
                        var before = new List<Material>(penisR.sharedMaterials);
                        _mCopyRemove.Invoke(me, new object[] { 0, _otCharacter, dankon, go });
                        foreach (var m in penisR.sharedMaterials)
                            if (m != null && !before.Contains(m)) { copy = m; break; }
                        if (copy == null)
                        {
                            LiquidWobbleMPBPlugin._logger?.LogError("MEBridge: penis x-ray NOT applied on '" + cc.name + "' — ME MaterialCopyRemove created no copy slot. Fix the copy path; the penis material is never converted in place.");
                            return false;
                        }
                        _mSetShader.Invoke(me, new object[] { 0, _otCharacter, copy, OrgInsideShader, go, true });
                    }
                    _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, copy, "StencilBody",        (float)stencil,       go, true });
                    _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, copy, "StencilBody_Plus_1", (float)(stencil + 1), go, true });
                    _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, copy, "OutsideOfBodyAlpha", 0f,                   go, true });   // inside-only: the ORIGINAL owns the outside look.
                    // Shield (shader v389): the carve writes NEAR depth over the outside-body penis
                    // silhouette so copy2's re-draw is depth-rejected there.
                    _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, copy, "OutsideShieldDepth", 1f,                   go, true });
                    if (bottomWindow >= 0f)
                        _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, copy, "BottomWindow",   bottomWindow,         go, true });

                    const int OriginalRedrawQueue = 3502;
                    Material copy2 = null;
                    foreach (var m in penisR.sharedMaterials)
                        if (m != null && !ReferenceEquals(m, copy) && m.name.Contains(".MECopy") &&
                            (m.shader == null || !m.shader.name.StartsWith("CloXray/")) &&
                            m.renderQueue == OriginalRedrawQueue) { copy2 = m; break; }
                    if (copy2 == null)
                    {
                        var before2 = new List<Material>(penisR.sharedMaterials);
                        _mCopyRemove.Invoke(me, new object[] { 0, _otCharacter, dankon, go });
                        foreach (var m in penisR.sharedMaterials)
                            if (m != null && !before2.Contains(m)) { copy2 = m; break; }
                        if (copy2 == null)
                        {
                            LiquidWobbleMPBPlugin._logger?.LogError("MEBridge: penis ORIGINAL-LOOK copy not created on '" + cc.name + "' (second MaterialCopyRemove had no effect) — fix the copy path; the in-window look stays OrgInside until then.");
                            return false;
                        }
                        SetQueuePersisted(me, copy2, go, OriginalRedrawQueue);
                    }
                    LiquidWobbleMPBPlugin._logger?.LogInfo("MEBridge: penis x-ray COPIES on '" + cc.name + "': carve '" + copy.name + "' -> " + OrgInsideShader + " (stencil " + stencil + "/" + (stencil + 1) + ") + original-look '" + copy2.name + "' @" + OriginalRedrawQueue + " ('" + dankon.name + "' untouched; in-window look = the original shader).");
                    return true;
                }
                // Stencil pair must match the FEMALE body the penis is seen through.
                _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, dankon, "StencilBody",        (float)stencil,       go, true });
                _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, dankon, "StencilBody_Plus_1", (float)(stencil + 1), go, true });
                _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, dankon, "OutsideOfBodyAlpha", outsideAlpha,        go, true });
                if (bottomWindow >= 0f)   // <0 = leave as-is (Studio: ME owns the slider).
                    _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, dankon, "BottomWindow",    bottomWindow,       go, true });
                LiquidWobbleMPBPlugin._logger?.LogInfo("MEBridge: penis x-ray on '" + cc.name + "': '" + dankon.name + "' -> " + OrgInsideShader + " (stencil " + stencil + "/" + (stencil + 1) + ", OutsideOfBodyAlpha=" + outsideAlpha.ToString("F2") + ")" + (already ? " [shader already set]" : "") + ".");
                return true;
            }
            catch (Exception e)
            {
                LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: EnsurePenisOrgInside failed on '" + (cc ? cc.name : "?") + "': " + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        public static void DumpXrayChain(Component male, Component female, Component womb)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                Action<string, Renderer> dump = (tag, r) =>
                {
                    if (r == null || r.sharedMaterials == null) { sb.Append("\n  ").Append(tag).Append(": renderer NOT FOUND"); return; }
                    sb.Append("\n  ").Append(tag).Append(" '").Append(r.name).Append("':");
                    foreach (var m in r.sharedMaterials)
                    {
                        if (m == null) { sb.Append("\n    <null material>"); continue; }
                        sb.Append("\n    '").Append(m.name).Append("' q=").Append(m.renderQueue)
                          .Append(" sh=").Append(m.shader != null ? m.shader.name : "NULL");
                        foreach (var p in new[] { "_StencilBody", "_StencilBody_Plus_1", "_OutsideOfBodyAlpha", "_OutsideShieldDepth", "_BottomWindow", "_StencilOrgan", "_OutBodyBackOcclude", "_AlphaOptionZWrite" })
                            if (m.HasProperty(p)) sb.Append(" ").Append(p.Substring(1)).Append("=").Append(m.GetFloat(p).ToString("F0"));
                    }
                };
                Renderer penisR = null;
                if (male != null)
                    foreach (var r in male.GetComponentsInChildren<Renderer>(true))
                    {
                        if (r == null || r.sharedMaterials == null) continue;
                        foreach (var m in r.sharedMaterials) if (m != null && IsPenisMat(m.name)) { penisR = r; break; }
                        if (penisR != null) break;
                    }
                dump("MALE penis", penisR);
                // every renderer+material on the male, one line each: the balls material is not named "tama"
                // on this rig, so the white-balls report needs the real names before it can be diagnosed.
                if (male != null)
                    foreach (var r in male.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    {
                        if (r == null || r.sharedMaterials == null || r.sharedMaterials.Length == 0) continue;
                        sb.AppendLine().Append("  MALE rend '").Append(r.name).Append("' enabled=").Append(r.enabled);
                        foreach (var m in r.sharedMaterials)
                        {
                            if (m == null) { sb.AppendLine().Append("      <null>"); continue; }
                            sb.AppendLine().Append("      '").Append(m.name).Append("' sh=").Append(m.shader != null ? m.shader.name : "NULL")
                              .Append(" tex=").Append(m.HasProperty("_MainTex") ? (m.GetTexture("_MainTex") != null ? "yes" : "NULL") : "-")
                              .Append(" col=").Append(m.HasProperty("_Color") ? m.GetColor("_Color").ToString("F2") : "-");
                        }
                    }
                Renderer bodyR = null;
                if (female != null)
                    foreach (var r in female.GetComponentsInChildren<Renderer>(true))
                    {
                        if (r == null || r.sharedMaterials == null) continue;
                        foreach (var m in r.sharedMaterials) if (m != null && m.name.StartsWith("cf_m_body")) { bodyR = r; break; }
                        if (bodyR != null) break;
                    }
                dump("FEMALE body", bodyR);
                Renderer wombR = null;
                if (womb != null) wombR = womb.GetComponentInChildren<SkinnedMeshRenderer>(true);
                dump("WOMB", wombR);
                LiquidWobbleMPBPlugin._logger?.LogInfo("MEBridge: XRAY-CHAIN dump:" + sb);
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: XRAY-CHAIN dump failed: " + e.Message); }
        }

        // Match cm_m_dankon, tolerating runtime " (Instance)" suffix(es) (like the clothes path does).
        private static bool IsPenisMat(string n)
        {
            if (string.IsNullOrEmpty(n)) return false;
            int sp = n.IndexOf(" (");
            if (sp > 0) n = n.Substring(0, sp);
            return n == PenisMat;
        }

        // Does this character carry a real penis material (cm_m_dankon)?
        public static bool HasVisiblePenisMesh(Component cc)
        {
            if (cc == null) return false;
            foreach (var r in cc.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || r.sharedMaterials == null) continue;
                if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
                foreach (var m in r.sharedMaterials)
                    if (m != null && IsPenisMat(m.name)) return true;
            }
            return false;
        }

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

        /// Idempotently ensure the body ALSO has the BodyRevealExtra "skin veil" copy.
        public static bool EnsureBodyVeil(Component cc, int stencilPlus1, bool debug, bool overwriteExisting)
        {
            Init();
            if (cc == null || _ctrlType == null) return false;
            if (MainGameWomb.IsMaleChara(cc)) return false;   // wearer-only, same as the reveal stamp.

            try
            {
                var me = cc.GetComponent(_ctrlType);
                var bodyR = me != null ? FindBodyRenderer(cc) : null;
                if (bodyR == null) return false;

                GameObject go = GetBodyGo(cc) ?? bodyR.gameObject;

                // Already applied? -> just keep the pair stencil correct.
                var existing = FindConfiguredCopy(bodyR, BodyVeilShader);
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

                // Fresh copy. MaterialCopyRemove on the SOURCE adds a copy; verify one actually appeared
                // (toggle semantics differ across ME versions.
                var srcBody = FindByName(bodyR, "cf_m_body") ?? (bodyR.sharedMaterials.Length > 0 ? bodyR.sharedMaterials[0] : null);
                if (srcBody == null) return false;
                var copy = FindCopy(bodyR, BaseName(srcBody)) ?? CreateCopyTracked(me, bodyR, srcBody, go, "skin veil");
                if (copy == null) return false;

                _mSetShader.Invoke(me, new object[] { 0, _otCharacter, copy, BodyVeilShader, go, true });
                _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, copy, "StencilBody_Plus_1", (float)stencilPlus1, go, true });
                // Initial x-ray strength - set only on creation.
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

        // ChaControl.objBody - field or property depending on the game build.
        private static GameObject GetBodyGo(Component cc)
        {
            const BindingFlags ANY = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var f = cc.GetType().GetField("objBody", ANY);
            if (f != null) { var g = f.GetValue(cc) as GameObject; if (g != null) return g; }
            var p = cc.GetType().GetProperty("objBody", ANY);
            if (p != null) { try { return p.GetValue(cc, null) as GameObject; } catch { } }
            return null;
        }

        private static int CountCopies(SkinnedMeshRenderer r, string srcName)
        {
            int n = 0;
            foreach (var m in r.sharedMaterials)
                if (m != null && BaseName(m).StartsWith(srcName + ".MECopy")) n++;
            return n;
        }

        private static readonly HashSet<string> HeadBranchBones = new HashSet<string>
        { "cf_j_neck", "cf_j_head", "cf_s_head", "p_cf_head_bone", "ct_head", "cf_J_FaceRoot", "cf_J_FaceBase" };
        private static bool IsHeadBranchAccessory(Transform t)
        {
            for (var p = t; p != null; p = p.parent)
                if (HeadBranchBones.Contains(p.name)) return true;
            return false;
        }

        private static readonly string[] HeadKeyMarkers = { "head", "hair", "kami", "megane", "earring", "nose", "mouth", "face", "mimi" };
        private static bool IsHeadRegionParent(string parentKey)
        {
            if (string.IsNullOrEmpty(parentKey)) return false;
            string k = parentKey.ToLowerInvariant();
            foreach (var mk in HeadKeyMarkers) if (k.Contains(mk)) return true;
            return false;
        }

        // Read ChaControl.nowCoordinate.accessory.parts[slot].parentKey (the game's authoritative
        // per-accessory attach anchor), resolved lazily off the live objects.
        private static bool _accReflWarned;
        private static System.Reflection.PropertyInfo _pNowCoord; private static System.Reflection.FieldInfo _fNowCoord, _fAccessory, _fParts, _fParentKey;
        private static string AccessoryParentKey(Component cc, int slot)
        {
            try
            {
                const BindingFlags ANY = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                object nc = null;
                if (_pNowCoord == null && _fNowCoord == null)
                {
                    _pNowCoord = cc.GetType().GetProperty("nowCoordinate", ANY);
                    if (_pNowCoord == null) _fNowCoord = cc.GetType().GetField("nowCoordinate", ANY);
                }
                if (_pNowCoord != null) nc = _pNowCoord.GetValue(cc, null);
                else if (_fNowCoord != null) nc = _fNowCoord.GetValue(cc);
                if (nc == null) return null;

                if (_fAccessory == null) _fAccessory = nc.GetType().GetField("accessory", ANY);
                var acc = _fAccessory != null ? _fAccessory.GetValue(nc) : null;
                if (acc == null) return null;

                if (_fParts == null) _fParts = acc.GetType().GetField("parts", ANY);
                var parts = _fParts != null ? _fParts.GetValue(acc) as Array : null;
                if (parts == null || slot < 0 || slot >= parts.Length) return null;

                var part = parts.GetValue(slot);
                if (part == null) return null;
                if (_fParentKey == null) _fParentKey = part.GetType().GetField("parentKey", ANY);
                return _fParentKey != null ? _fParentKey.GetValue(part) as string : null;
            }
            catch (Exception e)
            {
                if (!_accReflWarned) { _accReflWarned = true; LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: could not read accessory parentKey (" + e.Message + ") — head/hair skip falls to the attach-bone check only."); }
                return null;
            }
        }

        // KK clothes kinds stamped by EnsureClothesReveal: top, bot, bra, shorts, panst.
        private static readonly int[] ClothesKinds = { 0, 1, 2, 3, 5 };

        /// Idempotently stamp every WORN (active) torso garment with a BodyReveal copy at the given stencil.
        public static bool EnsureClothesReveal(Component cc, int stencil, bool debug)
        {
            Init();
            if (cc == null || _ctrlType == null) return false;
            if (MainGameWomb.IsMaleChara(cc)) return false;   // wearer-only, same as the reveal stamp.
            if (_otClothing == null)
            {
                LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: ME ObjectType has no Clothing member — clothes stamping unavailable.");
                return false;
            }

            try
            {
                var me = cc.GetComponent(_ctrlType);
                if (me == null) return false;

                // objClothes is a FIELD in some game builds and a PROPERTY in others (this install.
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
                        continue;
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

                        // Snapshot the SOURCE materials first (ME appends copies while it work).
                        var sources = new System.Collections.Generic.List<Material>();
                        foreach (var m in r.sharedMaterials)
                        {
                            if (m == null) continue;
                            if (m.name.Contains(".MECopy")) continue;   // a copy, not a source.
                            if (m.shader != null && m.shader.name.StartsWith("CloXray/")) continue;   // already its own.
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
                            // ME names copies from the SANITIZED base name: runtime clothes materials are
                            // instanced ("cf_m_bot_skirt01 (Instance) (Instance)") but the copy is "cf_m_bot_skirt01.MECopy1".
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

                            // Adopt a leftover unconfigured copy first (e.g. from a run where the name
                            // matching failed) before asking ME for a new one.
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

        // Same stamp for worn ACCESSORIES (jewelry, acc-clothing, skirts built from accessories…).
        public static bool EnsureAccessoryReveal(Component cc, int stencil, bool debug)
        {
            Init();
            if (cc == null || _ctrlType == null) return false;
            if (MainGameWomb.IsMaleChara(cc)) return false;   // wearer-only, same as the reveal stamp.
            if (_otAccessory == null)
            {
                LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: ME ObjectType has no Accessory member — accessory stamping unavailable.");
                return false;
            }

            try
            {
                var me = cc.GetComponent(_ctrlType);
                if (me == null) return false;

                GameObject[] slots = null;
                const BindingFlags ANY = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var fA = cc.GetType().GetField("objAccessory", ANY);
                if (fA != null) slots = fA.GetValue(cc) as GameObject[];
                if (slots == null)
                {
                    var pA = cc.GetType().GetProperty("objAccessory", ANY);
                    if (pA != null) slots = pA.GetValue(cc, null) as GameObject[];
                }
                if (slots == null)
                {
                    LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: ChaControl.objAccessory not found on '" + cc.name + "' — accessory stamping unavailable.");
                    return false;
                }

                int stamped = 0, updated = 0;
                for (int slot = 0; slot < slots.Length; slot++)
                {
                    var go = slots[slot];
                    if (go == null || !go.activeInHierarchy) continue;

                    // coordinate ('parentKey').
                    bool headSlot = IsHeadRegionParent(AccessoryParentKey(cc, slot));

                    foreach (var r in go.GetComponentsInChildren<Renderer>(false))
                    {
                        if (r == null || r.sharedMaterials == null) continue;
                        if (headSlot || IsHeadBranchAccessory(r.transform))
                        {
                            foreach (var m in r.sharedMaterials)
                                if (m != null && m.name.Contains(".MECopy") && m.shader != null && m.shader.name == BodyRevealShader)
                                {
                                    _mCopyRemove.Invoke(me, new object[] { slot, _otAccessory, m, go });
                                    LiquidWobbleMPBPlugin._logger?.LogInfo("MEBridge: removed a stray x-ray copy from head/hair accessory '" + r.name + "' (slot " + slot + ") — restored the original hair look.");
                                    break;
                                }
                            if (debug) LiquidWobbleMPBPlugin._logger?.LogInfo("MEBridge: accessory '" + r.name + "' (slot " + slot + ") is head/hair-attached -> NOT stamped (can't occlude the womb window).");
                            continue;
                        }

                        var sources = new System.Collections.Generic.List<Material>();
                        foreach (var m in r.sharedMaterials)
                        {
                            if (m == null) continue;
                            if (m.name.Contains(".MECopy")) continue;   // a copy, not a source.
                            if (m.shader != null && m.shader.name.StartsWith("CloXray/")) continue;   // already its own.
                            sources.Add(m);
                        }

                        foreach (var src in sources)
                        {
                            string baseName = src.name;
                            while (baseName.EndsWith(" (Instance)"))
                                baseName = baseName.Substring(0, baseName.Length - " (Instance)".Length);

                            Material existing = null;
                            foreach (var m in r.sharedMaterials)
                                if (m != null && m.name.StartsWith(baseName + ".MECopy") &&
                                    m.shader != null && m.shader.name == BodyRevealShader) { existing = m; break; }
                            if (existing != null)
                            {
                                if (existing.HasProperty("_StencilRef") && Mathf.RoundToInt(existing.GetFloat("_StencilRef")) != stencil)
                                {
                                    _mSetFloat.Invoke(me, new object[] { slot, _otAccessory, existing, "StencilRef", (float)stencil, go, true });
                                    updated++;
                                }
                                continue;
                            }

                            Material copy = null;
                            foreach (var m in r.sharedMaterials)
                                if (m != null && m.name.StartsWith(baseName + ".MECopy") &&
                                    (m.shader == null || !m.shader.name.StartsWith("CloXray/"))) { copy = m; break; }
                            if (copy == null)
                            {
                                _mCopyRemove.Invoke(me, new object[] { slot, _otAccessory, src, go });
                                foreach (var m in r.sharedMaterials)
                                    if (m != null && m.name.StartsWith(baseName + ".MECopy") &&
                                        (m.shader == null || !m.shader.name.StartsWith("CloXray/"))) { copy = m; break; }
                            }
                            if (copy == null)
                            {
                                LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: accessory copy not created for '" + src.name + "' on '" + r.name + "' (slot " + slot + ").");
                                continue;
                            }
                            _mSetShader.Invoke(me, new object[] { slot, _otAccessory, copy, BodyRevealShader, go, true });
                            _mSetFloat.Invoke(me, new object[] { slot, _otAccessory, copy, "StencilRef", (float)stencil, go, true });
                            stamped++;
                        }
                    }
                }

                if (stamped > 0 || updated > 0 || debug)
                    LiquidWobbleMPBPlugin._logger?.LogInfo("MEBridge: accessory x-ray on '" + cc.name + "': " + stamped + " material(s) stamped, " + updated + " restenciled (stencil " + stencil + ").");
                return true;
            }
            catch (Exception e)
            {
                LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: EnsureAccessoryReveal failed on '" + (cc ? cc.name : "?") + "': " + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        // The veil must sit at queue 3502 (after the organ, 3500) or it silently does nothing.
        private static void EnsureVeilQueue(object me, Material veil, GameObject go)
        {
            if (veil == null || veil.renderQueue == BodyVeilQueue) return;
            LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: veil copy queue is " + veil.renderQueue + " (want " + BodyVeilQueue + ") — correcting.");
            SetQueuePersisted(me, veil, go, BodyVeilQueue);
        }

        // Push a render queue through ME's queue API (persisted with the scene/card) AND set it directly on
        // the material (immediate).
        private static void SetQueuePersisted(object me, Material m, GameObject go, int queue)
        {
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
                        if (pt == typeof(int))            { args[i] = slotFilled ? (object)queue : (object)0; slotFilled = true; }
                        else if (pt == _objType)          args[i] = _otCharacter;
                        else if (pt == typeof(Material))  args[i] = m;
                        else if (pt == typeof(GameObject))args[i] = go;
                        else if (pt == typeof(bool))      args[i] = true;
                        else args[i] = null;
                    }
                    _mSetQueue.Invoke(me, args);
                }
                catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: ME queue set failed (" + e.Message + ") — applying direct (non-persisted) queue."); }
            }
            m.renderQueue = queue;
        }
    }

    // Drives the NodesConstraints plugin (Joan6694, GUID com.joan6694.illusionplugins.nodesconstraints) by
    // reflection.
    internal static class NodeConstraintBridge
    {
        private const string NcGuid = "com.joan6694.illusionplugins.nodesconstraints";
        private const BindingFlags BF = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static bool _tried;
        private static Type _ncType;
        private static MethodInfo _addConstraint;   // the 10-arg convenience overload.
        private static FieldInfo _fConstraints;   // NodesConstraints._constraints (List<Constraint>).
        private static FieldInfo _fChildTransform;   // Constraint.childTransform.
        private static FieldInfo _fParentTransform;   // Constraint.parentTransform.
        private static Harmony _ncHarmony;   // patches the NC pairing-change methods (add / enable-disable / delete).
        private static bool _pairingHooksTried;   // install-once guard for the pairing hooks.

        // DIAG: dump every existing constraint (parent/child name + world position) so it can read what.
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

        // Live constraint count, or -1 if the list can't be read.
        public static int ConstraintCount
        {
            get
            {
                Init();
                var inst = Instance();
                if (inst == null) return -1;
                try
                {
                    if (_fConstraints == null) _fConstraints = _ncType.GetField("_constraints", BF);
                    var list = (_fConstraints != null ? _fConstraints.GetValue(inst) : null) as System.Collections.IList;
                    return list != null ? list.Count : -1;
                }
                catch { return -1; }
            }
        }

        // Drops constraints whose parent or child transform has been destroyed.
        public static int RemoveDeadConstraints()
        {
            Init();
            var inst = Instance();
            if (inst == null) return 0;
            try
            {
                if (_fConstraints == null) _fConstraints = _ncType.GetField("_constraints", BF);
                var list = (_fConstraints != null ? _fConstraints.GetValue(inst) : null) as System.Collections.IList;
                if (list == null) return 0;
                int removed = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    var c = list[i];
                    if (c == null) { list.RemoveAt(i); i--; removed++; continue; }
                    if (_fChildTransform == null)  _fChildTransform  = c.GetType().GetField("childTransform", BF);
                    if (_fParentTransform == null) _fParentTransform = c.GetType().GetField("parentTransform", BF);
                    if (_fChildTransform == null || _fParentTransform == null) return removed;
                    var ch = _fChildTransform.GetValue(c) as Transform;
                    var pa = _fParentTransform.GetValue(c) as Transform;
                    if (ch == null || pa == null)
                    {
                        list.RemoveAt(i); i--; removed++;
                        LiquidWobbleMPBPlugin._logger?.LogInfo("NodeConstraintBridge: dropped a constraint with a destroyed endpoint.");
                    }
                }
                return removed;
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("NodeConstraintBridge: dead-constraint sweep failed: " + e.Message); return 0; }
        }

        // Drops constraints that repeat an earlier (parent, child) pair.
        public static Transform ParentOfChild(Transform child)
        {
            if (child == null) return null;
            foreach (var pair in LivePairs())
                if (pair.Value == child) return pair.Key;
            return null;
        }

        public static System.Collections.Generic.List<KeyValuePair<Transform, Transform>> LivePairs()
        {
            var pairs = new System.Collections.Generic.List<KeyValuePair<Transform, Transform>>();
            Init();
            var inst = Instance();
            if (inst == null) return pairs;
            try
            {
                if (_fConstraints == null) _fConstraints = _ncType.GetField("_constraints", BF);
                var list = (_fConstraints != null ? _fConstraints.GetValue(inst) : null) as System.Collections.IList;
                if (list == null) return pairs;
                for (int i = 0; i < list.Count; i++)
                {
                    var c = list[i];
                    if (c == null) continue;
                    if (_fChildTransform == null)  _fChildTransform  = c.GetType().GetField("childTransform", BF);
                    if (_fParentTransform == null) _fParentTransform = c.GetType().GetField("parentTransform", BF);
                    if (_fChildTransform == null || _fParentTransform == null) return pairs;
                    var ch = _fChildTransform.GetValue(c) as Transform;
                    var pa = _fParentTransform.GetValue(c) as Transform;
                    if (ch != null && pa != null) pairs.Add(new KeyValuePair<Transform, Transform>(pa, ch));
                }
            }
            catch { }
            return pairs;
        }

        public static int RemoveDuplicatePairs()
        {
            Init();
            var inst = Instance();
            if (inst == null) return 0;
            try
            {
                if (_fConstraints == null) _fConstraints = _ncType.GetField("_constraints", BF);
                var list = (_fConstraints != null ? _fConstraints.GetValue(inst) : null) as System.Collections.IList;
                if (list == null) return 0;
                var seen = new System.Collections.Generic.List<KeyValuePair<Transform, Transform>>();
                int removed = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    var c = list[i];
                    if (c == null) continue;
                    if (_fChildTransform == null)  _fChildTransform  = c.GetType().GetField("childTransform", BF);
                    if (_fParentTransform == null) _fParentTransform = c.GetType().GetField("parentTransform", BF);
                    if (_fChildTransform == null || _fParentTransform == null) return removed;
                    var ch = _fChildTransform.GetValue(c) as Transform;
                    var pa = _fParentTransform.GetValue(c) as Transform;
                    if (ch == null || pa == null) continue;   // dead endpoint: not its own to judge.
                    bool dup = false;
                    for (int s = 0; s < seen.Count; s++)
                        if (seen[s].Key == pa && seen[s].Value == ch) { dup = true; break; }
                    if (dup)
                    {
                        list.RemoveAt(i); i--; removed++;
                        LiquidWobbleMPBPlugin._logger?.LogInfo("NodeConstraintBridge: removed a duplicate '" + pa.name + " -> " + ch.name + "' link.");
                    }
                    else seen.Add(new KeyValuePair<Transform, Transform>(pa, ch));
                }
                return removed;
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("NodeConstraintBridge: duplicate sweep failed: " + e.Message); return 0; }
        }

        // Is `child` already the CHILD of any existing constraint?
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

        // Is `node` already wired into any existing constraint.
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

        // Resolve the live NodesConstraints plugin by GUID (robust vs a bare type-name scan that could
        // collide).
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

        // Position-only link: parentTransform drives childTransform.
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
                // Will it actually DRIVE? A link only moves a bone if an endpoint is a live Studio node
                // (GuideObject).
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

        // ── NC PAIRING HOOKS ────────────────────────────────────────────────────────────────────────────────── Harmony-postfix the THREE NodesConstraints methods that can change a k_f_dan_entry constraint.
        public static void InstallPairingHooks()
        {
            if (_pairingHooksTried) return;
            Init();
            if (_ncType == null || Instance() == null) return;   // NodesConstraints not loaded yet -> retry on a later call.
            _pairingHooksTried = true;
            if (_ncHarmony == null) _ncHarmony = new Harmony("Clo.LiquidWobbleMPB.ncpairing");
            var cstr = _ncType.GetNestedType("Constraint", BindingFlags.NonPublic | BindingFlags.Public);

            // enable/disable toggle: SetConstraintEnabled(Constraint, bool).
            var mToggle = (cstr != null) ? _ncType.GetMethod("SetConstraintEnabled", BF, null, new[] { cstr, typeof(bool) }, null) : null;
            PatchOrWarn(mToggle, nameof(OnSetConstraintEnabled), "SetConstraintEnabled (enable/disable)");

            // create: the AddConstraint overload that actually appends to _constraints is the one with the
            // MOST parameters (the shorter overloads delegate to it); its return value is the new Constraint.
            MethodInfo mAdd = null; int maxP = -1;
            foreach (var m in _ncType.GetMethods(BF))
                if (m.Name == "AddConstraint" && m.GetParameters().Length > maxP) { maxP = m.GetParameters().Length; mAdd = m; }
            PatchOrWarn(mAdd, nameof(OnAddConstraint), "AddConstraint (create)");

            // delete: RemoveConstraintAt(int).
            var mDel = _ncType.GetMethod("RemoveConstraintAt", BF, null, new[] { typeof(int) }, null);
            PatchOrWarn(mDel, nameof(OnRemoveConstraint), "RemoveConstraintAt (delete)");
        }

        // Each hook is resolved, patched + logged INDEPENDENTLY (its own try/catch) so one failing target
        // can't abort the others, and every outcome is logged (fail-loud, no silent give-up).
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

        // Postfixes. __0 = the Constraint arg (toggle); __result = the new Constraint (add; null if NC
        // deduped it).
        private static void OnSetConstraintEnabled(object __0)  { RepairIfDanEntry(__0); }
        private static void OnAddConstraint(object __result)    { RepairIfDanEntry(__result); }
        private static void OnRemoveConstraint()
        {
            if (WombExpandEffect.EffectiveActive) WombExpandEffect.RequestRepair();
        }

        private static void RepairIfDanEntry(object constraint)
        {
            if (!WombExpandEffect.EffectiveActive || constraint == null) return;   // mod off / no womb / deduped add -> ignore.
            try
            {
                if (_fChildTransform == null)  _fChildTransform  = constraint.GetType().GetField("childTransform", BF);
                if (_fParentTransform == null) _fParentTransform = constraint.GetType().GetField("parentTransform", BF);
                var ct = _fChildTransform  != null ? _fChildTransform.GetValue(constraint)  as Transform : null;
                var pt = _fParentTransform != null ? _fParentTransform.GetValue(constraint) as Transform : null;
                if ((ct != null && ct.name == "k_f_dan_entry") || (pt != null && pt.name == "k_f_dan_entry"))
                    WombExpandEffect.RequestRepair();   // the pairing anchor changed -> every womb rechecks next post-IK frame.
            }
            catch { }
        }
    }

    // Stops BetterPenetration stacking a DUPLICATE dan constraint on every scene load.
    internal static class UncBodyReloadWatch
    {
        private const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static Harmony _harmony;
        private static bool _installed;
        private static readonly System.Collections.Generic.HashSet<int> _pending = new System.Collections.Generic.HashSet<int>();
        private static readonly System.Collections.Generic.HashSet<int> _done = new System.Collections.Generic.HashSet<int>();

        public static void Arm(Component uncController, Component chaControl)
        {
            Install(uncController);
            if (chaControl == null) return;
            int id = chaControl.GetInstanceID();
            _pending.Add(id);
            _done.Remove(id);
        }

        public static bool Done(Component chaControl) { return chaControl != null && _done.Contains(chaControl.GetInstanceID()); }

        public static void Clear(Component chaControl)
        {
            if (chaControl == null) return;
            int id = chaControl.GetInstanceID();
            _pending.Remove(id);
            _done.Remove(id);
        }

        private static void Install(Component ctl)
        {
            if (_installed || ctl == null) return;
            _installed = true;
            try
            {
                var m = ctl.GetType().GetMethod("ReloadCharacterBody", BF);
                if (m == null) { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: UncensorSelector.ReloadCharacterBody not found - the carry-over completion event cannot be hooked."); return; }
                if (_harmony == null) _harmony = new Harmony("Clo.LiquidWobbleMPB.uncreload");
                _harmony.Patch(m, postfix: new HarmonyMethod(typeof(UncBodyReloadWatch).GetMethod(nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic)));
                LiquidWobbleMPBPlugin._logger?.LogInfo("UncBodyReloadWatch: hooked UncensorSelector.ReloadCharacterBody (body-swap completion event).");
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: UncensorSelector reload hook failed: " + e.Message); }
        }

        private static void Postfix(object __instance)
        {
            try
            {
                var pcha = __instance.GetType().GetProperty("ChaControl", BF);
                var cc = pcha != null ? pcha.GetValue(__instance, null) as Component : null;
                if (cc == null) return;
                int id = cc.GetInstanceID();
                if (_pending.Remove(id)) _done.Add(id);
            }
            catch { }
        }
    }

    internal static class BPDanReaddGuard
    {
        private static bool _stoodDown;
        private const string BpTypeName = "Core_BetterPenetration.BetterPenetrationController";
        private static bool _applied;
        private static Harmony _harmony;

        // Idempotent + LAZY. BP's Core_BetterPenetration assembly is loaded only when the first BP character
        // appears (after every plugin's Awake), so an Awake-time attempt no-ops; it therefore ALSO call this on each CharacterReloaded and actually patch once the type resolves.
        public static void TryApply()
        {
            if (BPBridge.BpHasOwnDanDupGuard)
            {
                if (!_stoodDown) { _stoodDown = true; LiquidWobbleMPBPlugin._logger?.LogInfo("BPDanReaddGuard: not installed - this BetterPenetration refuses the duplicate itself (per bone, which also lets it re-bind the female collision agent)."); }
                return;
            }
            if (_applied) return;
            try
            {
                var bpType = AccessTools.TypeByName(BpTypeName);
                if (bpType == null) return;   // BP not loaded yet - retry on the next CharacterReloaded.
                var m = AccessTools.Method(bpType, "AddDanConstraints");
                if (m == null)
                {
                    _applied = true;   // renamed/removed in this BP version - stop retrying, warn once.
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
                _applied = true;   // don't spin on a hard error - warn once, leave BP.
                LiquidWobbleMPBPlugin._logger?.LogWarning("BPDanReaddGuard: failed to install guard (" + e.Message + ") — BP will behave as before (may re-add a duplicate on load).");
            }
        }

        // Return false to SKIP BP's AddDanConstraints. Only for the by-name re-add path (both parents null)
        // when the male's k_f_dan_end is already constrained.
        private static bool Prefix(object __instance, Transform danEntryParent, Transform danEndParent)
        {
            if (!WombExpandEffect.EffectiveActive) return true;   // mod off or no CloXray womb -> never touch BP's dan re-add.
            // Auto-Target re-aim (and any explicit-target call) passes real parents.
            if (danEntryParent != null || danEndParent != null) return true;
            try
            {
                Transform danEnd = ResolveDanEnd(__instance);
                if (danEnd != null && NodeConstraintBridge.HasConstraintForNode(danEnd))
                {
                    LiquidWobbleMPBPlugin._logger?.LogInfo("BPDanReaddGuard: '" + danEnd.name + "' already constrained in NodesConstraints — skipping BP's by-name dan re-add (would duplicate).");
                    return false;   // skip BP's re-add.
                }
            }
            catch (Exception e)
            {
                LiquidWobbleMPBPlugin._logger?.LogWarning("BPDanReaddGuard: guard check failed (" + e.Message + ") — letting BP run (may duplicate).");
            }
            return true;   // run BP's AddDanConstraints unchanged.
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

        // A male's k_f_dan_end (real penis tip), found under its ChaControl.
        public static Transform FindDanEnd(Component chaControl)
        {
            if (chaControl == null) return null;
            foreach (var t in chaControl.GetComponentsInChildren<Transform>(true))
                if (t != null && t.name == "k_f_dan_end") return t;
            return null;
        }
    }

    // Reads Studio's GuideObjectManager (Singleton) to tell whether a Transform is a live guide object.
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
            for (var t = _gomType; t != null && _instProp == null && _instField == null; t = t.BaseType)   // Singleton<GuideObjectManager> owns the static Instance.
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
        private const string VaginaBone = "cf_J_Vagina_root";   // BP/uncensor-provided female vagina bone (primary match).
        private const string FallbackBone = "cf_j_kokan";   // vanilla female crotch bone (lowercase j is intentional.
        private const float  PenisWombRange = 0.5f;   // a penis is attached to a womb only if its tip (k_f_dan_end) is within this of penis_target.
        private const int    HotkeyBuild = 15;   // bump per plugin build so the log identifies the loaded DLL.

        public static bool Enabled { get; set; } = true;
        public static bool Debug { get; set; } = false;
        // World-distance within which a womb's entrance counts as "inside this character's vagina".
        public static float MaxRange = 0.15f;

        // Subscribe once to KKAPI CharacterApi.CharacterReloaded on every loaded copy.
        public static void Init()
        {
            if (_subscribed) return;
            _subscribed = true;
            KKAPI.Chara.CharacterApi.CharacterReloaded += OnCharacterReloaded;
            InstallSceneLoadWatch();
            LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: subscribed to CharacterApi.CharacterReloaded.");
        }

        // EventHandler<CharacterReloadedEventArgs> via relaxed binding (param typed as the base EventArgs).
        private static bool _bpWaOffLogged;
        internal static void InstallWombHooks()
        {
            if (LiquidWobbleMPBPlugin.BPWorkaroundsEnabled)
            {
                BPDanReaddGuard.TryApply();
                PenisFKEnforcer.TryApply();
            }
            else if (!_bpWaOffLogged)
            {
                _bpWaOffLogged = true;
                LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: BP WORK-AROUNDS DISABLED (test switch) — the dan-duplicate guard and the penis-FK enforcer are NOT installed, so BetterPenetration's own behaviour is exposed. Set LiquidWobbleMPBPlugin.BPWorkaroundsEnabled = true to restore them.");
            }
            NodeConstraintBridge.InstallPairingHooks();   // instant womb<->penis re-pair on any k_f_dan_entry NodesConstraint add/enable/disable/delete.
        }

        // A scene LOAD and a character REPLACEMENT both arrive as CharacterReloaded, but they need opposite
        // handling.
        private static bool _sceneLoading;
        private static float _sceneLoadOpenedAt;
        private static bool _sceneWatchOk, _sceneWatchTried;
        private const float SceneLoadWatchdog = 30f;   // a load that never completes must not disable re-linking for the session.
        private static void InstallSceneLoadWatch()
        {
            if (_sceneWatchTried) return;
            _sceneWatchTried = true;
            try
            {
                KKAPI.Studio.SaveLoad.StudioSaveLoadApi.SceneLoad += OnSceneLoadFinished;
                _sceneWatchOk = true;
                LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: scene-load bracket armed (the constraint re-link is suppressed for the duration of a load).");
            }
            catch (Exception e)
            { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: could not subscribe to StudioSaveLoadApi.SceneLoad (" + e.Message + ") - a scene load cannot be told from a character replacement, so constraints are NOT re-linked automatically. Use the apply hotkey after a character change."); }
        }

        // Called from WobbleSceneController.OnSceneLoad.
        internal static void MarkSceneLoadStarted()
        {
            _sceneLoading = true;
            _sceneLoadOpenedAt = Time.realtimeSinceStartup;
        }
        private static void OnSceneLoadFinished(object sender, EventArgs e) { _sceneLoading = false; }

        internal static bool SceneLoading
        {
            get
            {
                if (!_sceneLoading) return false;
                if (Time.realtimeSinceStartup - _sceneLoadOpenedAt > SceneLoadWatchdog)
                {
                    _sceneLoading = false;
                    LiquidWobbleMPBPlugin._logger?.LogError("CloXray: no scene-load-finished event arrived within " + SceneLoadWatchdog.ToString("F0") + "s of the load starting - treating the load as over. If the penis links end up wrong, press the apply hotkey.");
                    return false;
                }
                return true;
            }
        }

        // WHICH CHARACTER WEARS THIS WOMB. Studio states the answer outright when the item is parented.
        internal static Component ResolveWearer(WombExpandEffect w, out string how)
        {
            how = "none";
            if (w == null) return null;
            var byParent = w.transform.GetComponentInParent<ChaControl>();
            if (byParent != null) { how = "parented under her"; return byParent; }
            var byTree = WearerFromStudioTree(w);
            if (byTree != null) { how = "workspace tree"; return byTree; }
            var byNear = FindNearestCharacter(w.EntranceWorld(), MaxRange);
            if (byNear != null) { how = "nearest " + VaginaBone + "/" + FallbackBone; return byNear; }
            return null;
        }

        private static Component WearerFromStudioTree(WombExpandEffect w)
        {
            if (!MainGameWomb.IsStudio) return null;
            try
            {
                var studio = Studio.Studio.Instance;
                if (studio == null || studio.dicObjectCtrl == null || studio.dicInfo == null) return null;
                Studio.ObjectCtrlInfo self = null;
                foreach (var kv in studio.dicObjectCtrl)
                {
                    var oci = kv.Value;
                    if (oci == null || oci.guideObject == null || oci.guideObject.transformTarget == null) continue;
                    if (w.transform.IsChildOf(oci.guideObject.transformTarget)) { self = oci; break; }
                }
                if (self == null || self.treeNodeObject == null) return null;
                for (var node = self.treeNodeObject.parent; node != null; node = node.parent)
                {
                    Studio.ObjectCtrlInfo up;
                    if (!studio.dicInfo.TryGetValue(node, out up) || up == null) continue;
                    var och = up as Studio.OCIChar;
                    if (och != null && och.charInfo != null) return och.charInfo;
                }
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("AutoBodyReveal: workspace-tree wearer lookup failed on '" + w.name + "': " + e.Message); }
            return null;
        }

        // The womb this character's penis is parked in, or null.
        private static WombExpandEffect WombPenetratedBy(Component cc)
        {
            if (cc == null || !MainGameWomb.IsMaleChara(cc)) return null;
            WombExpandEffect best = null;
            float bestD = float.MaxValue;
            foreach (var w in UnityEngine.Object.FindObjectsOfType<WombExpandEffect>())
            {
                if (w == null) continue;
                Transform target = FindChild(w.transform, "penis_target");
                if (target == null) continue;
                foreach (var bone in PenisMarkers)
                {
                    Transform t = FindChild(cc.transform, bone);
                    if (t == null) continue;
                    float d = Vector3.Distance(t.position, target.position);
                    if (d <= PenisWombRange && d < bestD) { bestD = d; best = w; }
                }
            }
            return best;
        }
        private static readonly string[] PenisMarkers = { "k_f_dan_end", "k_f_dan_entry", FallbackBone };

        // The reverse direction: the womb this character wears, decided by the same resolver, so a parented
        // item can never be claimed by a nearer unrelated womb.
        internal static WombExpandEffect WombOfWearer(Component cc, out string how)
        {
            how = "none";
            if (cc == null) return null;
            foreach (var w in UnityEngine.Object.FindObjectsOfType<WombExpandEffect>())
            {
                if (w == null) continue;
                string h;
                var owner = ResolveWearer(w, out h);
                if ((UnityEngine.Object)owner == (UnityEngine.Object)cc) { how = h; return w; }
            }
            return null;
        }

        private static void OnCharacterReloaded(object sender, KKAPI.Chara.CharaReloadEventArgs e)
        {
            // A (re)load can change the vagina-root list AND the womb<->penis pairing candidates -> refresh
            // both event-driven.
            WombExpandEffect.InvalidateVaginaRoots();
            WombExpandEffect.RequestRepair();
            // A womb spawn deferred behind the forced uncensor reload waits.
            MainGameWomb.NotifyReloadComplete();
            // The BP-interop hooks are CloXray-womb-scoped: install them only while a womb is in the scene,
            // so a no-CloXray scene is never patched.
            if (WombExpandEffect.AnyActive) InstallWombHooks();
            if (!Enabled) return;
            try
            {
                var p = e.GetType().GetProperty("ReloadedCharacter");
                var cc = p != null ? p.GetValue(e, null) as Component : null;
                // DEFER: run the re-apply after MaterialEditor has restored this body's saved BodyReveal
                // copy.
                if (cc != null) WobbleSceneController.DeferApply(cc);
            }
            catch (Exception ex) { LiquidWobbleMPBPlugin._logger?.LogWarning("AutoBodyReveal.OnCharacterReloaded: " + ex.Message); }
        }

        // On character load/change: if a CloXray womb sits IN this character's vagina (entrance within
        // MaxRange of cf_J_Vagina_root), (re)apply BodyReveal.
        public static void ApplyForCharacterNow(Component cc)
        {
            if (!LiquidWobbleMPBPlugin.CfgEnabled) return;   // master toggle OFF -> never stamp materials.
            if (cc == null) return;
            // Two anatomy probes: the BP vagina root AND the vanilla crotch bone (excluding any womb item's
            // own copy).
            Transform vagina = FindChild(cc.transform, VaginaBone);
            Transform crotch = null;
            foreach (var t in cc.GetComponentsInChildren<Transform>(true))
                if (t != null && t.name == FallbackBone && t.GetComponentInParent<WombExpandEffect>() == null) { crotch = t; break; }
            if (vagina == null && crotch == null) { if (Debug) LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: '" + cc.name + "' has neither " + VaginaBone + " nor " + FallbackBone + "; skipping."); return; }

            string how;
            WombExpandEffect best = WombOfWearer(cc, out how);
            if (best == null)
            {
                if (Debug) LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: '" + cc.name + "' wears no womb (not parented to one, none within " + MaxRange.ToString("F2") + "m) -> skip.");
                return;
            }
            int st = best.OrganStencil();
            // overwriteExisting=false: on LOAD/reload, ensure the reveal exists but never re-stamp an
            // existing copy's stencil.
            if (MEBridge.EnsureBodyReveal(cc, st, Debug, false))
                best.OnBodyRevealApplied();   // restore out-of-body interior+cum (new-spawn default hides them).
            if (LiquidWobbleMPBPlugin.CfgBodyVeil) MEBridge.EnsureBodyVeil(cc, st + 1, Debug, false);
            if (LiquidWobbleMPBPlugin.CfgClothesReveal) MEBridge.EnsureClothesReveal(cc, st, Debug);
            if (LiquidWobbleMPBPlugin.CfgClothesReveal && !MainGameWomb.IsStudio) MEBridge.EnsureAccessoryReveal(cc, st, Debug);   // Free-H: accessories dress the card - stamp them too.

            // Studio: a character load or replacement invalidates the penis links (her half of "vagina ->
            // k_f_dan_entry" dies with her old body), so they have to be re-made.
        }

        // The penis links have to be re-made after a character change whatever else happened to that
        // character.
        internal static void StudioRelinkFor(Component cc)
        {
            if (!MainGameWomb.IsStudio || cc == null || !LiquidWobbleMPBPlugin.CfgEnabled || !Enabled) return;
            string how;
            WombExpandEffect best = WombOfWearer(cc, out how);
            if (best != null)
                // Remember the body this scene runs on every time the wearer is seen with her vagina bones.
                CaptureWearer(best, cc);
            else
            {
                // A MALE reload rebuilds his body and with it k_f_dan_entry / k_f_dan_end.
                best = WombPenetratedBy(cc);
                if (best == null) return;
                if (TryRestorePenisUncensor(best, cc)) return;   // reload started; the deferred path re-links.
                how = "his penis is parked in it";
            }
            if (!_sceneWatchOk || SceneLoading)
            {
                if (Debug) LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: skipping the constraint re-link (" + (!_sceneWatchOk ? "no scene-load bracket" : "scene load in progress") + ").");
                return;
            }
            LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: '" + cc.name + "' changed under womb '" + best.name + "' (" + how + ") - re-making the penis links once NodesConstraints settles.");
            WobbleSceneController.DeferNodeRelink(best, cc);
        }

        // Records on the womb which body its wearer runs: the had-vagina flag plus her body-uncensor GUID,
        // so a later replacement can put the same body on the incoming card.
        internal static void CaptureWearer(WombExpandEffect w, Component cc)
        {
            if (w == null || cc == null || FindChild(cc.transform, VaginaBone) == null) return;
            w.WearerHadBPVagina = true;
            _bpBodyForced.Remove(cc.GetInstanceID());
            string g = MainGameWomb.GetBodyUncensorGuid(cc);
            if (!string.IsNullOrEmpty(g) && g != w.WearerBodyGuid)
            {
                w.WearerBodyGuid = g;
                if (Debug) LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: womb '" + w.name + "' wearer body captured (" + g + ").");
            }
        }

        // Records the MALE's penis/balls uncensor on the womb while he still has BP's dan bones.
        internal static void CapturePenetrator(WombExpandEffect w, Component male)
        {
            if (w == null || male == null || FindChild(male.transform, DanEntryBone) == null) return;
            _bpPenisForced.Remove(male.GetInstanceID());
            string pg = MainGameWomb.GetUncensorGuid(male, 0), bg = MainGameWomb.GetUncensorGuid(male, 1);
            if (!string.IsNullOrEmpty(pg) && pg != w.PenetratorPenisGuid)
            {
                w.PenetratorPenisGuid = pg;
                if (Debug) LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: womb '" + w.name + "' penetrator penis captured (" + pg + ").");
            }
            if (!string.IsNullOrEmpty(bg)) w.PenetratorBallsGuid = bg;
        }

        // A replacement male whose card brought no BP penis has no dan bones, so BetterPenetration has
        // nothing to drive and the womb cannot be penetrated.
        internal static bool TryRestorePenisUncensor(WombExpandEffect w, Component male)
        {
            if (w == null || male == null) return false;
            int id = male.GetInstanceID();
            if (FindChild(male.transform, DanEntryBone) != null) { _bpPenisForced.Remove(id); return false; }
            if (string.IsNullOrEmpty(w.PenetratorPenisGuid)) return false;
            if (!_bpPenisForced.Add(id))
            {
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: '" + male.name + "' still has no " + DanEntryBone + " after restoring the scene's penis uncensor - BetterPenetration cannot drive him. Pick a BP penis in Uncensor Selector.");
                return false;
            }
            LiquidWobbleMPBPlugin._logger?.LogWarning("AutoBodyReveal: '" + male.name + "' replaced a male whose penis was BP-driven; restoring that penis uncensor so he can penetrate again.");
            if (!MainGameWomb.SetPenisUncensorGuid(male, w.PenetratorPenisGuid, w.PenetratorBallsGuid)) return false;
            WobbleSceneController.DeferPenisRelink(w, male);
            return true;
        }
        private static readonly System.Collections.Generic.HashSet<int> _bpPenisForced = new System.Collections.Generic.HashSet<int>();
        internal const string DanEntryBone = "k_f_dan_entry";

        // Poll-side capture (2s cadence from WobbleSceneController).
        internal static void CaptureWearersFromConstraints()
        {
            if (!MainGameWomb.IsStudio || !LiquidWobbleMPBPlugin.CfgEnabled || !Enabled) return;
            var wombs = UnityEngine.Object.FindObjectsOfType<WombExpandEffect>();
            if (wombs.Length == 0) return;
            bool need = false;
            foreach (var w in wombs)
                if (w != null && (!w.WearerHadBPVagina || string.IsNullOrEmpty(w.WearerBodyGuid) || string.IsNullOrEmpty(w.PenetratorPenisGuid))) { need = true; break; }
            if (!need) return;
            foreach (var pair in NodeConstraintBridge.LivePairs())
            {
                var pa = pair.Key;
                if (pa == null || pa.name != VaginaBone) continue;
                Component cc = null;
                for (var c = pa; c != null; c = c.parent) { cc = c.GetComponent("ChaControl"); if (cc != null) break; }
                if (cc == null) continue;
                WombExpandEffect best = null; float bestD = float.MaxValue;
                foreach (var w in wombs)
                {
                    if (w == null) continue;
                    float d = (w.EntranceWorld() - pa.position).magnitude;
                    if (d < bestD) { bestD = d; best = w; }
                }
                if (best != null && bestD <= MaxRange)
                {
                    CaptureWearer(best, cc);
                    // The child of this row is k_f_dan_entry - i.e. the MALE.
                    var male = pair.Value != null ? FindChaControlOf(pair.Value) : null;
                    if (male != null) CapturePenetrator(best, male);
                }
            }
        }

        // Runs once NodesConstraints has settled (see WobbleSceneController.DeferNodeRelink).
        internal static void RelinkNearWomb(WombExpandEffect w)
        {
            if (w == null) return;
            string how;
            Component cc = ResolveWearer(w, out how);
            if (cc == null)
            { LiquidWobbleMPBPlugin._logger?.LogWarning("AutoBodyReveal: no character wears womb '" + w.name + "' after the swap settled (not parented to one, nothing within " + MaxRange.ToString("F2") + "m) - the penis links were not re-made."); return; }
            RelinkAfterSettle(w, cc);
        }
        internal static void RelinkAfterSettle(WombExpandEffect w, Component cc)
        {
            if (w == null || cc == null || !LiquidWobbleMPBPlugin.CfgEnabled) return;
            if (SceneLoading) { LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: a scene load started while waiting — leaving the constraints to NodesConstraints."); return; }
            Transform vagina = FindChild(cc.transform, VaginaBone);
            if (vagina != null) CaptureWearer(w, cc);
            else if (w.WearerHadBPVagina)
            {
                // The swapped-in card came in on a body with no cf_J_Vagina bones, so the penis has nothing
                // to anchor.
                if (string.IsNullOrEmpty(w.WearerBodyGuid))
                    LiquidWobbleMPBPlugin._logger?.LogError("CloXray: '" + cc.name + "' needs the BP body this scene was built on, but that body's uncensor GUID was never captured - pick it in Uncensor Selector, then press the apply hotkey.");
                else if (!HasPenetratorFor(w, cc))
                    LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: no penis parked in womb '" + w.name + "' - leaving '" + cc.name + "' body untouched.");
                else if (_bpBodyForced.Add(cc.GetInstanceID()))
                {
                    LiquidWobbleMPBPlugin._logger?.LogWarning("AutoBodyReveal: '" + cc.name + "' replaced a character whose body had " + VaginaBone + "; restoring that body uncensor so the penis stays anchored.");
                    if (MainGameWomb.SetBodyUncensorGuid(cc, w.WearerBodyGuid))
                    {
                        WobbleSceneController.DeferPostUncensorApply(w, cc);
                        return;
                    }
                }
                else LiquidWobbleMPBPlugin._logger?.LogError("CloXray: '" + cc.name + "' still has no " + VaginaBone + " after restoring the scene's body uncensor - the penis anchors to the vanilla crotch bone instead. Pick a BP-compatible body in Uncensor Selector.");
            }
            int dead = NodeConstraintBridge.RemoveDeadConstraints();
            if (dead > 0) LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: cleared " + dead + " constraint(s) left pointing at the replaced body.");
            if (w.WearerHadBPVagina && FindChild(cc.transform, VaginaBone) == null)
            {
                // Her body is still the one without vagina bones (the carry-over above either could not run
                // or has not finished).
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: '" + cc.name + "' has no " + VaginaBone + " yet — not re-linking the penis (it would anchor to the crotch bone). Press the apply hotkey once her body has loaded.");
                return;
            }
            ApplyPenisForWomb(w, cc);
            int dupes = NodeConstraintBridge.RemoveDuplicatePairs();
            if (dupes > 0) LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: cleaned " + dupes + " duplicate constraint(s) after the re-link.");
            WombExpandEffect.RequestRepair();   // re-pair the womb to the penis on the current links.
            WobbleSceneController.DeferBpRebind(w, cc);   // BP cleared her collision agent on the body reload.
        }

        // Where the penis entry is pinned on the receiver: whichever of her BP entry bones the womb actually
        // sits.
        private static readonly string[] BpEntryAnchors = { VaginaBone, "cf_J_Ana_Root" };
        private static readonly string[] VanillaEntryAnchors = { "k_f_ana_00", "cf_j_ana", FallbackBone };
        // A bone of HERS by that name - never one belonging to a womb item parented under.
        private static Transform HerBone(Component receiver, string name)
        {
            foreach (var tr in receiver.GetComponentsInChildren<Transform>(true))
                if (tr != null && tr.name == name && tr.GetComponentInParent<WombExpandEffect>() == null) return tr;
            return null;
        }

        private static Transform EntryAnchorFor(WombExpandEffect w, Component receiver, Transform ourEntry)
        {
            if (receiver == null || w == null) return null;
            Vector3 mouth = w.EntranceWorld();
            string wn = w.name;

            // Seated in one of her BetterPenetration orifices?
            var seats = new System.Collections.Generic.List<KeyValuePair<float, Transform>>();
            foreach (var nm in BpOrifices)
            {
                Transform b = HerBone(receiver, nm);
                if (b == null) continue;
                seats.Add(new KeyValuePair<float, Transform>(Vector3.Distance(b.position, mouth), b));
            }
            seats.Sort((x, y) => x.Key.CompareTo(y.Key));
            if (Debug && seats.Count > 0)
            {
                var sb = new System.Text.StringBuilder("AutoBodyReveal: womb '").Append(wn).Append("' orifice seats on '").Append(receiver.name).Append("':");
                foreach (var kv in seats) sb.Append(" ").Append(kv.Value.name).Append("=").Append((kv.Key * 100f).ToString("F1")).Append("cm");
                LiquidWobbleMPBPlugin._logger?.LogInfo(sb.ToString());
            }
            foreach (var kv in seats)
            {
                if (kv.Key > OrificeSeatRange) break;   // not seated in this one, nor any farther one.
                if (AnchorTakenBy(kv.Value, ourEntry)) continue;   // that orifice already has a penis.
                if (Debug) LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: womb '" + wn + "' entry anchor = '" + kv.Value.name
                    + "' (" + (kv.Key * 100f).ToString("F1") + "cm from the womb mouth; BetterPenetration drives this orifice).");
                return kv.Value;
            }

            // Anywhere else: the womb's own canal mouth, so the penis enters the womb where it actually.
            Transform canal = w.CanalEntryBone;
            if (canal != null)
            {
                LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: womb '" + wn + "' is not seated in one of "
                    + receiver.name + "'s BetterPenetration orifices - anchoring the penis entry at the womb's own canal mouth.");
                return canal;
            }

            // Old womb mesh with no canal marker: her nearest orifice bone, vanilla ones included.
            Transform best = null; float bestD = float.MaxValue;
            foreach (var nm in FallbackOrifices)
            {
                Transform o = HerBone(receiver, nm);
                if (o == null) continue;
                float d = Vector3.Distance(o.position, mouth);
                if (d < bestD) { bestD = d; best = o; }
            }
            if (best != null)
                LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: womb '" + wn + "' has no clo_canal_entry marker (old mesh) - anchoring the penis entry at '"
                    + best.name + "' instead. Re-add the womb from the current mod for an exact entry.");
            return best;
        }

        // BP's own entry targets: the vagina and the anus.
        private static readonly string[] BpOrifices = { VaginaBone, "cf_J_Ana_Root" };
        private static readonly string[] FallbackOrifices = { VaginaBone, "cf_J_Ana_Root", "k_f_ana_00", "cf_j_ana", FallbackBone };
        private const float OrificeSeatRange = 0.10f;   // beyond this the womb is not sitting in that orifice.

        // Is this bone already driving a DIFFERENT penis's entry?
        private static bool AnchorTakenBy(Transform anchor, Transform ourEntry)
        {
            foreach (var pair in NodeConstraintBridge.LivePairs())
                if (pair.Key == anchor && pair.Value != null && pair.Value.name == DanEntryBone && pair.Value != ourEntry) return true;
            return false;
        }

        // Is a penetrating penis actually parked in this womb? Gates the uncensor force to real couples.
        private static bool HasPenetratorFor(WombExpandEffect w, Component receiver)
        {
            return FindPenetratorForWomb(w, receiver) != null;
        }

        // The male whose penis is parked in this womb (null when nobody is).
        internal static Component FindPenetratorForWomb(WombExpandEffect w, Component receiver)
        {
            if (w == null) return null;
            Transform target = FindChild(w.transform, "penis_target");
            if (target == null) return null;
            Component penetrator;
            Transform end = NearestPenisEnd(UnityEngine.Object.FindObjectsOfType<Transform>(), target.position, receiver, out penetrator);
            return (penetrator != null && end != null && Vector3.Distance(end.position, target.position) <= PenisWombRange) ? penetrator : null;
        }

        // Characters already tried to give a BP body after a replacement.
        private static readonly System.Collections.Generic.HashSet<int> _bpBodyForced = new System.Collections.Generic.HashSet<int>();

        // Full x-ray stamp of one character for one womb.
        internal static void StampWombChar(WombExpandEffect w, Component cc)
        {
            if (w == null || cc == null) return;
            if (Debug) MEBridge.DumpBodyState(cc, "pre-stamp");   // material-state dump: diagnostics only.
            CaptureWearer(w, cc);   // remember her body for a later character replacement.
            int st = w.OrganStencil();
            if (MEBridge.EnsureBodyReveal(cc, st, true, true))
                w.OnBodyRevealApplied();   // restore out-of-body interior+cum (new-spawn default hides them).
            if (LiquidWobbleMPBPlugin.CfgBodyVeil) MEBridge.EnsureBodyVeil(cc, st + 1, true, true);
            if (LiquidWobbleMPBPlugin.CfgClothesReveal) MEBridge.EnsureClothesReveal(cc, st, true);
            if (LiquidWobbleMPBPlugin.CfgClothesReveal && !MainGameWomb.IsStudio) MEBridge.EnsureAccessoryReveal(cc, st, true);   // Free-H: accessories dress the card - stamp them too.
        }

        internal static bool HasVaginaBone(Component cc) { return cc != null && FindChild(cc.transform, VaginaBone) != null; }

        // Re-entry after the body-uncensor carry-over. The uncensor rebuild runs inside UncensorSelector's.
        internal static void PostUncensorApply(WombExpandEffect w, Component cc)
        {
            if (w == null || cc == null || !LiquidWobbleMPBPlugin.CfgEnabled) return;
            if (SceneLoading) { LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: a scene load started during the uncensor restore - leaving it to the load."); return; }
            LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: '" + cc.name + "' body reloaded with the scene's uncensor - re-stamping the x-ray and re-making the penis links.");
            StampWombChar(w, cc);
            int dead = NodeConstraintBridge.RemoveDeadConstraints();
            if (dead > 0) LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: cleared " + dead + " constraint(s) left pointing at the replaced body.");
            ApplyPenisForWomb(w, cc);
            int dupes = NodeConstraintBridge.RemoveDuplicatePairs();
            if (dupes > 0) LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: cleaned " + dupes + " duplicate constraint(s) after the re-link.");
            WombExpandEffect.RequestRepair();   // re-pair the womb to the penis on the current links.
            WobbleSceneController.DeferBpRebind(w, cc);   // BP cleared her collision agent on the body reload.
        }

        // Manual hotkey: apply now to every character that has a womb within MaxRange of its vagina (covers
        // the initial placement, where no reload event fires).
        public static void ApplyAll()
        {
            if (!LiquidWobbleMPBPlugin.CfgEnabled) return;   // master toggle OFF -> hotkey does nothing.
            AttachLiquidWobbleSelected();   // bottles etc.: attach the wobble driver to the SELECTED item(s) only.
            var wombs = UnityEngine.Object.FindObjectsOfType<WombExpandEffect>();
            LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: manual apply — " + wombs.Length + " womb(s), MaxRange=" + MaxRange.ToString("F3") + "m.");
            if (wombs.Length == 0) { LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: manual apply - no CloXray wombs in scene."); return; }
            if (Debug) NodeConstraintBridge.DumpConstraints("constraints BEFORE apply");
            // NEAREST first. A penis can only be claimed once (the constraint guard refuses a second claim
            // on the same k_f_dan_end), so with several wombs in a scene the one processed first wins.
            var scan = UnityEngine.Object.FindObjectsOfType<Transform>();
            var order = new System.Collections.Generic.List<KeyValuePair<float, WombExpandEffect>>();
            foreach (var w0 in wombs)
                if (w0 != null) order.Add(new KeyValuePair<float, WombExpandEffect>(NearestPenisDistance(w0, scan), w0));
            order.Sort((x, y) => x.Key.CompareTo(y.Key));
            if (Debug && order.Count > 1)
            {
                var sb = new System.Text.StringBuilder("AutoBodyReveal: womb order (closest penis first):");
                foreach (var kv in order)
                    sb.Append(" '").Append(kv.Value.name).Append("'=").Append(kv.Key >= float.MaxValue * 0.5f ? "none" : kv.Key.ToString("F2") + "m");
                LiquidWobbleMPBPlugin._logger?.LogInfo(sb.ToString());
            }
            foreach (var kv in order)
            {
                var w = kv.Value;
                if (w == null) continue;
                Vector3 _ew = w.EntranceWorld();
                if (Debug) LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: womb '" + w.name + "' entranceWorld=" + _ew.ToString("F3") + " itemRoot=" + w.transform.position.ToString("F3") + (_ew == w.transform.position ? "  (!! cf_j_kokan NOT found -> using item root)" : ""));
                string howW;
                Component cc = ResolveWearer(w, out howW);
                if (cc != null)
                {
                    LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: womb '" + w.name + "' is worn by '" + cc.name + "' (" + howW + ").");
                    StampWombChar(w, cc);
                }
                else LiquidWobbleMPBPlugin._logger?.LogWarning("AutoBodyReveal: manual apply - womb '" + w.name + "' has no wearer (not parented to a character, and no " + VaginaBone + "/" + FallbackBone + " within " + MaxRange.ToString("F2") + "m).");
                // AUTO penis: x-ray + aim the PENETRATOR (the OTHER character that has a penis) at THIS womb.
                ApplyPenisForWomb(w, cc, true);   // hotkey = an explicit request to wire this pair up.
            }
            WombExpandEffect.RequestRepair();   // the hotkey may have added/aimed NC links -> re-pair every womb to its penis now.
        }

        // Distance from this womb's aim bone to the nearest penis that could claim it (its own wearer
        // excluded).
        private static float NearestPenisDistance(WombExpandEffect w, Transform[] scan)
        {
            if (w == null) return float.MaxValue;
            Transform target = FindChild(w.transform, "penis_target");
            if (target == null) return float.MaxValue;
            string how;
            Component wearer = ResolveWearer(w, out how);
            Component pen;
            Transform end = NearestPenisEnd(scan, target.position, wearer, out pen, target);
            return (pen != null && end != null) ? Vector3.Distance(end.position, target.position) : float.MaxValue;
        }

        // MAIN-GAME direct path: the womb was spawned ON a known character, so skip the proximity search
        // entirely.
        public static void RemoveForWomb(Component receiver)
        {
            if (MainGameWomb.IsStudio || receiver == null) return;
            MEBridge.RemoveXrayCopies(receiver, Debug);
            // Her partner's penis copy only goes if no other womb is left in the scene.
            if (MainGameWomb.AnySpawned()) return;
            Component male = MainGameWomb.FindNearestMaleWithPenis(receiver.transform.position, 2f, receiver);
            if (male != null) MEBridge.RemoveXrayCopies(male, Debug);
        }

        public static void ApplyForWomb(WombExpandEffect w, Component cc)
        {
            if (!LiquidWobbleMPBPlugin.CfgEnabled || w == null || cc == null) return;
            int st = w.OrganStencil();
            LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: direct apply — womb '" + w.name + "' on '" + cc.name + "' stencil=" + st + ".");
            if (MEBridge.EnsureBodyReveal(cc, st, Debug, true))
                w.OnBodyRevealApplied();   // restore out-of-body interior+cum (new-spawn default hides them).
            if (LiquidWobbleMPBPlugin.CfgBodyVeil) MEBridge.EnsureBodyVeil(cc, st + 1, Debug, true);
            if (LiquidWobbleMPBPlugin.CfgClothesReveal) MEBridge.EnsureClothesReveal(cc, st, Debug);
            if (LiquidWobbleMPBPlugin.CfgClothesReveal && !MainGameWomb.IsStudio) MEBridge.EnsureAccessoryReveal(cc, st, Debug);
            if (MainGameWomb.IsStudio)
            {
                ApplyPenisForWomb(w, cc);
            }
            else
            {
                // MAIN GAME: BP's k_f_dan_end marker does NOT track the visible penis there (it idles ~0.7m
                // off even mid-insertion), so the Studio marker-distance gate always rejects.
                Component male = MainGameWomb.FindNearestMaleWithPenis(w.transform.position, 2f, cc);
                if (male != null)
                {
                    MEBridge.EnsurePenisOrgInside(male, st, Debug, LiquidWobbleMPBPlugin.CfgHPenisOutside,
                                                  LiquidWobbleMPBPlugin.CfgHPenisBottomWindow ? 1f : 0f);
            MEBridge.EnsureBallsOrgInside(male, st, Debug);   // the shaft/balls junction, or it reads white in the window.
                    MEBridge.EnsureBallsOrgInside(male, st, Debug);   // the shaft/balls junction, or it reads white in the window.
                    MainGameWomb.AttachPenisAim(w, male, cc);   // pin BP's inner limit at the womb's penis_target.
                    LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: main-game penis x-ray on '" + male.name + "' (stencil " + st + ").");
                    if (Debug) MEBridge.DumpXrayChain(male, cc, w);
                }
                else LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: no male with a penis material within 2m of the womb — penis not x-rayed.");
            }
        }

        public static void ReapplyMainGamePenisXray(WombExpandEffect w, Component male)
        {
            if (w == null || male == null || MainGameWomb.IsStudio) return;
            if (!LiquidWobbleMPBPlugin.CfgEnabled) return;
            int st = w.OrganStencil();
            MEBridge.EnsurePenisOrgInside(male, st, Debug, LiquidWobbleMPBPlugin.CfgHPenisOutside,
                                          LiquidWobbleMPBPlugin.CfgHPenisBottomWindow ? 1f : 0f);
            MEBridge.EnsureBallsOrgInside(male, st, Debug);   // the shaft/balls junction, or it reads white in the window.
            LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: penis x-ray RE-APPLIED on '" + male.name + "' after a body reload (stencil " + st + ") — the reload wiped the instanced copies.");
            if (Debug) MEBridge.DumpXrayChain(male, null, w);
        }

        // Same story for HER body. The BP5 body-uncensor force reloads the body mesh, which wipes the
        // instanced cf_m_body .MECopy x-ray materials.
        public static void ReapplyMainGameBodyXray(WombExpandEffect w, Component female)
        {
            if (w == null || female == null || MainGameWomb.IsStudio) return;
            if (!LiquidWobbleMPBPlugin.CfgEnabled) return;
            int st = w.OrganStencil();
            if (MEBridge.EnsureBodyReveal(female, st, Debug, true))
                w.OnBodyRevealApplied();
            if (LiquidWobbleMPBPlugin.CfgBodyVeil) MEBridge.EnsureBodyVeil(female, st + 1, Debug, true);
            if (LiquidWobbleMPBPlugin.CfgClothesReveal)
            {
                MEBridge.EnsureClothesReveal(female, st, Debug);
                MEBridge.EnsureAccessoryReveal(female, st, Debug);
            }
            LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: body x-ray RE-APPLIED on '" + female.name
                + "' after a body reload (stencil " + st + ") — the reload wiped the instanced copies.");
        }

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

        // Hotkey path: attach the wobble to the SELECTED Studio item(s) only.
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

        // For ONE womb: x-ray the PENETRATOR's penis and aim it at this womb.
        private static void ApplyPenisForWomb(WombExpandEffect w, Component receiver, bool manual = false)
        {
            try
            {
                Transform target = FindChild(w.transform, "penis_target");
                if (target == null) { LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: womb '" + w.name + "' has no penis_target bone."); return; }
                var all = UnityEngine.Object.FindObjectsOfType<Transform>();
                Component penetrator;
                Transform end = NearestPenisEnd(all, target.position, receiver, out penetrator, target);   // the OTHER character's penis, if unclaimed.
                if (penetrator == null || end == null)
                {
                    LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: womb '" + w.name + "': no penetrator (a cm_m_dankon character other than the receiver) found.");
                    return;
                }
                // Is that nearest penis actually IN this womb? With one male and several
                // females-each-with-a-womb, every womb finds the same lone male.
                float penisDist = Vector3.Distance(end.position, target.position);
                // The aim constraint is what HOLDS the penis at the womb, so its own marker springs back
                // once the links are deleted.
                bool bpSaysThisPair = receiver != null &&
                    (UnityEngine.Object)BPBridge.CollisionCharacterOf(penetrator) == (UnityEngine.Object)receiver;
                if (penisDist > PenisWombRange && (bpSaysThisPair || manual) && !NodeConstraintBridge.HasConstraintForNode(end))
                {
                    LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: womb '" + w.name + "': '" + penetrator.name + "' is "
                        + penisDist.ToString("F2") + "m from penis_target, but " + (bpSaysThisPair
                            ? "BetterPenetration has him bound to '" + receiver.name + "'"
                            : "the apply hotkey was pressed for this womb")
                        + " - aiming him at it (the aim link is what brings the penis to the womb).");
                    penisDist = 0f;
                }
                if (penisDist > PenisWombRange)
                {
                    LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: womb '" + w.name + "': nearest penis ('" + penetrator.name + "') is " + penisDist.ToString("F2") + "m from penis_target (> " + PenisWombRange.ToString("F2") + "m) -> that male is in a DIFFERENT womb; skipping penis x-ray + aiming for this one.");
                    return;
                }
                CapturePenetrator(w, penetrator);   // remember his penis uncensor for a later male replacement.
                Transform entry  = FindChild(penetrator.transform, DanEntryBone);
                Transform vagina = EntryAnchorFor(w, receiver, entry);
                int st = w.OrganStencil();
                LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: womb '" + w.name + "' -> receiver='" + (receiver != null ? receiver.name : "none") + "' penetrator='" + penetrator.name + "' vagina=" + (vagina != null ? "'" + vagina.name + "'" : "NONE") + " stencil=" + st + ".");
                if (Debug)
                {
                    // POSITION READOUT: is penis_target actually AT the womb?
                    Vector3 ewp = w.EntranceWorld();
                    LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal:   POS penis_target=" + target.position.ToString("F2") + " wombEntrance=" + ewp.ToString("F2") + " (target<->entrance=" + Vector3.Distance(target.position, ewp).ToString("F2") + "m)  penisEnd=" + end.position.ToString("F2") + " (end<->target=" + Vector3.Distance(end.position, target.position).ToString("F2") + "m)" + (vagina != null ? "  vagina=" + vagina.position.ToString("F2") : ""));
                }
                // penis_target is BAKED at the womb's tube centre in the mod -> the plugin just constrains
                // the penis.
                if (target.childCount > 0)
                    LiquidWobbleMPBPlugin._logger?.LogWarning("AutoBodyReveal: penis_target has " + target.childCount + " child bone(s) -> OLD load-bearing bone; rebuild the womb with the centred leaf aim bone.");
                else if (Debug)
                {
                    Vector3 tfoot;
                    if (w.SnapToTubeCenter(target.position, out tfoot))
                        LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal:   penis_target offCentre=" + ((target.position - tfoot).magnitude * 1000f).ToString("F1") + "mm from tube centre.");
                    LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal:   GUIDE-OBJ? penis_target=" + GuideObjectBridge.IsGuideObject(target) + " vagina=" + (vagina != null ? GuideObjectBridge.IsGuideObject(vagina) : null) + " k_f_dan_entry=" + (entry != null ? GuideObjectBridge.IsGuideObject(entry) : null) + " k_f_dan_end=" + GuideObjectBridge.IsGuideObject(end) + "  (blank = couldn't read)");
                }
                MEBridge.EnsurePenisOrgInside(penetrator, st, true);   // x-ray the penetrator's penis, matched to THIS womb.
                MainGameWomb.DumpBPDanOptions(penetrator);   // log this male's BP DanOptions (harvest for the Free-H override).
                if (NodeConstraintBridge.Available)
                {
                    if (entry != null && vagina != null)
                    {
                        if (NodeConstraintBridge.HasConstraintForNode(entry)) LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: k_f_dan_entry already has a constraint -> leaving your target, not reassigning.");
                        else NodeConstraintBridge.AddPositionLink(vagina, entry, "");   // empty alias -> shows raw "parent -> child" like a manual one.
                    }
                    if (NodeConstraintBridge.HasConstraintForNode(end)) LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: k_f_dan_end already has a constraint -> leaving your target, not reassigning.");
                    else NodeConstraintBridge.AddPositionLink(target, end, "");
                }
                else LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: NodesConstraints not present -> penis x-rayed but not aimed.");
            }
            catch (System.Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("AutoBodyReveal: penis-for-womb failed on '" + (w != null ? w.name : "?") + "': " + e.Message); }
        }

        // Nearest k_f_dan_end under a ChaControl that carries a cm_m_dankon penis, OTHER than `exclude` (the
        // receiver).
        private static Transform NearestPenisEnd(Transform[] all, Vector3 pos, Component exclude, out Component owner, Transform myTarget = null)
        {
            Transform best = null; float bsq = float.MaxValue; owner = null;
            foreach (var t in all)
            {
                if (t == null || t.name != "k_f_dan_end") continue;
                Component cc = FindChaControlOf(t);
                if (cc == null || cc == exclude || !MEBridge.HasPenisMaterial(cc)) continue;
                if (myTarget != null)
                {
                    Transform holder = NodeConstraintBridge.ParentOfChild(t);
                    if (holder != null && holder != myTarget) continue;   // another womb already owns this penis.
                }
                float d = (t.position - pos).sqrMagnitude; if (d < bsq) { bsq = d; best = t; owner = cc; }
            }
            return best;
        }

        private static Component FindChaControlOf(Transform t)
        {
            for (var c = t; c != null; c = c.parent) { var cc = c.GetComponent("ChaControl"); if (cc != null) return cc; }
            return null;
        }

        // Nearest character (ChaControl) to a world point, within maxRange.
        private static Component FindNearestCharacter(Vector3 pos, float maxRange = float.MaxValue)
        {
            int cand;
            Component best = FindNearestByBone(pos, maxRange, VaginaBone, false, out cand);
            if (best == null)
            {
                // No vagina-root candidate at all (BP-less body) OR every one out of range.
                LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal:   '" + VaginaBone + "' " + (cand == 0 ? "not present on any character" : "candidate(s) out of range") + " -> fallback to vanilla '" + FallbackBone + "' (female-only).");
                best = FindNearestByBone(pos, maxRange, FallbackBone, true, out cand);
            }
            return best;
        }

        // Nearest ChaControl owning a bone named `boneName` to `pos`, within maxRange.
        private static Component FindNearestByBone(Vector3 pos, float maxRange, string boneName, bool femaleOnly, out int candCount)
        {
            Component best = null; float bestSq = float.MaxValue; int cand = 0; string bestName = "none";
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
            {
                if (t == null || t.name != boneName) continue;
                if (t.GetComponentInParent<WombExpandEffect>() != null) continue;
                Component cc = null;
                for (var c = t; c != null; c = c.parent) { cc = c.GetComponent("ChaControl"); if (cc != null) break; }
                if (cc == null) continue;   // not under a character (e.g. a free-standing womb item) -> skip.
                if (femaleOnly && !IsFemale(cc)) { LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal:   skip '" + cc.name + "' (not female) for fallback bone."); continue; }
                cand++;
                float d = (t.position - pos).sqrMagnitude;
                LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal:   candidate '" + cc.name + "' " + boneName + "@" + t.position.ToString("F3") + " dist=" + Mathf.Sqrt(d).ToString("F3") + "m");   // DIAG.
                if (d < bestSq) { bestSq = d; best = cc; bestName = cc.name; }
            }
            candCount = cand;
            float bestDist = best != null ? Mathf.Sqrt(bestSq) : -1f;   // DIAG.
            LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal:   -> " + cand + " '" + boneName + "' candidate(s) under a ChaControl; nearest '" + bestName + "' dist=" + (bestDist >= 0f ? bestDist.ToString("F3") + "m" : "n/a") + " vs maxRange=" + maxRange.ToString("F3") + "m" + (bestDist >= 0f && bestDist > maxRange ? "  -> REJECTED (raise the range or move the womb)" : ""));   // DIAG.
            if (best != null && maxRange < float.MaxValue && Mathf.Sqrt(bestSq) > maxRange) return null;
            return best;
        }

        // Female? Read the character's sex by reflection (no hard ChaControl ref).
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
