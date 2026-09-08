using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace LiquidWobbleMPB
{
    // Carry the AUTHOR's CloXray material setup across a Studio character
    // REPLACE, so a scene made with these shaders survives swapping a character for a different card,
    // WITHOUT needing a womb in the scene (the womb-proximity re-apply is womb-gated; a shader-only
    // scene would otherwise lose the x-ray on swap).
    //
    // MECHANISM (event-driven, keyed to the STUDIO SLOT which survives a replace):
    //   - CAPTURE: read the CloXray material COPIES straight off the live
    //     renderers - the copies ME has already restored - and record each copy's shader, queue and
    // the KNOWN x-ray float properties. (The earlier private-ME-record reflection was fragile:
    //     per-record-type fields + ObjectType matching silently found nothing. The live materials are
    //     exactly what `hasOurShaders` already proved is present, so this can't miss.)
    //   • REPLAY on the replacement: recreate the copies FROM THE NEW CHARACTER'S base material (so it
    // keeps its own textures/look), set the shader + queue + the captured x-ray floats, then
    //     RE-DERIVE clothes reveal on the new outfit (EnsureClothesReveal at the captured stencil).
    //
    // ANTI-DEGRADATION: only look-NEUTRAL x-ray params cross over; textures come from the fresh copy.
    // A NON-CloXray copy (the penis "original-look" re-draw @3502) keeps the new char's own shader -
    // only its render queue is carried.
    //
    // Studio-only; if ME reflection can't bind it goes inert (loud error once), never a silent fallback.
    internal static class ShaderCarryOver
    {
        private const string CloPrefix = "CloXray/";
        private const int OriginalRedrawQueue   = 3502;   // the one non-CloXray copy still recreated (penis look)
        private const int OriginalRedrawQueueUp = 2484;   // ...and its upgraded-stack twin

        // Unity property names (with leading _) of every setting these shaders expose. ME's setter takes
        // the name WITHOUT the underscore, so that is what is stored.
        private static readonly string[] KnownProps =
        {
            "_StencilRef", "_StencilBody", "_StencilBody_Plus_1",
            "_OutsideOfBodyAlpha", "_OutsideShieldDepth", "_BottomWindow", "_XrayAlpha",
            "_StencilWriteMask", "_StencilReadMask"
        };

        private class CopyRec
        {
            public string baseName;                 // cf_m_body / cm_m_dankon
            public int index;                       // .MECopyN index, for a stable recreate order
            public bool clo;                        // shader is one of ours
            public string shader;                   // shader name (applied only when clo)
            public int queue;                       // render queue
            public readonly Dictionary<string, float> floats = new Dictionary<string, float>();   // ME-name -> value
        }
        private class Snap { public readonly List<CopyRec> copies = new List<CopyRec>(); public int stencil = -1; }

        private static readonly Dictionary<int, Snap> _snap = new Dictionary<int, Snap>();

        // ── reflection: just the ME controller type + ObjectType.Character + the setters ───────────
        private static bool _init, _ok;
        private static Type _ctrlType, _objTypeEnum; private static object _otChar;
        private static MethodInfo _mCopyRemove, _mSetShader, _mSetQueue, _mSetFloat;

        private static void EnsureInit(Type ccType)
        {
            if (_init) return;
            _init = true;
            try
            {
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { var t = a.GetType("KK_Plugins.MaterialEditor.MaterialEditorCharaController", false); if (t != null) { _ctrlType = t; break; } }
                    catch { }
                }
                if (_ctrlType == null) { Fail("MaterialEditorCharaController not found"); return; }
                _objTypeEnum = _ctrlType.GetNestedType("ObjectType");
                if (_objTypeEnum == null) { Fail("ObjectType enum not found"); return; }
                _otChar = Enum.Parse(_objTypeEnum, "Character");

                var mMat = typeof(Material); var mGo = typeof(GameObject); var mStr = typeof(string);
                _mCopyRemove = _ctrlType.GetMethod("MaterialCopyRemove", new[] { typeof(int), _objTypeEnum, mMat, mGo });
                _mSetShader  = _ctrlType.GetMethod("SetMaterialShader",  new[] { typeof(int), _objTypeEnum, mMat, mStr, mGo, typeof(bool) });
                _mSetQueue   = _ctrlType.GetMethod("SetMaterialShaderRenderQueue", new[] { typeof(int), _objTypeEnum, mMat, typeof(int), mGo, typeof(bool) });
                _mSetFloat   = _ctrlType.GetMethod("SetMaterialFloatProperty", new[] { typeof(int), _objTypeEnum, mMat, mStr, typeof(float), mGo, typeof(bool) });
                if (_mCopyRemove == null || _mSetShader == null || _mSetQueue == null || _mSetFloat == null) { Fail("ME setter methods not found"); return; }

                _ok = true;
                InstallCaptureHook(ccType);
                LiquidWobbleMPBPlugin._logger?.LogInfo("ShaderCarryOver: ready (Studio character-replace carry-over armed).");
            }
            catch (Exception e) { Fail(e.GetType().Name + ": " + e.Message); }
        }

        private static void Fail(string why)
        {
            _ok = false;
            LiquidWobbleMPBPlugin._logger?.LogError("ShaderCarryOver: DISABLED — " + why + ". Character-replace carry-over will NOT run (no fallback). Fix the ME reflection binding.");
        }

        // Pre-reload capture: snapshot the OUTGOING character before its card swaps (covers a setup
        // applied in-session with no reload after). Walk the ChaControl type hierarchy for ChangeChara.
        private static HarmonyLib.Harmony _hook; private static bool _hookInstalled;
        // PRE-REPLACE hook. The replace call lives on Studio's OCIChar, NOT on ChaControl - patching the
        // ChaControl hierarchy found zero overloads and this hook silently never ran, leaving the snapshot
        // to the reload path alone. It also arms the uncensor hand-over, which has to happen before the new
        // card is read.
        private static void InstallCaptureHook(Type ccType)
        {
            if (_hookInstalled) return;
            _hookInstalled = true;
            try
            {
                _hook = new HarmonyLib.Harmony("Clo.LiquidWobbleMPB.carryover");
                var prefix = new HarmonyLib.HarmonyMethod(typeof(ShaderCarryOver).GetMethod(nameof(BeforeReload), BindingFlags.Static | BindingFlags.NonPublic));
                int n = 0;
                foreach (var m in typeof(Studio.OCIChar).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (m.Name == "ChangeChara" && !m.IsGenericMethod && !m.IsAbstract && m.GetMethodBody() != null)
                        try { _hook.Patch(m, prefix: prefix); n++; } catch (Exception pe) { LiquidWobbleMPBPlugin._logger?.LogWarning("ShaderCarryOver: could not patch an OCIChar.ChangeChara overload: " + pe.Message); }
                if (n > 0) LiquidWobbleMPBPlugin._logger?.LogInfo("ShaderCarryOver: pre-replace hook installed on OCIChar.ChangeChara (" + n + " overload(s)).");
                else LiquidWobbleMPBPlugin._logger?.LogError("CloXray: OCIChar.ChangeChara not found - a character replacement cannot be seen BEFORE the new card loads, so the scene's body uncensor is restored after the fact instead of being handed to the card.");
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: pre-replace hook install failed (" + e.Message + ") - the scene's body uncensor will be restored after the swap instead of handed to the incoming card."); }
        }

        private static void BeforeReload(object __instance)
        {
            try
            {
                if (!MainGameWomb.IsStudio) return;
                var oci = __instance as Studio.OCIChar;
                var cc = oci != null ? oci.charInfo as Component : null;
                if (cc == null) return;

                // Hand this scene's body uncensor to the card that is about to load.
                string how;
                var womb = AutoBodyReveal.WombOfWearer(cc, out how);
                if (womb != null && womb.WearerHadBPVagina && !string.IsNullOrEmpty(womb.WearerBodyGuid))
                    MainGameWomb.UncensorInject.Arm(womb.WearerBodyGuid);

                if (!_ok) return;
                int key = SlotKey(cc);
                if (key < 0 || !HasOurMaterials(cc)) return;
                Capture(cc, key);
                LiquidWobbleMPBPlugin._logger?.LogInfo("ShaderCarryOver: pre-replace snapshot of slot " + key + " ('" + cc.name + "') taken before its card swaps.");
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("ShaderCarryOver.BeforeReload: " + e.Message); }
        }

        // ── public entry: called from the deferred (post-ME-restore) reload path ───────────────────
        // Returns TRUE only when it REPLAYED a snapshot (the caller then skips the womb-proximity apply).
        /// Drop every snapshot. Studio numbers its objects per scene and re-uses the low ids, so a
        /// snapshot taken from slot 3 in the OUTGOING scene would be replayed onto whoever lands in
        /// slot 3 of the incoming one - creating material copies and setting CloXray shaders on a
        /// character the loaded scene never gave any to. Carry-over exists for a REPLACE inside one
        /// scene; a Load or a Clear ends the scene its snapshots describe.
        internal static void ForgetAll()
        {
            if (_snap.Count == 0) return;
            LiquidWobbleMPBPlugin._logger?.LogInfo("ShaderCarryOver: dropping " + _snap.Count
                + " material snapshot(s) - they belong to the outgoing scene.");
            _snap.Clear();
        }

        public static bool OnReloaded(Component cc)
        {
            if (!MainGameWomb.IsStudio || cc == null) return false;
            EnsureInit(cc.GetType());
            if (!_ok) return false;
            int key = SlotKey(cc);
            if (key < 0) { LiquidWobbleMPBPlugin._logger?.LogInfo("ShaderCarryOver: reload of '" + cc.name + "' — no studio slot key yet (skipped)."); return false; }
            try
            {
                bool has = HasOurMaterials(cc), have = _snap.ContainsKey(key);
                LiquidWobbleMPBPlugin._logger?.LogInfo("ShaderCarryOver: reload slot " + key + " '" + cc.name + "' — hasOurShaders=" + has + ", storedSnapshot=" + have + " -> " + (has ? "CAPTURE" : (have ? "REPLAY" : "nothing")) + ".");
                if (has) { Capture(cc, key); return false; }
                if (have) { Replay(cc, key); return true; }
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogError("ShaderCarryOver.OnReloaded failed on '" + cc.name + "': " + e.GetType().Name + ": " + e.Message); }
            return false;
        }

        private static int SlotKey(Component cc)
        {
            var studio = Studio.Studio.Instance;
            if (studio == null || studio.dicObjectCtrl == null) return -1;
            foreach (var kv in studio.dicObjectCtrl)
            {
                var oci = kv.Value as Studio.OCIChar;
                if (oci != null && ReferenceEquals(oci.charInfo, cc)) return kv.Key;
            }
            return -1;
        }

        // A womb (clo_xraywomb) is a Studio item often parented UNDER the character; its own CloXray
        // Liquid/Organ materials must NOT count as "the character has CloXray shaders" nor be captured -
        // only the character's own body/clothes REVEAL COPIES are ours to carry. Skip womb subtrees.
        private static bool IsWombRenderer(Renderer r)
        {
            return r != null && r.GetComponentInParent<WombExpandEffect>() != null;
        }

        private static bool HasOurMaterials(Component cc)
        {
            foreach (var r in cc.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || r.sharedMaterials == null || IsWombRenderer(r)) continue;
                foreach (var m in r.sharedMaterials)
                    if (m != null && m.name.IndexOf(".MECopy", StringComparison.Ordinal) >= 0 && m.shader != null && m.shader.name.StartsWith(CloPrefix)) return true;
            }
            return false;
        }

        private static string BaseName(string matName)
        {
            string b = matName;
            while (b.EndsWith(" (Instance)")) b = b.Substring(0, b.Length - " (Instance)".Length);
            int i = b.IndexOf(".MECopy", StringComparison.Ordinal);
            if (i >= 0) b = b.Substring(0, i);
            return b;
        }
        private static int CopyIndex(string matName)
        {
            int i = matName.IndexOf(".MECopy", StringComparison.Ordinal);
            if (i < 0) return 0;
            int j = i + ".MECopy".Length, k = j;
            while (k < matName.Length && char.IsDigit(matName[k])) k++;
            int n; return int.TryParse(matName.Substring(j, k - j), out n) ? n : 0;
        }

        // Read the copies straight off the live renderers (ME already restored them).
        private static void Capture(Component cc, int key)
        {
            var snap = new Snap();
            string dbg = "";
            // Only materials on the BODY object are captured. Replay hands MaterialEditor that same
            // GameObject with ObjectType.Character and ME looks for the source material underneath it, so
            // a garment material (which lives on a clothing object) can never be recreated this way - the
            // copy call returns an empty name and records a row ME cannot resolve. Clothes do not need
            // carrying anyway: a replacement card wears a different outfit, and the clothes x-ray is
            // re-derived for what she is actually wearing. The penis/balls materials DO sit under the body
            // object, so the male's x-ray still carries.
            var bodyGo = BodyGo(cc);
            Transform bodyT = bodyGo != null ? bodyGo.transform : null;
            int skippedClothes = 0;
            foreach (var r in cc.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || r.sharedMaterials == null || IsWombRenderer(r)) continue;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || m.shader == null) continue;
                    bool isCopy = m.name.IndexOf(".MECopy", StringComparison.Ordinal) >= 0;
                    if (isCopy || m.shader.name.StartsWith(CloPrefix))
                        dbg += "\n    [" + r.name + "] '" + m.name + "' shader='" + m.shader.name + "' q=" + m.renderQueue;
                    if (!isCopy) continue;
                    bool clo = m.shader.name.StartsWith(CloPrefix);
                    bool origLook = !clo && (m.renderQueue == OriginalRedrawQueue || m.renderQueue == OriginalRedrawQueueUp);   // penis original-look copy, either stack
                    if (!clo && !origLook) continue;
                    if (bodyT == null || !r.transform.IsChildOf(bodyT)) { skippedClothes++; continue; }

                    var rec = new CopyRec { baseName = BaseName(m.name), index = CopyIndex(m.name), clo = clo, shader = m.shader.name, queue = m.renderQueue };
                    if (clo)
                        foreach (var up in KnownProps)
                            if (m.HasProperty(up)) rec.floats[up.Substring(1)] = m.GetFloat(up);   // ME name drops the leading _
                    snap.copies.Add(rec);
                    if (m.HasProperty("_StencilRef")) snap.stencil = Mathf.RoundToInt(m.GetFloat("_StencilRef"));
                }
            }
            if (snap.copies.Count == 0)
            {
                LiquidWobbleMPBPlugin._logger?.LogWarning("ShaderCarryOver: capture of slot " + key + " ('" + cc.name + "') found NO CloXray copies — nothing stored. Candidate materials (copies / CloXray-shaded):" + (dbg.Length == 0 ? " NONE" : dbg));
                return;
            }
            if (skippedClothes > 0)
                LiquidWobbleMPBPlugin._logger?.LogInfo("ShaderCarryOver: " + skippedClothes + " clothing copy(ies) not captured - the clothes x-ray is re-derived on the new character's own outfit.");
            snap.copies.Sort((a, b) => { int c = string.CompareOrdinal(a.baseName, b.baseName); return c != 0 ? c : a.index.CompareTo(b.index); });
            _snap[key] = snap;
            LiquidWobbleMPBPlugin._logger?.LogInfo("ShaderCarryOver: CAPTURED slot " + key + " ('" + cc.name + "') — " + snap.copies.Count + " copy(ies), stencil=" + snap.stencil + ".");
        }

        private static void Replay(Component cc, int key)
        {
            var snap = _snap[key];
            var ctrl = cc.GetComponent(_ctrlType);
            if (ctrl == null) { LiquidWobbleMPBPlugin._logger?.LogError("ShaderCarryOver: new character in slot " + key + " has no ME controller — cannot carry over."); return; }
            var go = BodyGo(cc);
            if (go == null) { LiquidWobbleMPBPlugin._logger?.LogError("ShaderCarryOver: no body GameObject on '" + cc.name + "' — cannot carry over."); return; }

            // group by base, preserving the captured (index-sorted) order
            var byBase = new Dictionary<string, List<CopyRec>>();
            foreach (var rec in snap.copies)
            {
                List<CopyRec> l; if (!byBase.TryGetValue(rec.baseName, out l)) { l = new List<CopyRec>(); byBase[rec.baseName] = l; }
                l.Add(rec);
            }

            int made = 0;
            foreach (var kv in byBase)
            {
                Renderer rend; var baseMat = FindMaterial(cc, kv.Key, out rend);
                if (baseMat == null) { ReportNotCarried(kv.Key, "she does not have that material"); continue; }
                foreach (var rec in kv.Value)
                {
                    // The new copy is identified BY NAME, never by reference: assigning renderer.materials
                    // (which ME's copy call does internally) re-instances every entry, so on a pristine
                    // incoming body a reference-diff saw ALL materials as "new" and resolved the copy to
                    // sharedMaterials[0] - the ORIGINAL - which then had a CloXray shader persisted onto
                    // it (the invisible-body corruption).
                    // Which copy did ME just make? Its own MaterialCopyList says so. Diffing the
                    // renderer's materials cannot: ME's copy call reassigns the array, so on a pristine
                    // incoming body every entry looks new and the "copy" resolved to the ORIGINAL - which
                    // then had a CloXray shader persisted onto it (the invisible-body corruption).
                    Material nw = CreateCopyTracked(ctrl, rend, baseMat, go, kv.Key);
                    if (nw == null) continue;

                    // CloXray copy -> set the CloXray shader; NON-CloXray (penis look) -> keep the new char's own shader.
                    if (rec.clo && !string.IsNullOrEmpty(rec.shader)) _mSetShader.Invoke(ctrl, new object[] { 0, _otChar, nw, rec.shader, go, true });
                    _mSetQueue.Invoke(ctrl, new object[] { 0, _otChar, nw, rec.queue, go, true });   // queue = look-neutral, always safe
                    if (rec.clo)
                        foreach (var p in rec.floats)
                            _mSetFloat.Invoke(ctrl, new object[] { 0, _otChar, nw, p.Key, p.Value, go, true });
                    made++;
                }
            }

            // Clothes/accessory reveal is OUTFIT-specific - RE-DERIVE it on the new character's CURRENT
            // clothes using the captured stencil, exactly like the womb path (minus the womb). Same
            // CfgClothesReveal gate. Studio path = clothes only (accessory reveal is Free-H).
            if (LiquidWobbleMPBPlugin.CfgClothesReveal && snap.stencil >= 0)
                // allowCreate: TRUE. This is a character SWAP - the new girl's outfit has no copies at
                // all, and carry-over exists precisely to reproduce what the old one had. Leaving her
                // dressed opaque would defeat the feature. Not the passive-load case guarded elsewhere.
                MEBridge.EnsureClothesReveal(cc, snap.stencil, false, true);

            if (_notCarried.Count > 0)
            {
                LiquidWobbleMPBPlugin._logger?.LogInfo("ShaderCarryOver: " + _notCarried.Count + " clothing copy(ies) not carried because the new card wears a different outfit ("
                    + string.Join(", ", _notCarried.ToArray()) + ") - the clothes x-ray is re-derived for what she is actually wearing.");
                _notCarried.Clear();
            }
            LiquidWobbleMPBPlugin._logger?.LogInfo("ShaderCarryOver: CARRIED OVER to '" + cc.name + "' (slot " + key + ") — recreated " + made + "/" + snap.copies.Count
                + " copy(ies)" + (LiquidWobbleMPBPlugin.CfgClothesReveal && snap.stencil >= 0 ? " + re-derived clothes reveal @stencil " + snap.stencil : "") + " (new char keeps its own textures/look).");
        }

        // Creates a copy through ME (persisted) and returns exactly the material ME recorded, or null
        // with a logged reason. Mirrors MEBridge.CreateCopyTracked; both read ME's MaterialCopyList.
        private static FieldInfo _fCopyList, _fCopyName, _fCopySource;

        // A missing BODY material is a real failure (that is the x-ray stamp). A missing GARMENT material
        // is the normal case: the incoming card wears a different outfit, and the clothes reveal is
        // RE-DERIVED for what she is actually wearing rather than copied. Those are collected and reported
        // as one line instead of one alarming error each.
        private static readonly List<string> _notCarried = new List<string>();
        private static bool IsBodyMat(string n) { return n != null && n.IndexOf("_m_body", StringComparison.OrdinalIgnoreCase) >= 0; }
        private static void ReportNotCarried(string matName, string why)
        {
            if (IsBodyMat(matName))
            { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: the x-ray copy of body material '" + matName + "' was NOT carried to the new character (" + why + ") - press the apply hotkey to re-stamp her."); return; }
            _notCarried.Add(matName);
        }
        private static Material CreateCopyTracked(object ctrl, Renderer rend, Material baseMat, GameObject go, string srcLabel)
        {
            const BindingFlags BI = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                if (_fCopyList == null) _fCopyList = _ctrlType.GetField("MaterialCopyList", BI);
                var rows = _fCopyList != null ? _fCopyList.GetValue(ctrl) as System.Collections.IList : null;
                if (rows == null)
                { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: MaterialEditor's MaterialCopyList is unreadable - no material copy can be carried to the new character (ME version changed?)."); return null; }
                string src = CleanName(baseMat.name);
                var before = new HashSet<string>();
                foreach (var r in rows) { string n = RowName(r, false); if (n != null) before.Add(n); }

                _mCopyRemove.Invoke(ctrl, new object[] { 0, _otChar, baseMat, go });

                rows = _fCopyList.GetValue(ctrl) as System.Collections.IList;
                string made = null;
                if (rows != null)
                    foreach (var r in rows)
                    {
                        string n = RowName(r, false);
                        if (n == null || before.Contains(n) || RowName(r, true) != src) continue;
                        made = n;
                    }
                // An EMPTY copy name means ME's CopyMaterial could not find the material to copy (the new
                // card does not have it) and still recorded a row - that row is junk ME can never resolve
                // on a later load, so drop it instead of letting it be saved.
                if (made != null && made.Length == 0) { DropEmptyCopyRow(rows, src); made = null; }
                if (made == null) { ReportNotCarried(srcLabel, "the new character does not have it"); return null; }
                foreach (var m in rend.sharedMaterials)
                    if (m != null && CleanName(m.name) == made) return m;
                ReportNotCarried(srcLabel, "MaterialEditor recorded copy '" + made + "' but it is not on renderer '" + rend.name + "'");
                return null;
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogError("ShaderCarryOver: copy creation failed for '" + srcLabel + "': " + e.Message); return null; }
        }

        // Removes the row ME just added with an empty copy name (see the call site).
        private static void DropEmptyCopyRow(System.Collections.IList rows, string src)
        {
            try
            {
                for (int i = rows.Count - 1; i >= 0; i--)
                {
                    string n = RowName(rows[i], false);
                    if (n != null && n.Length == 0 && RowName(rows[i], true) == src) { rows.RemoveAt(i); return; }
                }
            }
            catch { }
        }

        private static string RowName(object row, bool source)
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

        // Strips Unity's repeated " (Instance)" suffixes; ME names copies from the clean base name.
        private static string CleanName(string n)
        {
            const string suf = " (Instance)";
            while (n != null && n.EndsWith(suf)) n = n.Substring(0, n.Length - suf.Length);
            return n ?? "";
        }

        private static Material FindMaterial(Component cc, string matName, out Renderer rend)
        {
            foreach (var r in cc.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || r.sharedMaterials == null) continue;
                foreach (var m in r.sharedMaterials)
                    if (m != null && BaseName(m.name) == matName && m.name.IndexOf(".MECopy", StringComparison.Ordinal) < 0) { rend = r; return m; }
            }
            rend = null; return null;
        }

        private static GameObject BodyGo(Component cc)
        {
            const BindingFlags ANY = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var f = cc.GetType().GetField("objBody", ANY);
            if (f != null) { var g = f.GetValue(cc) as GameObject; if (g != null) return g; }
            var p = cc.GetType().GetProperty("objBody", ANY);
            if (p != null) { try { return p.GetValue(cc, null) as GameObject; } catch { } }
            return null;
        }
    }
}
