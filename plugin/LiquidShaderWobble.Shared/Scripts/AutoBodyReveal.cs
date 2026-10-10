using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace LiquidWobbleMPB
{
    /// <summary>
    /// Auto-applies the CloXray "BodyReveal" stencil-writer to a character's body by DRIVING
    /// MaterialEditor's API (reflection, no hard dependency - same style as <see cref="BPBridge"/>).
    /// Replicates the manual workflow: body o_body_a / cf_m_body -> ME "Copy Material" (cf_m_body.MECopy)
    /// -> shader "CloXray/BodyReveal" -> _StencilRef. MaterialEditor then PERSISTS + re-applies the copy
    /// on its own reloads, so it only has to be (re)asserted when a fresh/swapped character appears.
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
        private const string BallsMat        = "cm_m_dan_f";          // the male balls material (o_dan_f)
        // Garment/accessory stamp copies render one step BEFORE the body copy (2500), because the
        // body's LimbBlock pass runs last and overwrites limb pixels with the reserved region 31 -
        // a sleeve stamped afterwards would re-open the limb it is covering. Before the limb
        // feature existed these copies set no queue at all and inherited the shader's own 2500.
        // TWO STACKS. The SHIPPED default is the legacy one, so a scene made before any of this opens
        // exactly as it always did - including the CloXray shaders people applied to items of their own,
        // which carry no queue record at all and simply follow the shader tag. The UPGRADED stack lives
        // below Unity's 2500 opaque/transparent cutoff, where a draw still gets the screen-space shadow
        // buffer; that is what stops the in-window penis rendering flat. You opt in per character by
        // pressing the one-key hotkey, which writes the upgraded numbers through Material Editor so they
        // persist with the scene.
        // The one-key hotkey opts a STUDIO character in. The main game is not opt-in - it is always on
        // the sub-2500 stack, because the opt-in only exists to protect Studio scenes that persist
        // render queues through Material Editor, and Free-H has no scene file carrying them.
        //
        // That invariant used to live in the call sites, and one of them did not have it: the Free-H
        // re-apply after a body reload (ReapplyMainGameBodyXray) left the flag false, so every queue it
        // wrote came out LEGACY. The garment cut went back to 2499 while her body stamp stayed at 2479
        // and the womb at 2482 - a stamp drawn AFTER the organ it is supposed to open, so the clothes
        // stopped being see-through about a tenth of a second after each hotkey press while the womb
        // itself stayed correct. Free-H is now upgraded by CONSTRUCTION, not by remembering.
        private static bool _upgradeStack;
        internal static bool UpgradeStack
        {
            get { return _upgradeStack || !MainGameWomb.IsStudio; }
            set { _upgradeStack = value; }
        }
        private static int Q(int legacy, int upgraded) { return UpgradeStack ? upgraded : legacy; }
        // True for a value belonging to EITHER stack - an assertion must accept both, or re-applying to
        // an upgraded scene (a plain load, no hotkey) would drag it back to legacy.
        private static bool QueueOk(int q, int legacy, int upgraded) { return q == legacy || q == upgraded; }

        private const int GarmentStampQueue         = 2499;
        private const int GarmentStampQueueUp       = 2478;
        // Garment GHOST layer: a second per-garment copy on CloXray/BodyRevealExtra (the same shader
        // the skin veil uses) that redraws the fabric translucently over the x-ray window - so a
        // shirt over the belly ghosts at the XrayAlpha strength instead of being cut through
        // completely. One past the skin veil (3504): fabric is outermost, ties are sorting luck.
        private const int GarmentVeilQueue          = 3505;
        private const int GarmentVeilQueueUp        = 2487;
        private const int   BodyStampQueue   = 2500;   // the body stencil stamp: MUST precede every organ layer
        private const int   BodyStampQueueUp = 2479;
        private const int   BodyVeilQueue    = 3504;   // after the WHOLE womb stack - XrayAlpha = master fade
        private const int   BodyVeilQueueUp  = 2486;
        // The "original look" penis copy sits here, keeping the character's OWN shader. Class-level
        // because APPLY (adopt) and REMOVE must agree on the identity - they did not, and the copy leaked.
        private const int   BallsOccluderQueue    = 2501;
        private const int   BallsOccluderQueueUp  = 2480;
        private const int   PenisCarveQueue       = 3501;   // = the OrgInside tag; a COPY inherits it with no record
        private const int   PenisCarveQueueUp     = 2483;
        private const int   OriginalRedrawQueue   = 3502;
        private const int   OriginalRedrawQueueUp = 2484;

        private static bool _tried;
        private static Type _ctrlType;            // KK_Plugins.MaterialEditor.MaterialEditorCharaController
        private static Type _objType;             // nested ObjectType enum
        private static object _otCharacter;       // ObjectType.Character (boxed)
        private static object _otClothing;        // ObjectType.Clothing (boxed; resolved by name scan)
        private static object _otAccessory;       // ObjectType.Accessory (boxed; resolved by name scan)
        private static MethodInfo _mCopyRemove, _mSetShader, _mSetFloat, _mSetQueue;
        private static MethodInfo _mRemoveShader, _mRemoveShaderQueue;   // ME's reset: restores the ORIGINAL shader and deletes the persisted edit
        private static FieldInfo _fCopyList;         // MaterialEditor's own record of the copies it created
        private static FieldInfo _fCopyName, _fCopySource;

        public static bool Available { get { Init(); return _ctrlType != null; } }

        /// <summary>
        /// Ask MaterialEditor to re-apply its BODY edits to this character.
        /// Needed after a forced uncensor swap: UncensorSelector's body reload is a PARTIAL reload and ME
        /// has no handler for it (its re-apply hooks are all KKAPI-level, and the patches that call
        /// RefreshBodyEdits are #if PH), so the character's own skin shader - KKUTS and the like - is lost
        /// with the old mesh. RefreshBodyEdits() is public and body/face only, so it restores their shader
        /// without touching clothes or accessories.
        /// </summary>
        public static void RefreshBodyEdits(Component cc)
        {
            Init();
            if (cc == null || _ctrlType == null) return;   // guard BEFORE GetComponent: a null type throws
            try
            {
                var me = cc.GetComponent(_ctrlType);
                if (me == null) return;                    // no ME data on this character - nothing to restore
                var m = _ctrlType.GetMethod("RefreshBodyEdits", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (m == null)
                {
                    LiquidWobbleMPBPlugin._logger?.LogError("CloXray: MaterialEditor has no RefreshBodyEdits() — cannot restore '" + cc.name
                        + "' body material edits after the uncensor swap. Their skin shader will need re-picking in the MaterialEditor menu.");
                    return;
                }
                m.Invoke(me, null);
                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: MaterialEditor RefreshBodyEdits() on '" + cc.name
                    + "' — re-applying their own body material edits after the uncensor swap.");
            }
            catch (Exception e)
            {
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: RefreshBodyEdits() failed on '" + cc.name + "': " + e.GetType().Name + ": " + e.Message);
            }
        }

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
                // Clothing/Accessory member names vary in capitalization across ME builds - resolve by scan.
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
                // Queue setter (for the veil copy, which must land after the organ). Signature varies across
                // ME versions - bind by name and fill args by parameter type at call time.
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

        // The existing-check for the reveal/veil stamps: the CloXray shader counts as applied ONLY when it sits
        // on a .MECopy. A CloXray shader that ended up on the ORIGINAL body material (a corrupted save)
        // must read as NOT-applied so the stamp path can repair and recreate instead of no-opping.
        // Her body STAMP copy, or null. Its render queue is the marker for which stack she is on -
        // read by SyncWombStack, which has no business reaching into MEBridge's internals for it.
        internal static Material FindBodyStamp(Component cc)
        {
            var bodyR = FindBodyRenderer(cc);
            return bodyR != null ? FindConfiguredCopy(bodyR, BodyRevealShader) : null;
        }
        internal static bool StampIsUpgraded(Material stamp)
        {
            return stamp != null && stamp.renderQueue == BodyStampQueueUp;
        }

        private static Material FindConfiguredCopy(SkinnedMeshRenderer r, string shaderName)
        {
            foreach (var m in r.sharedMaterials)
                if (m != null && m.shader != null && m.shader.name == shaderName && BaseName(m).Contains(".MECopy")) return m;
            return null;
        }

        // MaterialEditor records every copy it creates in its MaterialCopyList as
        // (MaterialName -> MaterialCopyName). Asking that list which copy just appeared is exact, where
        // comparing the renderer's material array before and after is not: ME's copy call reassigns the
        // whole array, so every entry comes back as a fresh instance with a " (Instance)" suffix. That is
        // what once resolved "the new copy" to the ORIGINAL body material and persisted a stencil-writer
        // shader onto it - an invisible body - and what left unconfigured copies behind on a name miss.
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
        // Null on any failure, always with a reason logged - nothing is guessed.
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
                    if (CopyRowName(r, true) != src) continue;   // a copy of a different material
                    made = n;   // last new row for this source wins
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
        // (stencil-writer passes write no color) - the invisible-body state. ME's RemoveMaterialShader
        // restores the shader it recorded as original and deletes the persisted edit, so the fix also
        // sticks in the next save.
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
                    // ME's remove only clears rows under the current coordinate index; the poison was written per
                    // coordinate index; sweeping every index covers any layout MaterialEditor used.
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
        // NON-copy material, on EVERY outfit coordinate. Only ever called for an ORIGINAL body material
        // that was found carrying a CloXray shader - copies are never touched (their MaterialName contains
        // ".MECopy" and never equals the original's name).
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
                        {
                            // A record with NO shader name but one of the CloXray queues is the penis redraw copy -
                            // it keeps its own shader, so the queue is the only thing that marks it.
                            // BOTH stacks: the legacy band and the sub-2500 one the hotkey writes.
                            try
                            {
                                int q = Convert.ToInt32(rq);
                                if ((q >= 3490 && q <= 3505) || (q >= 2478 && q <= 2488)) poison = true;
                            }
                            catch { }
                        }
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

        // One-block state dump of the body renderer, for diagnosing material corruption from the log.
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

        // Unity appends " (Instance)" to a material's name every time something instantiates the
        // renderer's material array (each ME operation does), so runtime names drift to
        // "cf_m_body (Instance) (Instance)..." while ME keeps naming copies from the clean base
        // ("cf_m_body.MECopy1"). Every name comparison in this bridge goes through this strip.
        internal static string BaseName(Material m)
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
        // Skipping configured copies keeps the stamp copy and the veil copy from stealing each
        // other's slot when both exist on the body.


        // ---- MASK / QUEUE STATE ----------------------------------------------------------
        // Two questions in one dump, because both are answered by the same per-material state and the
        // repro run is expensive:
        //
        // (1) VANISHING BODYPARTS. Every CloXray shader is ColorMask 0 - never paints a pixel - so the
        //     cannot repaint a garment or a body. Geometry can therefore only go missing three ways:
        //     the render array (see AuditGeometry), DEPTH written, or the GAME's own clothes alpha mask
        //     that hides the body under a garment so it cannot clip through. The third is not ours to
        //     write, but it IS ours to disturb: an ME material copy carries whatever mask texture was
        //     bound at copy time, and a body rebuild re-composites it. So print the mask textures and
        //     the queue, per material, and the answer stops being a guess.
        //
        // (2) THE HAND/TORSO REGION MASK. It only works if the copy actually carries _RegionMask with a
        //     non-zero cutoff; a null texture or cutoff 0 means masking is silently OFF (by design, so
        //     old scenes keep working) and hands still x-ray through. Printing both proves which.
        static readonly string[] MaskTexProps = {
            "_AlphaMask", "_alpha_a", "_AlphaMask2", "_ClothesMask", "_MaskTex", "_liquidmask",
            "_MainTex", "_overtex1", "_Texture2", "_RegionMask"
        };
        static readonly string[] MaskFloatProps = {
            "_Cutoff", "_RegionMaskCutoff", "_StampZWrite", "_StampZTest", "_AlphaOptionZWrite", "_alpha"
        };

        public static void DumpMaskState(Component cc, string when)
        {
            if (cc == null) return;
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("CloXray MASK STATE ").Append(when).Append(" '").Append(cc.name).Append("'");
                foreach (var r in cc.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || r.sharedMaterials == null || r.sharedMaterials.Length == 0) continue;
                    bool interesting = r.name == "o_body_a" || r.name.StartsWith("o_body") || r.name.Contains("dan")
                                     || r.name.Contains("tama") || r.name.Contains("clothes") || r.name.Contains("cf_") || r.name.Contains("cm_");
                    if (!interesting) continue;
                    sb.Append("\n").Append("  '").Append(r.name).Append("' enabled=").Append(r.enabled)
                      .Append(" active=").Append(r.gameObject.activeInHierarchy);
                    foreach (var m in r.sharedMaterials)
                    {
                        if (m == null) { sb.Append("\n").Append("    (null)"); continue; }
                        sb.Append("\n").Append("    '").Append(m.name).Append("' q=").Append(m.renderQueue)
                          .Append(" sh=").Append(m.shader != null ? m.shader.name : "NULL");
                        // SHADER FINGERPRINT. A copy dumped sh=CloXray/BodyReveal with NONE of
                        // the newer properties, while the only zipmod on disk (and the only manifest
                        // among 7416 that declares CloXray at all) verifiably carries them - so two
                        // Shader objects with the same name must be alive. Print the instance id and a
                        // property fingerprint so the log says which object each material is bound to.
                        if (m.shader != null && m.shader.name.StartsWith("CloXray/"))
                            sb.Append(" shId=").Append(m.shader.GetInstanceID())
                              .Append(" fp[RM=").Append(m.HasProperty("_RegionMask") ? 1 : 0)
                              .Append(",RMC=").Append(m.HasProperty("_RegionMaskCutoff") ? 1 : 0)
                              .Append(",SZW=").Append(m.HasProperty("_StampZWrite") ? 1 : 0)
                              .Append(",SZT=").Append(m.HasProperty("_StampZTest") ? 1 : 0).Append("]");
                        foreach (var pn in MaskTexProps)
                            if (m.HasProperty(pn))
                            { var tx = m.GetTexture(pn); sb.Append(" ").Append(pn).Append("=").Append(tx != null ? tx.name : "NULL"); }
                        foreach (var pn in MaskFloatProps)
                            if (m.HasProperty(pn)) sb.Append(" ").Append(pn).Append("=").Append(m.GetFloat(pn).ToString("F2"));
                    }
                }
                LiquidWobbleMPBPlugin._logger?.LogInfo(sb.ToString());
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: mask dump failed: " + e.Message); }
        }

        // ---- GEOMETRY AUDIT --------------------------------------------------------------
        // "The character loses bodyparts" is a RENDER-ARRAY fault, not a shader one, and Unity gives it
        // exactly three causes. Nothing in the existing dumps tested any of them, so every log so far
        // read was blind to the actual failure:
        //   1. sharedMaterials.Length < subMeshCount -> the trailing submeshes are NEVER DRAWN. This is
        //      the one this code can cause: MaterialCopyRemove is a TOGGLE, and handing it the wrong
        //      material removes a slot instead of adding one. Losing a slot silently deletes geometry.
        //   2. a null entry in sharedMaterials -> that submesh is not drawn.
        //   3. renderer disabled / object inactive.
        // Anomalies only, so the log stays readable on a healthy character; the summary line proves the
        // audit ran even when it finds nothing (a silent audit is indistinguishable from a skipped one).
        public static void AuditGeometry(Component cc, string when)
        {
            if (cc == null) return;
            try
            {
                int checkedR = 0, bad = 0;
                var sb = new System.Text.StringBuilder();
                foreach (var r in cc.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null) continue;
                    // The game's touch-collision meshes (o_hit_* and *_atari, created when H starts)
                    // ship with ZERO materials by design - they are never rendered. Auditing them
                    // produced 22-23 false STARVED errors per character on an otherwise healthy body.
                    if (r.name.StartsWith("o_hit_") || r.name.EndsWith("_atari")) continue;
                    checkedR++;
                    var mats = r.sharedMaterials;
                    if (mats == null) { bad++; sb.Append("\n'").Append(r.name).Append("': sharedMaterials is NULL"); continue; }
                    var smr = r as SkinnedMeshRenderer;
                    var mesh = smr != null ? smr.sharedMesh : null;
                    var mf = mesh == null ? r.GetComponent<MeshFilter>() : null;
                    if (mesh == null && mf != null) mesh = mf.sharedMesh;
                    int subs = mesh != null ? mesh.subMeshCount : -1;

                    int nulls = 0;
                    for (int i = 0; i < mats.Length; i++) if (mats[i] == null) nulls++;

                    // Cause 4: a CloXray copy PAIRED with a submesh. These stamps are ColorMask 0
                    // - invisible by design - so if ME's array rebuild ever leaves one at an index
                    // below subMeshCount, that submesh renders ONLY the invisible stamp and the
                    // garment part visually disappears while the material COUNT stays healthy. The
                    // audit was blind to this ("all intact" through every report of vanishing
                    // clothes); a copy is only harmless in the extra re-draw slots at the END.
                    int pairedCopy = -1;
                    if (subs > 0)
                        for (int i = 0; i < mats.Length && i < subs; i++)
                            if (mats[i] != null && mats[i].shader != null
                                && mats[i].shader.name.StartsWith("CloXray/") && mats[i].name.Contains(".MECopy"))
                            { pairedCopy = i; break; }

                    bool starved = subs > 0 && mats.Length < subs;      // cause 1 - geometry deleted
                    if (!starved && nulls == 0 && pairedCopy < 0) continue;   // healthy - stay quiet

                    bad++;
                    sb.Append("\n'").Append(r.name).Append("': materials=").Append(mats.Length)
                      .Append(" subMeshCount=").Append(subs).Append(" nullSlots=").Append(nulls)
                      .Append(starved ? "  <== STARVED: submesh " + mats.Length + ".." + (subs - 1) + " CANNOT DRAW"
                            : pairedCopy >= 0 ? "  <== INVISIBLE PAIRING: submesh " + pairedCopy + " is drawn ONLY by the ColorMask-0 stamp '" + mats[pairedCopy].name + "' - that garment part has visually disappeared"
                            : "  <== NULL SLOT: that submesh cannot draw")
                      .Append(" enabled=").Append(r.enabled);
                    for (int i = 0; i < mats.Length; i++)
                        sb.Append("\n[").Append(i).Append("] ").Append(mats[i] == null ? "(null)" : mats[i].name);
                }
                if (bad > 0)
                    LiquidWobbleMPBPlugin._logger?.LogError("CloXray GEOMETRY AUDIT " + when + " '" + cc.name + "': "
                        + bad + " of " + checkedR + " renderer(s) CANNOT DRAW ALL THEIR GEOMETRY -" + sb);
                else if (AutoBodyReveal.Debug)
                    LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray geometry audit " + when + " '" + cc.name + "': "
                        + checkedR + " renderer(s), all intact.");
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: geometry audit failed: " + e.Message); }
        }

        /// <summary>The stencil ref actually live on her BodyReveal copy, or -1. This is the half of a
        /// pair that MaterialEditor persists, so after a load it says which pair the scene was built
        /// on.</summary>
        internal static int ReadBodyStencilRef(Component cc)
        {
            if (cc == null) return -1;
            foreach (var r in cc.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || r.sharedMaterials == null) continue;
                foreach (var m in r.sharedMaterials)
                    if (m != null && m.shader != null && m.shader.name == BodyRevealShader && m.HasProperty("_StencilRef"))
                        return Mathf.RoundToInt(m.GetFloat("_StencilRef"));
            }
            return -1;
        }

        // Hand MaterialEditor back every CloXray copy on this character. MaterialCopyRemove is a
        // TOGGLE keyed on the SOURCE material, so one call per copy; each is verified against the
        // MaterialCopyList so a silent no-op cannot leave a half-stripped character behind.
        internal static int RemoveXrayCopies(Component cc, bool debug)
        {
            Init();   // same lazy resolve the Ensure* apply paths do - without it _ctrlType is null here
            // _ctrlType null -> GetComponent((Type)null) THROWS, and this runs inside the toggle-off of
            // the nudge-bake respawn coroutine: the throw killed the coroutine between its remove and its
            // respawn, so the womb vanished and the next hotkey press "spawned it again" (the KKS
            // press-twice report). Cleanup never gets to abort the toggle.
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
            // CLOTHES FIRST. ME addresses a garment by its slot index + the Clothing object type; giving
            // it the Character type (as the body/penis pass does) makes it look in the wrong list and
            // drop nothing. Doing these first also leaves only body/penis copies for the pass below.
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
            // ACCESSORIES NEXT - the missing half of EnsureAccessoryReveal, found via the geometry
            // audit + copy-row errors in a live log. Accessory garments are stamped with the Accessory
            // object type and their SLOT index; without this pass their copies fell through to the
            // character-wide sweep below, which hands ME the Character type - the wrong list - so ME
            // never dropped the row and the garment stayed x-rayed after toggle-off. Through a stuck
            // see-through garment the body is alpha-masked away by the game, which on screen reads as
            // "the character lost bodyparts" / transparent skin - the long-standing report.
            // Sweep ALL slots including inactive ones (true): a stamped accessory can be toggled off
            // between apply and removal, and its copy must still come off.
            if (_otAccessory != null)
            {
                const BindingFlags ANYA = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                GameObject[] accs = null;
                var fA2 = cc.GetType().GetField("objAccessory", ANYA);
                if (fA2 != null) accs = fA2.GetValue(cc) as GameObject[];
                if (accs == null)
                {
                    var pA2 = cc.GetType().GetProperty("objAccessory", ANYA);
                    if (pA2 != null) accs = pA2.GetValue(cc, null) as GameObject[];
                }
                if (accs != null)
                    for (int slot = 0; slot < accs.Length; slot++)
                    {
                        if (accs[slot] == null) continue;
                        foreach (var r in accs[slot].GetComponentsInChildren<Renderer>(true))
                            removed += RemoveCopiesOn(me, r, slot, _otAccessory, accs[slot], cc.name);
                    }
            }
            foreach (var r in cc.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                removed += RemoveCopiesOn(me, r, 0, _otCharacter, GetBodyGo(cc) ?? (r != null ? r.gameObject : null), cc.name);
            }
            if (debug || removed > 0)
                LiquidWobbleMPBPlugin._logger?.LogInfo("MEBridge: removed " + removed + " x-ray copy/copies from '" + cc.name + "'.");
                // DELIBERATELY NOT ForgetPenisLook(). The snapshot is already per-male and
                // first-write-wins (see CapturePenisLook), so it self-invalidates when the male changes.
                // Dropping it on every removal meant a toggle OFF then ON re-captured - and a reporter's
                // sequence is exactly "change pose, x-ray off, x-ray on", by which point KPlug may have
                // already rewritten the materials. That would snapshot the BROKEN state and restore it
                // faithfully from then on, which is why the fault looked random: it is a race between this
                // re-capture and KPlug's rewrite. Keeping the first good capture removes the race.
                // With the CloXray copies gone the renderer is back to what KPlug/the uncensor own. Dumping here
                // is the only way to see that state - every other dump fires while a womb exists.
                if (AutoBodyReveal.Debug && cc != null) DumpXrayChain(cc, null, null);
            AuditGeometry(cc, "AFTER REMOVE");
            if (AutoBodyReveal.Debug) DumpMaskState(cc, "AFTER REMOVE");
            return removed;
        }

        // Hand ME the COPY itself: MaterialCopyRemove branches on the name it is given - a ".MECopy"
        // removes, an ORIGINAL creates another copy. Snapshot first: removing rewrites sharedMaterials.
        private static int RemoveCopiesOn(object me, Renderer r, int slot, object objType, GameObject go, string who)
        {
            if (r == null || r.sharedMaterials == null || go == null) return 0;
            var copies = new System.Collections.Generic.List<Material>();
            foreach (var m in r.sharedMaterials)
            {
                if (m == null || m.shader == null || !BaseName(m).Contains(".MECopy")) continue;
                if (m.shader.name.StartsWith("CloXray/")) { copies.Add(m); continue; }
                // THE ORIGINAL-LOOK COPY. It carries the character's OWN shader by design - that is the
                // whole point of it, re-drawing the penis through the window with his real shading - so a
                // "shader starts with CloXray/" test can never see it, and it survived every removal. It
                // then leaks: the male keeps a second draw of his penis at the redraw queue after the womb is gone,
                // and the next apply allocates a fresh index (MECopy1 -> MECopy3) so the layout no longer
                // matches a clean apply. Two draws of a reflective skin shader over one mesh read as a
                // blown-out white penis, which is the reported symptom.
                //
                // Identity is the one the APPLY path already claims as ours (see the copy2 adopt block):
                // a .MECopy on the penis material at exactly the redraw queue. Symmetric by construction -
                // if apply would adopt it, removal must drop it.
                // Compare against the SOURCE name, not the copy's. BaseName gives "cm_m_dankon.MECopy2"
                // and IsPenisMat demands exact equality with "cm_m_dankon", so an earlier form of this
                // test could never be true and the copy went on leaking - visible in a reporter's log as
                // MECopy2 surviving every removal while the carve came back as MECopy3.
                string bn = BaseName(m);
                int mc = bn.IndexOf(".MECopy", StringComparison.Ordinal);
                string bnBase = mc > 0 ? bn.Substring(0, mc) : bn;
                if ((IsPenisMat(bnBase) || bnBase == BallsMat) && m.renderQueue == OriginalRedrawQueue)
                {
                    LiquidWobbleMPBPlugin._logger?.LogInfo("MEBridge: removing the original-look copy '" + m.name
                        + "' (" + m.shader.name + " @" + m.renderQueue + ") on '" + who + "' — it carries the character's own shader, not ours.");
                    copies.Add(m);
                }
            }
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

        /// <summary>
        /// Idempotently ensure the body has a BodyReveal copy at the given stencil. cc = the ChaControl
        /// (treated as a UnityEngine.Component). Logs loudly; no-ops if ME is absent.
        /// </summary>
        // True once this body's BodyReveal MECopy is present - i.e. MaterialEditor has restored (or the hotkey
        // created) it. The deferred on-load coroutine polls this: once true the saved copy is ADOPTED (never
        // re-stamped); if it never becomes true within the cap, the character is genuinely fresh -> create.
        public static bool MERestoredFor(Component cc)
        {
            if (cc == null) return false;
            try { var bodyR = FindBodyRenderer(cc); return bodyR != null && FindByShader(bodyR, BodyRevealShader) != null; }
            catch { return false; }
        }

        // KKUTS_Tess-family shaders tessellate and DISPLACE the surface in front of the flat stamp
        // copy, so the stamp's LEqual depth test fails up close and the reveal window never opens
        // (visible only zoomed out, where the distance-adaptive tessellation collapses back to the
        // base mesh - GitHub issue #3). A camera-ward pull of a few mm on the stamp restores it
        // while keeping depth occlusion by props and hands. Applied ONLY when the SOURCE material's
        // shader is a tess variant; the shader default is 0, so every other install keeps the exact
        // legacy stamp.
        private const float TessStampPull = 0.010f;   // metres - covers Phong + heightmap displacement
        private static bool IsTessShader(Material m)
        {
            return m != null && m.shader != null
                && m.shader.name.IndexOf("tess", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        // Sets _StampPull on a stamp copy to match its SOURCE material. Loud when the installed
        // shader zipmod predates the property - silently skipping would reproduce the exact
        // invisible-window this exists to fix.
        private static void ApplyStampPull(object me, int slot, object objType, Material stampCopy, Material source, GameObject go, string who)
        {
            if (stampCopy == null) return;
            float want = IsTessShader(source) ? TessStampPull : 0f;
            if (!stampCopy.HasProperty("_StampPull"))
            {
                if (want > 0f)
                    LiquidWobbleMPBPlugin._logger?.LogError("CloXray: '" + who + "' uses a tess shader ('" + source.shader.name
                        + "') but the installed CloXray/BodyReveal has no _StampPull - update [Clo]XrayShaders.zipmod; the x-ray window will keep failing up close until then.");
                return;
            }
            if (Mathf.Approximately(stampCopy.GetFloat("_StampPull"), want)) return;
            _mSetFloat.Invoke(me, new object[] { slot, objType, stampCopy, "StampPull", want, go, true });
            if (want > 0f)
                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: '" + who + "' uses tess shader '" + source.shader.name
                    + "' - " + (TessStampPull * 1000f).ToString("F0") + "mm stamp pull applied so the window survives the displaced skin.");
        }

        public static bool EnsureBodyReveal(Component cc, int stencil, bool debug, bool overwriteExisting)
        {
            Init();
            if (cc == null) return false;
            // The reveal/veil/clothes stamps dress the womb's WEARER. A male near the womb must never
            // receive them: his body material (cm_m_body) was copied by the source-pick fallback
            // and then missed by the old cf_m_body-only copy finder, leaking an orphan .MECopy into the
            // scene on every pass (reported: cm_m_body.MECopy12..16 after a few sessions).
            if (MainGameWomb.IsMaleChara(cc)) { if (debug) LiquidWobbleMPBPlugin._logger?.LogInfo("MEBridge: '" + cc.name + "' is male - the body reveal stamp only applies to the wearer."); return false; }
            if (_ctrlType == null) { if (debug) LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: ME unavailable; cannot auto-apply BodyReveal."); return false; }

            try
            {
                var me = cc.GetComponent(_ctrlType);
                if (me == null) { if (debug) LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: no MaterialEditor controller on '" + cc.name + "'."); return false; }

                DumpMeShaderRegistry();   // one-shot: what BodyReveal object does ME actually hold?
                var bodyR = FindBodyRenderer(cc);
                if (bodyR == null) { if (debug) LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: body renderer (o_body*) not found on '" + cc.name + "'."); return false; }

                // GameObject ME expects for body edits = ChaControl.objBody (field OR property,
                // varies by game build); fall back to the renderer's GO (ME accepts it).
                GameObject go = GetBodyGo(cc) ?? bodyR.gameObject;

                // Already applied? On the HOTKEY (overwriteExisting) keep the stencil synced to the womb. On the
                // on-LOAD re-apply (overwriteExisting=false) LEAVE it: the saved copy is authoritative, and the womb's
                // _StencilBody may not be MaterialEditor-restored yet - reading it early on a non-default pair would
                // stamp the stale default 4 over a saved 8 (the load-reset bug). Mirrors how the veil already
                // protects its XrayAlpha slider from a re-press.
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
                    // The torso mask shipped AFTER many copies were created and persisted, and this
                    // branch used to return without ever touching it - so an adopted pre-mask copy kept
                    // stamping her hands forever - bare hands must not block the womb, confirmed by live test.
                    // The mask belongs to CloXray, not to a hand edit: assert it whenever it is absent.
                    QueueRegionMask(me, existing, go, cc.name, debug);
                    // Same reasoning as the mask, and as the veil's own queue check: the stamp copy is
                    // OURS. A scene saved before the stack moved below 2500 pins its queue at the old
                    // 2500, which would draw the body stencil AFTER the organs that test it - the window
                    // simply would not open. Assert it; a queue the USER set on a material of their own
                    // is never touched by this.
                    EnsureStampQueue(me, existing, go, cc.name);
                    // F1 master: Free-H has no ME, so the cutoff follows the toggle. 0 = mask
                    // off = the womb shows through hands/limbs; 0.5 = the shipped default behavior.
                    _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, existing, "RegionMaskCutoff", LiquidWobbleMPBPlugin.CfgLimbMask ? 0.5f : 0f, go, true });
                    // the BODY stamp must not respect the limb block (gate 0) - plain depth
                    // already stops it behind a limb, and gating it only fattened the margins by the
                    // inflate distance. Garment copies keep the shader default (128).
                    ApplyStampPull(me, 0, _otCharacter, existing,
                        FindByName(bodyR, "cf_m_body") ?? (bodyR.sharedMaterials.Length > 0 ? bodyR.sharedMaterials[0] : null), go, cc.name);
                    return true;
                }

                // Find or create the copy slot (MaterialCopyRemove is a toggle -> only call it to CREATE).
                // The copy is matched by the SOURCE material's real name - a custom body may not call it
                // cf_m_body, and a name mismatch here is what orphaned copies instead of configuring them.
                var srcBody = FindByName(bodyR, "cf_m_body") ?? (bodyR.sharedMaterials.Length > 0 ? bodyR.sharedMaterials[0] : null);
                if (srcBody == null) { if (debug) LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: no body material on '" + cc.name + "'."); return false; }
                var copy = FindCopy(bodyR, BaseName(srcBody)) ?? CreateCopyTracked(me, bodyR, srcBody, go, "body reveal");
                if (copy == null) return false;

                _mSetShader.Invoke(me, new object[] { 0, _otCharacter, copy, BodyRevealShader, go, true });
                DirectSetShader(copy, go, BodyRevealShader);   // the flip happens NOW; the ME call above is persistence only
                _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, copy, "StencilRef", (float)stencil, go, true });
                QueueRegionMask(me, copy, go, cc.name, debug);
                _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, copy, "RegionMaskCutoff", LiquidWobbleMPBPlugin.CfgLimbMask ? 0.5f : 0f, go, true });   // F1 master, see the adopted path
                EnsureStampQueue(me, copy, go, cc.name);
                ApplyStampPull(me, 0, _otCharacter, copy, srcBody, go, cc.name);
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
        // ---- DAN_F SPLIT -----------------------------------------------------------------
        // o_dan_f is ONE mesh with two roles, split by its own bones (cm_J_dan_f_top = the covering
        // skin at the shaft junction; cm_J_dan_f_L/_R = the sack lobes) and baked offline into two
        // UV masks (tools/make_danf_mask.py, shipped beside the DLL):
        //   OCCLUDER (stamp, kept): masked to the SACK - the lobes block the x-ray window like
        //     a hand wherever they are frontmost, and never draw through anything;
        //   CONTENT (new): a carve+redraw pair like the shaft's, masked to the TOP skin - so the
        //     junction region renders in-window with the penis, which removes the white blob that
        //     every removal of dan_f content has re-exposed (it was dan_f's own zone,
        //     bare, under the veil).
        // ---- (OCCLUDER value; the organ value was incorrect) ------------------------------------
        // The balls (cm_m_dan_f) stamp an INERT region value (2) that NO pass consumes - not the
        // shaft's window punch (4), not the organ pipeline (5), not the veil (>=5). An earlier value used the
        // organ value on the theory that only the WOMB chain would accept it; on screen the PENIS
        // carve's own DepthClear keys on the same 5, so the shaft punched through the balls even in
        // open air (screenshots, view from behind). With 2 the balls become a pure OCCLUDER of
        // the x-ray window, like a bare hand:
        //   - in AIR: nothing consumes 2, the redraw fails against the balls' own depth -> plain;
        //   - at the penetration overlap: no window on balls-front pixels -> the real balls show,
        //     and the white is impossible (white always required a contentless WINDOW pixel);
        //   - behind her body/clothes: the stamp loses the depth test to whatever is in front, so
        //     her stamps rule and the balls never draw on top of anything.
        // No planes, no feeds, no state gating.
        private const float BallsOccluderRef = 2f;   // low-5 region value consumed by no pass (0=outside, 4=body, 5=organ)

        internal static bool EnsureBallsStamp(Component cc, int stencil, bool debug)
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
                if (src == null) { if (debug) LiquidWobbleMPBPlugin._logger?.LogInfo("MEBridge: no '" + BallsMat + "' on '" + cc.name + "' - balls stamp skipped."); return false; }

                GameObject go = GetBodyGo(cc) ?? ballsR.gameObject;
                // Classify existing dan_f copies by role; anything unconfigured (source shader at the
                // source queue - includes legacy junction/occluder-era leftovers after conversion) is
                // claimed for the next missing role. Old scenes self-heal on sight.
                Material occ = null, carve = null, redraw = null;
                var spare = new System.Collections.Generic.List<Material>();
                foreach (var m in ballsR.sharedMaterials)
                {
                    if (m == null || !m.name.Contains(".MECopy") || !BaseName(m).StartsWith(BallsMat)) continue;
                    if (m.shader != null && m.shader.name == BodyRevealShader) { if (occ == null) occ = m; }
                    else if (m.shader != null && m.shader.name == OrgInsideShader) { if (carve == null) carve = m; }
                    else if (m.renderQueue == OriginalRedrawQueue) { if (redraw == null) redraw = m; }
                    else spare.Add(m);
                }
                System.Func<string, Material> take = role =>
                {
                    if (spare.Count > 0) { var s = spare[0]; spare.RemoveAt(0); return s; }
                    return CreateCopyTracked(me, ballsR, src, go, role);
                };

                // dan_f is OCCLUDER-ONLY. The carve and redraw existed to paint the "covering
                // skin" band in-window like the penis - but any in-window dan_f content draws THROUGH
                // clothes/body whenever the window overlaps the balls on screen (the window IS what
                // you see there). Every leak variant of the saga (white, tinted, real-look) was one
                // of those two painters under different conditions; the "confirmed good" scene
                // simply never overlapped window and balls. So: no in-window content, ever. The whole
                // mesh blocks the window like a hand (cutoff 0 disables the region mask entirely -
                // nothing texture-shaped left for ME replay to lose), and the base material owns
                // every pixel the balls legitimately show.
                if (occ == null) occ = take("dan_f occluder");
                if (occ != null)
                {
                    _mSetShader.Invoke(me, new object[] { 0, _otCharacter, occ, BodyRevealShader, go, true });
                    DirectSetShader(occ, go, BodyRevealShader);   // the flip happens NOW; the ME call above is persistence only
                    _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, occ, "StencilRef", BallsOccluderRef, go, true });
                    _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, occ, "StampZWrite", 0f, go, true });
                    _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, occ, "RegionMaskCutoff", 0f, go, true });   // whole mesh occludes (shader default is 0.5!)
                    SetQueuePersisted(me, 0, _otCharacter, occ, go, Q(BallsOccluderQueue, BallsOccluderQueueUp));
                }

                // Both in-window painters are handed back to ME on sight - old scenes self-heal.
                if (carve != null)
                {
                    try { _mCopyRemove.Invoke(me, new object[] { 0, _otCharacter, carve, go }); } catch { }
                    LiquidWobbleMPBPlugin._logger?.LogInfo("MEBridge: removed the retired dan_f carve copy '" + carve.name + "' on '" + cc.name + "'.");
                }
                if (redraw != null)
                {
                    try { _mCopyRemove.Invoke(me, new object[] { 0, _otCharacter, redraw, go }); } catch { }
                    LiquidWobbleMPBPlugin._logger?.LogInfo("MEBridge: removed the retired dan_f redraw copy '" + redraw.name + "' on '" + cc.name + "'.");
                }

                LiquidWobbleMPBPlugin._logger?.LogInfo("MEBridge: dan_f OCCLUDER-ONLY on '" + cc.name + "' - "
                    + (occ != null ? occ.name : "MISSING")
                    + " (whole mesh blocks the window like a hand; no in-window balls content by design).");
                return occ != null;
            }
            catch (System.Exception e)
            {
                LiquidWobbleMPBPlugin._logger?.LogError("MEBridge: balls stamp failed on '" + (cc ? cc.name : "?") + "': " + e.Message);
                return false;
            }
        }

        public static bool EnsurePenisOrgInside(Component cc, int stencil, bool debug)
        {
            return EnsurePenisOrgInside(cc, stencil, debug, 1f, -1f);   // Studio defaults: outside visible, BottomWindow untouched (ME owns it there)
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

                // LEGACY UPGRADE. Early builds converted the MAIN penis material in place;
                // scenes saved then still replay that record, so the penis arrives with ONE OrgInside
                // material instead of carve + original-look redraw, and the outside-body half is painted
                // by OrgInside itself (which is what leaked background through its out-of-body clears).
                // Only the one-key hotkey does this: pressing it means "set this up properly", while
                // every other caller leaves a deliberate in-place conversion alone. ME recorded the
                // original shader as ShaderNameOriginal, so RemoveMaterialShader hands it straight
                // back - nothing is guessed. NO FALLBACK: if the ME method is missing, say so and
                // keep the old layout rather than half-upgrading.
                if (already && AutoBodyReveal.PenisTakeover)
                {
                    if (_mRemoveShader == null || _mRemoveShaderQueue == null)
                        LiquidWobbleMPBPlugin._logger?.LogError("MEBridge: '" + cc.name + "' has the OLD in-place penis conversion and this ME has no RemoveMaterialShader - leaving it as one material. Fix the ME bridge to upgrade old scenes.");
                    else
                    {
                        _mRemoveShaderQueue.Invoke(me, new object[] { 0, _otCharacter, dankon, go, true });
                        _mRemoveShader.Invoke(me, new object[] { 0, _otCharacter, dankon, go, true });
                        already = dankon.shader != null && dankon.shader.name == OrgInsideShader;
                        LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: '" + cc.name + "' carried the OLD in-place penis conversion -> restored '"
                            + (dankon.shader != null ? dankon.shader.name : "?") + "' and rebuilding it as carve + redraw copies."
                            + (already ? "  STILL OrgInside - the record did not clear." : ""));
                        AutoBodyReveal.PenisUpgraded = !already;
                    }
                }

                // COPY MODE (the behavior): the card's penis shader (KKUTS etc.) stays on the
                // ORIGINAL material - the x-ray lives on an ADDED ME copy: OrgInside +
                // OutsideOfBodyAlpha=0 renders ONLY the in-body part through the womb window, while the
                // original keeps rendering the outside part with its own effects (inside it is
                // depth-hidden by the body as always, so the two never double-draw). Hand-made copies
                // are NEVER touched: adopt only a copy already carrying OrgInside, or configure the
                // one appended here (snapshot-diff, like the clothes path). NO FALLBACK (hard rule): if the
                // copy can't be created, fail LOUD and apply nothing - the main material is never
                // converted. A main already converted by an OLDER build/scene takes the tail path below
                // (floats kept in sync; no second OrgInside stacked on top).
                if (!already)
                {
                    if (_mCopyRemove == null)
                    {
                        LiquidWobbleMPBPlugin._logger?.LogError("MEBridge: penis x-ray NOT applied on '" + cc.name + "' — ME MaterialCopyRemove unavailable (ME version?). Fix the ME bridge; the penis material is never converted in place.");
                        return false;
                    }
                    Material copy = null;
                    foreach (var m in penisR.sharedMaterials)
                        // NOT a copy sitting on a redraw queue: an old scene can leave OrgInside on
                        // MECopy2 as well, and taking the first match made the REDRAW the carve - both
                        // copies then landed on the carve's queue and the redraw lost its slot.
                        if (m != null && m.shader != null && m.shader.name == OrgInsideShader && m.name.Contains(".MECopy")
                            && !QueueOk(m.renderQueue, OriginalRedrawQueue, OriginalRedrawQueueUp)) { copy = m; break; }
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
                        DirectSetShader(copy, go, OrgInsideShader);   // the flip happens NOW; the ME call above is persistence only
                    }
                    // The carve normally has NO queue record - it just follows CloXray/OrgInside's tag. An
                    // upgrade has to state it, or the carve stays at the legacy 3501 while everything
                    // around it moves below 2500.
                    if (UpgradeStack && copy.renderQueue != PenisCarveQueueUp)
                        SetQueuePersisted(me, copy, go, PenisCarveQueueUp);
                    _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, copy, "StencilBody",        (float)stencil,       go, true });
                    _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, copy, "StencilBody_Plus_1", (float)(stencil + 1), go, true });
                    _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, copy, "OutsideOfBodyAlpha", 0f,                   go, true });   // inside-only: the ORIGINAL owns the outside look
                    // Shield (shader v389): the carve writes NEAR depth over the outside-body penis
                    // silhouette so copy2's re-draw is depth-rejected there - outside the body the
                    // look stays 100% the original material's own painting (no double outlines/rim).
                    _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, copy, "OutsideShieldDepth", 1f,                   go, true });
                    // skin textures alpha-fade at the BASE SEAM (to blend into the torso). The
                    // carve blends mainTex.a - at those texels it painted nothing and the veil shone
                    // through the punch as the white base sliver. In-window the torso is not painted,
                    // so the carve must be opaque; the womb materials never set this and keep their
                    // translucency.
                    _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, copy, "OpaqueMainTex",      1f,                   go, true });
                    if (bottomWindow >= 0f)
                        _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, copy, "BottomWindow",   bottomWindow,         go, true });

                    // SECOND copy = the ORIGINAL LOOK re-drawn through the window: the card's
                    // shader stays UNTOUCHED on it, only the render queue is raised to 3502 - after
                    // copy1's in-window depth carve (OrgInside, 3501), in the veil's queue (transparent
                    // queues sort back-to-front, so the nearer skin veil still tints over the deeper
                    // penis). In the window the penis now renders with the ORIGINAL shading (KKUTS
                    // effects intact, painting over copy1's OrgInside color at equal depth); outside
                    // the body it re-draws the identical look over the original (benign); inside the
                    // body but outside the window the body depth still occludes it. Identity: OURS = a
                    // .MECopy with a NON-CloXray shader at exactly queue 3502 (hand-made copies keep their
                    // own queue); otherwise created via snapshot-diff, like copy1.
                    Material copy2 = null;
                    foreach (var m in penisR.sharedMaterials)
                        if (m != null && !ReferenceEquals(m, copy) && m.name.Contains(".MECopy") &&
                            (m.shader == null || !m.shader.name.StartsWith("CloXray/")) &&
                            QueueOk(m.renderQueue, OriginalRedrawQueue, OriginalRedrawQueueUp)) { copy2 = m; break; }
                    // Say it out loud if the damage is already done. Two OrgInside copies on one penis
                    // means an older build made the REDRAW a second carve; which one is which can no
                    // longer be told apart from here, and guessing would throw away whichever one was
                    // actually wants. Name both and let them delete one in Material Editor.
                    {
                        int orgCopies = 0; string names = "";
                        foreach (var m in penisR.sharedMaterials)
                            if (m != null && m.shader != null && m.shader.name == OrgInsideShader && m.name.Contains(".MECopy"))
                            { orgCopies++; names += (names.Length > 0 ? " + " : "") + m.name + "@" + m.renderQueue; }
                        if (orgCopies > 1)
                            LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: '" + cc.name + "' has " + orgCopies
                                + " OrgInside penis copies (" + names + "). An older build turned the redraw into a second carve."
                                + " Delete the one you do not want in Material Editor - this cannot be guessed safely.");
                    }
                    // Adopted from a scene on the OTHER stack: move it, do not leave it behind and do not
                    // make a third copy. Keying the search on one exact queue is what produced MECopy3.
                    if (copy2 != null && UpgradeStack && copy2.renderQueue != OriginalRedrawQueueUp)
                        SetQueuePersisted(me, copy2, go, OriginalRedrawQueueUp);
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
                        SetQueuePersisted(me, copy2, go, Q(OriginalRedrawQueue, OriginalRedrawQueueUp));
                    }
                    // BP SoS + KKUTS clip the GLANS at the stock _Cutoff 0.5 - the glans texels sit
                    // under it. In AIR that is a small hole in the original material; in-window the
                    // original-look redraw clips the same texels, nothing repaints the punched depth and
                    // the veil shines bright white through the hole (the "white cap" at the tip - proven
                    // by the balls-removed isolation + _Cutoff=0.50 in the material dump). The
                    // documented fix is Cutoff=0 on skin (the old manual ME edit) - enforced here on the
                    // original AND the redraw clone. ME float records persist and replay; the OrgInside
                    // carve ignores the body cutoff and needs nothing.
                    if (dankon.HasProperty("_Cutoff") && dankon.GetFloat("_Cutoff") > 0f)
                    {
                        LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: penis _Cutoff was "
                            + dankon.GetFloat("_Cutoff").ToString("F2") + " on '" + cc.name
                            + "' -> forced 0 (SoS/KKUTS glans clip - the white tip hole).");
                        _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, dankon, "Cutoff", 0f, go, true });
                    }
                    if (copy2.HasProperty("_Cutoff") && copy2.GetFloat("_Cutoff") > 0f)
                        _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, copy2, "Cutoff", 0f, go, true });
                    LiquidWobbleMPBPlugin._logger?.LogInfo("MEBridge: penis x-ray COPIES on '" + cc.name + "': carve '" + copy.name + "' -> " + OrgInsideShader + " @" + copy.renderQueue + " (stencil " + stencil + "/" + (stencil + 1) + ") + original-look '" + copy2.name + "' @" + copy2.renderQueue + " ('" + dankon.name + "' untouched; in-window look = the original shader).");
                    // INHERITANCE PROBE. CloXray/OrgInside multiplies its lighting by `_RampG`, which
                    // it SAMPLES but does not DECLARE - so the value comes from whatever material was
                    // copied. Her body and the womb carry one; a penis material may not, and an absent
                    // ramp samples BLACK, which is how the garment ghost turned black. The carve is
                    // normally hidden under the redraw, so the only place it would show is the base
                    // seam where the redraw's own alpha fades.
                    var rampTex = copy.HasProperty("_RampG") ? copy.GetTexture("_RampG") : null;
                    if (rampTex == null)
                        LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: penis carve '" + copy.name
                            + "' has NO _RampG - its lighting samples black. Invisible under the redraw, but the base seam can read dark.");
                    else if (debug)
                        LiquidWobbleMPBPlugin._logger?.LogInfo("MEBridge: penis carve ramp OK ('" + rampTex.name + "').");
                    return true;
                }
                // Stencil pair MUST match the FEMALE body the penis is seen through (the same value EnsureBodyReveal
                // stamps on her body via womb.OrganStencil()). Without this the OrgInside depth-clear only fires for a
                // default pair-A womb (4/5); on any other pair (8/9,12/13,16/17) the penis stays hidden behind skin.
                // OutsideOfBodyAlpha=1 keeps the outside-body part visible (already the OrgInside default; re-asserted
                // in case a card lowered it).
                _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, dankon, "StencilBody",        (float)stencil,       go, true });
                _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, dankon, "StencilBody_Plus_1", (float)(stencil + 1), go, true });
                _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, dankon, "OutsideOfBodyAlpha", outsideAlpha,        go, true });
                _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, dankon, "OpaqueMainTex",      1f,                  go, true });   // base-seam alpha fade must not blend away in-window
                // IN-PLACE conversion (the main game's path): this is the ORIGINAL material, not a copy, so
                // nothing has ever written its queue - it just takes the OrgInside tag, which is the LEGACY
                // 3501. Every OTHER layer gets an explicit queue, so on the upgraded stack the womb dropped
                // to 2482-2485 and the penis stayed behind at 3501, drawing after the whole stack instead of
                // inside it - which is the penis showing through his own body in Free-H. State it here too.
                // Only when it differs, so the legacy path never gains a gratuitous record on his card.
                int wantPenisQ = Q(PenisCarveQueue, PenisCarveQueueUp);
                if (dankon.renderQueue != wantPenisQ) SetQueuePersisted(me, dankon, go, wantPenisQ);
                if (bottomWindow >= 0f)   // <0 = leave as-is (Studio: ME owns the slider)
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

        // XRAY-CHAIN dump (KKS penis-invisible-in-womb research): one line per material for the
        // male penis renderer, the female BODY renderer and the womb SMR - name, shader, renderQueue
        // and the stencil/alpha floats the window chain depends on. Diffing a KK dump against a KKS
        // dump pinpoints which link lands differently (queue not applied, float missing on the copy,
        // different original shader/queue...). Debug-gated, called once per penis x-ray apply.
        // ONE-SHOT PER CHARACTER SET. This fires on every penis x-ray apply and runs to ~296 lines
        // a session - it was added to find the real balls material name for the white-balls bug, which is
        // solved. Repeating it now just buries the lines actually being read in a bug-report log.
        private static readonly System.Collections.Generic.HashSet<string> _chainDumped = new System.Collections.Generic.HashSet<string>();


        // THE RULE: every stencil stamp draws before every organ. A stamp marks the window; an organ
        // tests that mark. A stamp scheduled after the organ it opens marks a window nothing will read
        // again this frame, and the organ is simply absent - no error, no warning, just missing pixels.
        //
        // Both shipped stacks satisfy it (legacy stamps 2499/2500/2501 vs organs 3500-3503; upgraded
        // 2478/2479/2480 vs 2482-2485). What broke Free-H was neither stack but a MIX: her body and the
        // womb upgraded while the garments were rewritten back to legacy, putting the garment cut at
        // 2499 behind the womb organ at 2482. Cheap to state, invisible to the eye, so state it.
        private static float _stackAssertNext;
        public static void AssertStackOrder(Component cc, Component womb, string when)
        {
            if (cc == null) return;
            try
            {
                int worstStamp = int.MinValue, firstOrgan = int.MaxValue;
                string stampName = null, organName = null;
                foreach (var root in new[] { cc, womb })
                {
                    if (root == null) continue;
                    foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                    {
                        if (r == null || r.sharedMaterials == null) continue;
                        foreach (var m in r.sharedMaterials)
                        {
                            if (m == null || m.shader == null) continue;
                            string sh = m.shader.name;
                            // BodyRevealExtra is the VEIL and is meant to draw after the organs - it is
                            // a redraw, not a stamp. Only BodyReveal marks the window.
                            if (sh == BodyRevealShader)
                            {
                                if (m.renderQueue > worstStamp) { worstStamp = m.renderQueue; stampName = m.name; }
                            }
                            else if (sh == "CloXray/Organ" || sh == "CloXray/OrgInside" || sh == "CloXray/Liquid")
                            {
                                if (m.renderQueue < firstOrgan) { firstOrgan = m.renderQueue; organName = m.name; }
                            }
                        }
                    }
                }
                if (stampName == null || organName == null) return;      // nothing to compare yet
                if (worstStamp < firstOrgan) return;                     // the rule holds
                if (Time.unscaledTime < _stackAssertNext) return;
                _stackAssertNext = Time.unscaledTime + 5f;
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: RENDER-QUEUE STACK IS MIXED (" + when
                    + ") - the stamp '" + stampName + "' draws at " + worstStamp + ", AFTER the organ '"
                    + organName + "' at " + firstOrgan + ". A stamp behind the organ it opens marks a"
                    + " window nothing reads, so that layer is invisible with no other symptom. Both"
                    + " stacks are internally consistent (legacy ~2499/3500, upgraded ~2478/2482); this"
                    + " is a MIX of the two. Full material dump follows.");
                TraceGarments(cc, "stack assertion failed");
            }
            catch (Exception e)
            { LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: AssertStackOrder threw: " + e.Message); }
        }

        // Identity of what is actually on the garment renderers right now. Names cannot answer this -
        // MaterialEditor clones carry the same name - so every line prints instance ids.
        public static void TraceGarments(Component cc, string when)
        {
            if (cc == null) return;
            try
            {
                Init();
                const BindingFlags ANY = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                GameObject[] slots = null;
                var fC = cc.GetType().GetField("objClothes", ANY);
                if (fC != null) slots = fC.GetValue(cc) as GameObject[];
                if (slots == null)
                {
                    var pC = cc.GetType().GetProperty("objClothes", ANY);
                    if (pC != null) slots = pC.GetValue(cc, null) as GameObject[];
                }
                if (slots == null) return;
                var sb = new System.Text.StringBuilder();
                sb.Append("CloXray GARMENT-ID (").Append(when).Append(") '").Append(cc.name).Append("'");
                foreach (int kind in ClothesKinds)
                {
                    if (kind >= slots.Length || slots[kind] == null || !slots[kind].activeInHierarchy) continue;
                    foreach (var r in slots[kind].GetComponentsInChildren<Renderer>(true))
                    {
                        if (r == null || r.sharedMaterials == null) continue;
                        sb.Append("\n  rend '").Append(r.name).Append("' id=").Append(r.GetInstanceID())
                          .Append(" enabled=").Append(r.enabled).Append(" vis=").Append(r.isVisible)
                          .Append(" sub=").Append(SubMeshCountOf(r)).Append(" mats=").Append(r.sharedMaterials.Length);
                        foreach (var m in r.sharedMaterials)
                        {
                            sb.Append("\n      ").Append(m == null ? "<null>" : m.name).Append(" id=")
                              .Append(m == null ? 0 : m.GetInstanceID()).Append(" q=")
                              .Append(m == null ? -1 : m.renderQueue).Append(" sh=")
                              .Append(m == null || m.shader == null ? "NULL" : m.shader.name);
                            // The numbers that decide whether fabric is painted over the womb.
                            // alpha = textureAlpha * (1 - _XrayAlpha): 0 = solid fabric, 1 = invisible.
                            if (m == null) continue;
                            foreach (var prop in new[] { "_XrayAlpha", "_StencilRef", "_StampZWrite", "_StampZTest", "_StampPull" })
                                if (m.HasProperty(prop))
                                    sb.Append(' ').Append(prop.Substring(1)).Append('=')
                                      .Append(m.GetFloat(prop).ToString("F3"));
                            if (m.HasProperty("_Color")) sb.Append(" colA=").Append(m.GetColor("_Color").a.ToString("F2"));
                        }
                    }
                }
                LiquidWobbleMPBPlugin._logger?.LogWarning(sb.ToString());
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: TraceGarments threw: " + e.Message); }
        }

        private static int SubMeshCountOf(Renderer r)
        {
            var smr = r as SkinnedMeshRenderer;
            if (smr != null && smr.sharedMesh != null) return smr.sharedMesh.subMeshCount;
            var mf = r.GetComponent<MeshFilter>();
            return (mf != null && mf.sharedMesh != null) ? mf.sharedMesh.subMeshCount : -1;
        }

        public static void DumpXrayChain(Component male, Component female, Component womb)
        {
            // keyed on POSE too. One dump per character set was right for log volume, but a
            // reporter has the penis rendering correctly on apply and degrading on a POSE CHANGE - and
            // nothing here re-checks the x-ray then, so a single apply-time dump shows only the healthy
            // state. One dump per pose brackets the transition: the last good chain and the first bad one
            // are both in the log, and diffing them says which material moved.
            string key = (male ? male.GetInstanceID() : 0) + ":" + (female ? female.GetInstanceID() : 0)
                       + ":" + (womb ? womb.GetInstanceID() : 0) + ":pose" + MainGameWomb.PoseVersion;
            if (!_chainDumped.Add(key)) return;
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
                        // TEXTURE MAPPING. KPlug sets _MainTex OFFSET per body part - Dick (-0.5, 0.5)
                        // against Ball (0, 0.5) - and the ME copies inherit whatever it left there. A
                        // half-texture offset samples the wrong region and reads blank, which on screen is
                        // indistinguishable from white. Every chain compared so far was identical in
                        // everything the dump prints; this is the one thing KPlug demonstrably changes
                        // that it did NOT print.
                        if (m.HasProperty("_MainTex"))
                        {
                            var tex = m.GetTexture("_MainTex");
                            Vector2 off = m.GetTextureOffset("_MainTex"), scl = m.GetTextureScale("_MainTex");
                            sb.Append(" | MainTex=").Append(tex != null ? tex.name : "NULL")
                              .Append(" off=(").Append(off.x.ToString("F2")).Append(",").Append(off.y.ToString("F2"))
                              .Append(") scale=(").Append(scl.x.ToString("F2")).Append(",").Append(scl.y.ToString("F2")).Append(")");
                            if (m.HasProperty("_overtex1"))
                            { var ov = m.GetTexture("_overtex1"); sb.Append(" overtex1=").Append(ov != null ? ov.name : "NULL"); }
                        }
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
                // BALLS STAMP STATE: shader, queue and the organ-value ref on the dan_f copy.
                if (male != null)
                    foreach (var r in male.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    {
                        if (r == null || r.name != "o_dan_f" || r.sharedMaterials == null) continue;
                        sb.Append("\n").Append("  BALLS '").Append(r.name).Append("':");
                        foreach (var m in r.sharedMaterials)
                        {
                            if (m == null) continue;
                            sb.Append("\n").Append("    '").Append(m.name).Append("' q=").Append(m.renderQueue)
                              .Append(" sh=").Append(m.shader != null ? m.shader.name : "NULL");
                            // the printer was blind to the carve - it asked only for BodyReveal stamp
                            // props, so an OrgInside copy printed bare and a default-float replay was
                            // indistinguishable from a configured one. Print both role's props plus the mask.
                            foreach (var pn in new[] { "_StencilRef", "_StampZWrite", "_StampZTest",
                                                       "_StencilBody", "_StencilBody_Plus_1", "_OutsideOfBodyAlpha",
                                                       "_OutsideShieldDepth", "_BottomWindow", "_RegionMaskCutoff" })
                                if (m.HasProperty(pn)) sb.Append(" ").Append(pn.Substring(1)).Append("=").Append(m.GetFloat(pn).ToString("F1"));
                            if (m.HasProperty("_RegionMask"))
                                sb.Append(" RegionMask=").Append(m.GetTexture("_RegionMask") != null ? m.GetTexture("_RegionMask").name : "NULL");
                        }
                        break;
                    }
                // EVERY renderer+material on the male, one line each: the balls material is not named
                // "tama" on this rig, so the white-balls report needs the real names before it can be
                // diagnosed. Shader + MainTex + colour distinguish "untextured", "wrong shader" and
                // "white tint" - three faults that look identical on screen.
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
        // TORSO REGION MASK. The stencil says only "body", and her hand IS her body - same mesh, same
        // material, same copy - so the womb draws through a hand crossing her belly exactly as it draws
        // through the belly. BodyReveal stamps with ZTest LEqual, so under a hand the fragment that
        // stamps is the HAND; clipping it there means the pixel is never marked as body and the hand
        // reads solid. The mask is torso-white / limb-black in body UV space, generated from the body
        // mesh's own skin weights by KK	ools\make_torso_mask.py.
        //
        // Routed through MaterialEditor ON PURPOSE, and only where a copy is configured for the first
        // time. An existing scene keeps its BodyReveal copy, EnsureBodyReveal no-ops on it, and no mask
        // is ever assigned - so old scenes render exactly as they always did while new applies get the
        // mask. Backward compatibility falls out of the structure instead of needing a flag.
        private const string RegionMaskFile = "cloxray_torso_mask.png";
        private static MethodInfo _mSetTexFile;
        private static bool _regionMaskMissingLogged;

        // ---- DEFERRED MASK ASSERT --------------------------------------------------------
        // ME's SetMaterialShader is DEFERRED: the apply log has repeatedly shown the copy still wearing
        // Shader Forge/main_skin at the moment configuration finished, with the flip to BodyReveal
        // landing later. Setting _RegionMask in that gap targets a material that does not HAVE the
        // property yet, so the edit is dropped - and BodyReveal's default mask is WHITE, which stamps
        // EVERYWHERE. That is exactly "bare hands do not block". So the mask is asserted from Update:
        // wait until the copy actually wears a shader with _RegionMask, then set it once.
        private class PendingMask
        {
            public object me; public Material copy; public GameObject go; public string who; public bool debug;
            public int frames; public int setAttempts; public int lastSetFrame;
            public bool reflipTried;   // one mid-wait SetShader retry before the timeout
        }
        private static readonly System.Collections.Generic.List<PendingMask> _pendingMasks = new System.Collections.Generic.List<PendingMask>();

        internal static void QueueRegionMask(object me, Material copy, GameObject go, string who, bool debug)
        {
            if (copy == null) return;
            foreach (var pm in _pendingMasks) if (pm.copy == copy) return;   // already queued
            _pendingMasks.Add(new PendingMask { me = me, copy = copy, go = go, who = who, debug = debug });
        }

        /// <summary>Called every frame by the plugin. Applies queued region masks once their copy's
        /// shader flip has landed; fails loud after ~10s (the flip never landing is a real fault).</summary>
        public static void PumpRegionMasks()
        {
            if (_pendingMasks.Count == 0) return;
            for (int i = _pendingMasks.Count - 1; i >= 0; i--)
            {
                var pm = _pendingMasks[i];
                if (pm.copy == null) { _pendingMasks.RemoveAt(i); continue; }
                pm.frames++;
                if (!pm.copy.HasProperty("_RegionMask"))
                {
                    // a copy whose flip never lands is not just maskless - it is a FULLY VISIBLE
                    // duplicate skin draw at 2500 (still the body shader): double reflective skin reads
                    // as blown-out white, washes alpha garments drawn over it into invisibility ("part
                    // of the top missing"), and under the pause menu's post-processing whites out the
                    // whole screen. Retry the flip once mid-wait; if it still refuses, REMOVE the stuck
                    // copy - a missing x-ray beats a broken body, and the next hotkey recreates it.
                    if (pm.frames > 450 && !pm.reflipTried)
                    {
                        pm.reflipTried = true;
                        try { _mSetShader.Invoke(pm.me, new object[] { 0, _otCharacter, pm.copy, BodyRevealShader, pm.go, true }); }
                        catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: shader re-flip threw: " + e.Message); }
                    }
                    if (pm.frames > 900)
                    {
                        try { _mCopyRemove.Invoke(pm.me, new object[] { 0, _otCharacter, pm.copy, pm.go }); }
                        catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: stuck-copy removal threw: " + e.Message); }
                        LiquidWobbleMPBPlugin._logger?.LogError("CloXray: the BodyReveal shader flip never landed on '" + pm.who
                            + "' (copy still '" + (pm.copy.shader ? pm.copy.shader.name : "?") + "') - REMOVED the stuck copy (it was a visible duplicate skin: "
                            + "blown-white body, washed-out alpha clothes, white screen under the pause menu). Press the hotkey to re-apply.");
                        _pendingMasks.RemoveAt(i);
                    }
                    continue;
                }
                // Shader is right. The set can be WIPED by ME's own deferred settle (verified in the
                // Studio panel: "No Texture" after a logged apply), so an entry only leaves the queue
                // once the texture has been seen PRESENT well after the set - and a wiped set is
                // retried with spacing, loudly giving up after 6 attempts.
                if (pm.copy.GetTexture("_RegionMask") != null)
                {
                    if (pm.setAttempts == 0) { _pendingMasks.RemoveAt(i); continue; }   // present before the set - nothing to prove
                    if (pm.frames - pm.lastSetFrame >= 30)
                    {
                        if (pm.debug) LiquidWobbleMPBPlugin._logger?.LogInfo("MEBridge: region mask STICKY on '" + pm.who + "' (attempt " + pm.setAttempts + ").");
                        _pendingMasks.RemoveAt(i);
                    }
                    continue;
                }
                if (pm.setAttempts > 0 && pm.frames - pm.lastSetFrame < 30) continue;   // let the set settle or get wiped first
                if (pm.setAttempts >= 6)
                {
                    LiquidWobbleMPBPlugin._logger?.LogError("CloXray: the torso region mask will not stick on '" + pm.who
                        + "' after " + pm.setAttempts + " attempts - hands/limbs WILL carve the x-ray. Send this log.");
                    _pendingMasks.RemoveAt(i);
                    continue;
                }
                pm.setAttempts++;
                pm.lastSetFrame = pm.frames;
                ApplyRegionMask(pm.me, pm.copy, pm.go, pm.who, pm.debug);
            }
        }

        // ---- MASK HEAL (replaces the earlier watch) ---------------------------------------------
        // Mask TEXTURES cannot persist in ME's records (SetMaterialTexture's
        // record never stores it), while shaders, floats and queues deterministically do.
        // So after any mid-H body rebuild, ME's record replay produces a fully configured copy with
        // NO mask - and the shaders' default masks are WHITE, which passes everywhere: the dan_f
        // carve then paints the whole sack through clothes in OrgInside tint, and the body stamp
        // quietly loses its limb blocking. The earlier watch pinned the Material OBJECT and died with
        // it: replay creates a NEW object, the old reference nulls, the pump pruned it, and the
        // replayed copy ran maskless until the next hotkey. This registry is reference-free - it
        // remembers WHAT to look for (character root, base name, shader role), and the pump scans
        // for any matching .MECopy that is masked-by-intent (cutoff > 0) but has no texture,
        // whoever created it. Removal needs no bookkeeping: un-applied copies stop existing and the
        // scan simply finds nothing.
        private class MaskHeal { public GameObject root; public string baseName; public string shaderName; public string file; }
        private static readonly System.Collections.Generic.List<MaskHeal> _maskHeal = new System.Collections.Generic.List<MaskHeal>();
        private static int _maskWatchFrame;

        private static void HealMask(GameObject root, string baseName, string shaderName, string file)
        {
            if (root == null || string.IsNullOrEmpty(baseName) || string.IsNullOrEmpty(shaderName)) return;
            foreach (var w in _maskHeal)
                if (w.root == root && w.baseName == baseName && w.shaderName == shaderName) { w.file = file; return; }
            _maskHeal.Add(new MaskHeal { root = root, baseName = baseName, shaderName = shaderName, file = file });
        }

        public static void PumpMaskWatch()
        {
            if (_maskHeal.Count == 0 || ++_maskWatchFrame % 30 != 0) return;
            for (int i = _maskHeal.Count - 1; i >= 0; i--)
            {
                var w = _maskHeal[i];
                if (w.root == null) { _maskHeal.RemoveAt(i); continue; }   // character despawned
                try
                {
                    foreach (var r in w.root.GetComponentsInChildren<Renderer>(true))
                    {
                        if (r == null || r.sharedMaterials == null) continue;
                        foreach (var m in r.sharedMaterials)
                        {
                            if (m == null || !m.name.Contains(".MECopy") || BaseName(m) != w.baseName) continue;
                            if (m.shader == null || m.shader.name != w.shaderName) continue;
                            if (!m.HasProperty("_RegionMask") || m.GetTexture("_RegionMask") != null) continue;
                            if (m.HasProperty("_RegionMaskCutoff") && m.GetFloat("_RegionMaskCutoff") <= 0f) continue;
                            if (DirectSetRegionMask(null, m, w.file))
                                LiquidWobbleMPBPlugin._logger?.LogInfo("MEBridge: mask HEALED on '" + m.name + "' under '"
                                    + w.root.name + "' (ME replay rebuilt the copy without its texture - it cannot persist ours).");
                        }
                    }
                }
                catch { }
            }
        }

        private static readonly System.Collections.Generic.Dictionary<string, Texture2D> _maskTexCache
            = new System.Collections.Generic.Dictionary<string, Texture2D>();   // per-file, loaded once

        // Set the mask straight onto every material that matches the copy's base name and has the
        // property - the same matching rule ME's MaterialAPI.SetTexture uses (NameFormatted equality,
        // renderers under go), minus everything that can silently refuse. Returns true if any set.
        private static bool DirectSetRegionMask(GameObject go, Material copy, string path)
        {
            try
            {
                Texture2D tex;
                if (!_maskTexCache.TryGetValue(path, out tex) || tex == null)
                {
                    var bytes = System.IO.File.ReadAllBytes(path);
                    tex = new Texture2D(2, 2, TextureFormat.ARGB32, false);
                    if (!tex.LoadImage(bytes)) { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: could not decode '" + path + "' - that mask cannot work."); return false; }
                    tex.wrapMode = TextureWrapMode.Clamp;
                    tex.name = System.IO.Path.GetFileNameWithoutExtension(path);
                    _maskTexCache[path] = tex;
                }
                string baseName = BaseName(copy);
                int set = 0;
                if (go != null)
                    foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                    {
                        if (r == null || r.sharedMaterials == null) continue;
                        foreach (var m in r.sharedMaterials)
                            if (m != null && BaseName(m) == baseName && m.name == copy.name && m.HasProperty("_RegionMask"))
                            { m.SetTexture("_RegionMask", tex); set++; }
                    }
                if (copy.HasProperty("_RegionMask") && copy.GetTexture("_RegionMask") == null)
                { copy.SetTexture("_RegionMask", tex); set++; }
                return set > 0;
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: direct region mask set failed: " + e.Message); return false; }
        }

        // Forensics: what does ME's controller ACTUALLY hold after the invoke? The decompiled 4.0.3
        // adds the record unconditionally, yet the UI showed "No Texture" - one of the links lies,
        // and this prints which: the record list, and every matching material's live state.
        private static void DumpMeTextureState(object me, Material copy, string who)
        {
            try
            {
                var sb = new System.Text.StringBuilder("MEBridge: ME texture state for '" + who + "':");
                var pl = me.GetType().GetProperty("MaterialTexturePropertyList", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var fl = pl == null ? me.GetType().GetField("MaterialTexturePropertyList", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) : null;
                var list = (pl != null ? pl.GetValue(me, null) : (fl != null ? fl.GetValue(me) : null)) as System.Collections.IEnumerable;
                int n = 0;
                if (list != null)
                    foreach (var e in list)
                    {
                        var ty = e.GetType();
                        object prop = ty.GetProperty("Property") != null ? ty.GetProperty("Property").GetValue(e, null) : null;
                        if (!(prop is string) || (string)prop != "RegionMask") continue;
                        n++;
                        object mn = ty.GetProperty("MaterialName") != null ? ty.GetProperty("MaterialName").GetValue(e, null) : "?";
                        object tid = ty.GetProperty("TexID") != null ? ty.GetProperty("TexID").GetValue(e, null) : "?";
                        object ot = ty.GetProperty("ObjectType") != null ? ty.GetProperty("ObjectType").GetValue(e, null) : "?";
                        object ci = ty.GetProperty("CoordinateIndex") != null ? ty.GetProperty("CoordinateIndex").GetValue(e, null) : "?";
                        sb.Append("\n  record: mat='" + mn + "' TexID=" + (tid ?? "null") + " type=" + ot + " coord=" + ci);
                    }
                sb.Append("\n  RegionMask records: " + n + (list == null ? " (list NOT FOUND via reflection)" : ""));
                sb.Append("\n  tracked copy '" + copy.name + "': hasProp=" + copy.HasProperty("_RegionMask")
                    + " tex=" + (copy.HasProperty("_RegionMask") && copy.GetTexture("_RegionMask") != null ? copy.GetTexture("_RegionMask").name : "NULL"));
                LiquidWobbleMPBPlugin._logger?.LogWarning(sb.ToString());
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: ME texture state dump failed: " + e.Message); }
        }

        private static bool ApplyRegionMask(object me, Material copy, GameObject go, string who, bool debug)
        {
            if (copy == null || !copy.HasProperty("_RegionMask")) return false;   // older shader zipmod: nothing to set
            try
            {
                if (_mSetTexFile == null)
                    _mSetTexFile = _ctrlType.GetMethod("SetMaterialTextureFromFile",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (_mSetTexFile == null) return false;

                string dir = System.IO.Path.GetDirectoryName(typeof(MEBridge).Assembly.Location);
                string path = System.IO.Path.Combine(dir, RegionMaskFile);
                if (!System.IO.File.Exists(path))
                {
                    if (!_regionMaskMissingLogged)
                    {
                        _regionMaskMissingLogged = true;
                        LiquidWobbleMPBPlugin._logger?.LogError("CloXray: " + RegionMaskFile + " is missing from '" + dir
                            + "' - the womb will keep drawing through hands and thighs. Re-install the plugin folder.");
                    }
                    return false;
                }
                // ME's signature grew a trailing display-message flag in later builds; fill by arity.
                var ps = _mSetTexFile.GetParameters();
                var args = new object[ps.Length];
                args[0] = 0; args[1] = _otCharacter; args[2] = copy; args[3] = "RegionMask"; args[4] = path; args[5] = go;
                for (int i = 6; i < ps.Length; i++)
                    args[i] = ps[i].ParameterType == typeof(bool) ? (object)false : null;
                // FUNCTION FIRST, persistence second. Six verified ME invokes produced neither a
                // texture on the material nor a record in ME's own UI (decompiled 4.0.3 says both are
                // unconditional - the forensic dump below finds out which link actually breaks). The
                // mask being LIVE must not be hostage to that: load the PNG ourselves and set it on
                // every matching material directly. Reloads re-run this pump, so function survives
                // even if ME never persists the edit.
                bool direct = DirectSetRegionMask(go, copy, path);   // path = this caller's mask file
                if (copy.shader != null) HealMask(go, BaseName(copy), copy.shader.name, path);   // reference-free re-assert after body rebuilds
                _mSetTexFile.Invoke(me, args);   // still try ME, for card/scene persistence
                bool landed = false;
                try { landed = copy.GetTexture("_RegionMask") != null; } catch { }
                if (landed)
                    LiquidWobbleMPBPlugin._logger?.LogInfo("MEBridge: torso region mask LIVE on '" + who + "' (direct=" + direct + ") - limbs no longer carve the x-ray.");
                else
                {
                    LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: region mask STILL absent on '" + who + "' even after the direct set - dumping ME state.");
                    DumpMeTextureState(me, copy, who);
                }
                return landed;
            }
            catch (Exception e)
            { LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: region mask not applied on '" + who + "': " + e.Message); return false; }
        }

        // ── PENIS LOOK SNAPSHOT ───────────────────────────────────────────────────────────────────────
        // The one certainty about the KPlug case: the penis is CORRECT immediately after
        // the first apply, and only goes wrong later when KPlug re-writes its shader/texture package.
        // So stop reasoning about what the values ought to be - record the whole set while it is known
        // good, and put it back whenever KPlug disturbs it.
        //
        // This deliberately does NOT sync from the live original. That copied
        // KPlug's offset onto the CARVE copy too, and the carve is what tints the in-window penis - it
        // was the one part that had always looked right, and overwriting it turned "white outside" into
        // "white everywhere". The snapshot keeps each material's own values, whatever they are.
        private class MatState
        {
            public Shader shader; public int queue;
            public bool hasMain, hasOver; public Texture main, over; public Vector2 off, scale;
        }
        private static readonly System.Collections.Generic.Dictionary<string, MatState> _penisSnap
            = new System.Collections.Generic.Dictionary<string, MatState>();
        private static int _penisSnapOwner;

        private static bool IsDickRenderer(string n) { return n == "o_dankon" || n == "o_dan_f"; }

        /// <summary>Record the penis material set while it is known good. First capture per male wins.</summary>
        public static void CapturePenisLook(Component male, bool debug)
        {
            if (male == null) return;
            int id = male.GetInstanceID();
            if (_penisSnapOwner == id && _penisSnap.Count > 0) return;   // already have the good state
            try
            {
                _penisSnap.Clear(); _penisSnapOwner = id;
                foreach (var r in male.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || r.sharedMaterials == null || !IsDickRenderer(r.name)) continue;
                    foreach (var m in r.sharedMaterials)
                    {
                        if (m == null) continue;
                        var s = new MatState { shader = m.shader, queue = m.renderQueue };
                        if ((s.hasMain = m.HasProperty("_MainTex")))
                        { s.main = m.GetTexture("_MainTex"); s.off = m.GetTextureOffset("_MainTex"); s.scale = m.GetTextureScale("_MainTex"); }
                        if ((s.hasOver = m.HasProperty("_overtex1"))) s.over = m.GetTexture("_overtex1");
                        _penisSnap[r.name + "|" + BaseName(m)] = s;
                    }
                }
                LiquidWobbleMPBPlugin._logger?.LogInfo("MEBridge: captured the penis look on '" + male.name + "' — "
                    + _penisSnap.Count + " material(s). This is the state it will be restored to if KPlug re-writes it.");
            }
            catch (Exception e)
            { LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: penis look capture failed: " + e.Message); _penisSnap.Clear(); }
        }

        /// <summary>Put the captured state back. Called right after KPlug re-writes the materials.</summary>
        public static void RestorePenisLook(Component male, bool debug)
        {
            if (male == null || _penisSnap.Count == 0 || _penisSnapOwner != male.GetInstanceID()) return;
            try
            {
                int n = 0;
                foreach (var r in male.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || r.sharedMaterials == null || !IsDickRenderer(r.name)) continue;
                    foreach (var m in r.sharedMaterials)
                    {
                        MatState s;
                        if (m == null || !_penisSnap.TryGetValue(r.name + "|" + BaseName(m), out s)) continue;
                        if (s.shader != null && m.shader != s.shader) { m.shader = s.shader; n++; }
                        if (m.renderQueue != s.queue) { m.renderQueue = s.queue; n++; }
                        if (s.hasMain && m.HasProperty("_MainTex"))
                        {
                            if (m.GetTexture("_MainTex") != s.main) { m.SetTexture("_MainTex", s.main); n++; }
                            if (m.GetTextureOffset("_MainTex") != s.off) { m.SetTextureOffset("_MainTex", s.off); n++; }
                            if (m.GetTextureScale("_MainTex") != s.scale) { m.SetTextureScale("_MainTex", s.scale); n++; }
                        }
                        if (s.hasOver && m.HasProperty("_overtex1") && m.GetTexture("_overtex1") != s.over)
                        { m.SetTexture("_overtex1", s.over); n++; }
                    }
                }
                if (n > 0 && debug)
                    LiquidWobbleMPBPlugin._logger?.LogInfo("MEBridge: restored the captured penis look on '" + male.name + "' (" + n + " value(s) put back).");
            }
            catch { /* runs inside another plugin's call stack - never throw */ }
        }

        public static void ForgetPenisLook() { _penisSnap.Clear(); _penisSnapOwner = 0; }

        // ---- ME SHADER REGISTRY DUMP -----------------------------------------------------
        // Every disk source is exonerated (one zipmod, current props; no other manifest among 7416
        // declares CloXray; no ME shader cache; no loose abdata bundle) - yet materials end up bound to
        // a CloXray/BodyReveal WITHOUT the newer properties. The remaining suspect is MaterialEditor's
        // in-memory shader dictionary. Dump every CloXray entry it holds, with instance id and property
        // fingerprint, so the next log convicts or exonerates it.
        // ---- DIRECT SHADER SET -----------------------------------------------------------
        // ME's SetMaterialShader applies via name lookups with two silent failure modes (an
        // instanced-materials reference check on the create path, and GetObjectMaterials name misses
        // on the set path), and H-mode body rebuilds re-roll those dice constantly. The result was a
        // copy left wearing the VISIBLE body shader - the blown-white duplicate skin behind weeks of
        // vanishing-garment reports. Same cure as the mask: set the shader directly on the
        // exact material references held here, resolved from ME's own LoadedShaders registry; ME's call
        // stays for scene persistence only. The registry queue is applied like ME would.
        private static System.Collections.IDictionary _meShaderRegistry;
        private static Shader ResolveMeShader(string shaderName, out int? registryQueue)
        {
            registryQueue = null;
            try
            {
                if (_meShaderRegistry == null)
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        Type[] types;
                        try { types = asm.GetTypes(); } catch { continue; }
                        foreach (var ty in types)
                        {
                            if (ty == null || ty.Name.IndexOf("MaterialEditor", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            var f = ty.GetField("LoadedShaders", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                            var dict = f != null ? f.GetValue(null) as System.Collections.IDictionary : null;
                            if (dict != null) { _meShaderRegistry = dict; break; }
                        }
                        if (_meShaderRegistry != null) break;
                    }
                if (_meShaderRegistry == null || !_meShaderRegistry.Contains(shaderName)) return null;
                var entry = _meShaderRegistry[shaderName];
                if (entry == null) return null;
                Shader sh = entry as Shader;
                if (sh == null)
                {
                    var ty = entry.GetType();
                    var sf = ty.GetField("Shader"); var sp = sf == null ? ty.GetProperty("Shader") : null;
                    sh = (sf != null ? sf.GetValue(entry) : sp != null ? sp.GetValue(entry, null) : null) as Shader;
                    var qf = ty.GetField("RenderQueue"); var qp = qf == null ? ty.GetProperty("RenderQueue") : null;
                    object q = qf != null ? qf.GetValue(entry) : qp != null ? qp.GetValue(entry, null) : null;
                    if (q != null) { try { registryQueue = Convert.ToInt32(q); } catch { } }
                }
                return sh;
            }
            catch { return null; }
        }

        /// <summary>Set the shader NOW on the tracked copy and its renderer-slot instances - no name
        /// lookups that can miss. Loud error if ME's registry lacks the shader (zipmod problem).</summary>
        private static bool DirectSetShader(Material copy, GameObject go, string shaderName)
        {
            if (copy == null) return false;
            if (copy.shader != null && copy.shader.name == shaderName) return true;   // already there
            int? rq;
            var sh = ResolveMeShader(shaderName, out rq);
            if (sh == null)
            {
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: '" + shaderName + "' is not in MaterialEditor's shader registry - the flip cannot happen. Update/repair [Clo]XrayShaders.zipmod.");
                return false;
            }
            copy.shader = sh;
            if (rq.HasValue) copy.renderQueue = rq.Value;
            if (go != null)
                foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || r.sharedMaterials == null) continue;
                    foreach (var m in r.sharedMaterials)
                        if (m != null && m != copy && m.name == copy.name && (m.shader == null || m.shader.name != shaderName))
                        { m.shader = sh; if (rq.HasValue) m.renderQueue = rq.Value; }
                }
            return true;
        }

        private static bool _meRegistryDumped;
        internal static void DumpMeShaderRegistry()
        {
            if (_meRegistryDumped) return;
            _meRegistryDumped = true;
            try
            {
                var sb = new System.Text.StringBuilder("CloXray: ME shader registry -");
                int found = 0;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = asm.GetTypes(); } catch { continue; }
                    foreach (var ty in types)
                    {
                        if (ty == null || ty.Name.IndexOf("MaterialEditor", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var f = ty.GetField("LoadedShaders", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        var dict = f != null ? f.GetValue(null) as System.Collections.IDictionary : null;
                        if (dict == null) continue;
                        foreach (System.Collections.DictionaryEntry e in dict)
                        {
                            string key = e.Key as string;
                            if (key == null || !key.StartsWith("CloXray/")) continue;
                            found++;
                            Shader sh = e.Value as Shader;
                            if (sh == null && e.Value != null)
                            {
                                var sf = e.Value.GetType().GetField("Shader");
                                if (sf != null) sh = sf.GetValue(e.Value) as Shader;
                                else { var sp = e.Value.GetType().GetProperty("Shader"); if (sp != null) sh = sp.GetValue(e.Value, null) as Shader; }
                            }
                            if (sh == null) { sb.Append("\n").Append("  '").Append(key).Append("': entry holds NO Shader object"); continue; }
                            var pm = new Material(sh);
                            sb.Append("\n").Append("  '").Append(key).Append("' shId=").Append(sh.GetInstanceID())
                              .Append(" supported=").Append(sh.isSupported)
                              .Append(" fp[RM=").Append(pm.HasProperty("_RegionMask") ? 1 : 0)
                              .Append(",SZW=").Append(pm.HasProperty("_StampZWrite") ? 1 : 0)
                              .Append(",SZT=").Append(pm.HasProperty("_StampZTest") ? 1 : 0).Append("]");
                            UnityEngine.Object.Destroy(pm);
                        }
                    }
                }
                if (found == 0) sb.Append(" no CloXray entries found (registry not located or empty)");
                LiquidWobbleMPBPlugin._logger?.LogWarning(sb.ToString());
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: ME registry dump failed: " + e.Message); }
        }

        private static bool IsPenisMat(string n)
        {
            if (string.IsNullOrEmpty(n)) return false;
            int sp = n.IndexOf(" (");
            if (sp > 0) n = n.Substring(0, sp);
            return n == PenisMat;
        }

        // Does this character carry a real penis material (cm_m_dankon)? Used to gate the penis features so the
        // shared k_f_dan FK bones - which KK_AdditionalFKNodes ALSO adds to females - don't trigger on a normal female.
        // Penis mesh that is actually DRAWN. Every female carries a hidden penis object, so a test that
        // counts inactive renderers would make any woman a penetration candidate - her own phantom entry
        // marker sits at her crotch, exactly where her womb is.
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

        /// <summary>
        /// Idempotently ensure the body ALSO has the BodyRevealExtra "skin veil" copy (the cosmetic
        /// translucent-skin layer over the organ window, with the XrayAlpha slider).
        /// A SECOND ME copy is required because one material has ONE render queue: the stamp must
        /// draw before the organ, the veil after it. Run AFTER EnsureBodyReveal.
        /// </summary>
        /// The veil redraws HER SKIN over the window, so it needs the body's texture. An ME copy does
        /// not reliably carry `_MainTex` (the body's is a runtime-composited texture, and ME cannot
        /// persist texture records for these copies - the same reason the region mask needs healing),
        /// and an unbound texture samples WHITE: lowering XrayAlpha then washed the window toward
        /// white instead of toward skin. Re-feed it from the live source every time the copy is touched.
        private static void EnsureVeilMainTex(Material veil, Material src, string who)
        {
            if (veil == null || src == null) return;
            if (!veil.HasProperty("_MainTex") || !src.HasProperty("_MainTex")) return;
            var want = src.GetTexture("_MainTex");
            var have = veil.GetTexture("_MainTex");
            // State readout: a body texture is usually a RUNTIME-COMPOSITED RenderTexture whose .name
            // is EMPTY, which reads as "no texture" in any name-printing dump. Say null-vs-unnamed
            // outright so that blank never gets mistaken for a missing texture again.
            LiquidWobbleMPBPlugin._logger?.LogInfo("MEBridge: skin veil on '" + who + "' texture check - source="
                + (want == null ? "NULL" : "'" + want.name + "' (" + want.GetType().Name + ")")
                + "  veil=" + (have == null ? "NULL" : "'" + have.name + "' (" + have.GetType().Name + ")")
                + "  same=" + (have == want));
            if (want == null || have == want) return;
            veil.SetTexture("_MainTex", want);
            veil.SetTextureOffset("_MainTex", src.GetTextureOffset("_MainTex"));
            veil.SetTextureScale("_MainTex", src.GetTextureScale("_MainTex"));
            LiquidWobbleMPBPlugin._logger?.LogInfo("MEBridge: skin veil on '" + who + "' had no body texture - re-fed '"
                + want.name + "' (an unbound texture samples white, which washed the x-ray window out).");
        }

        public static bool EnsureBodyVeil(Component cc, int stencilPlus1, bool debug, bool overwriteExisting)
        {
            Init();
            if (cc == null || _ctrlType == null) return false;
            if (MainGameWomb.IsMaleChara(cc)) return false;   // wearer-only, same as the reveal stamp

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
                    EnsureVeilMainTex(existing, FindByName(bodyR, "cf_m_body")
                        ?? (bodyR.sharedMaterials.Length > 0 ? bodyR.sharedMaterials[0] : null), cc.name);
                    return true;
                }

                // Fresh copy. MaterialCopyRemove on the SOURCE adds a copy; verify one actually
                // appeared (toggle semantics differ across ME versions - fail loud, never guess).
                var srcBody = FindByName(bodyR, "cf_m_body") ?? (bodyR.sharedMaterials.Length > 0 ? bodyR.sharedMaterials[0] : null);
                if (srcBody == null) return false;
                var copy = FindCopy(bodyR, BaseName(srcBody)) ?? CreateCopyTracked(me, bodyR, srcBody, go, "skin veil");
                if (copy == null) return false;

                _mSetShader.Invoke(me, new object[] { 0, _otCharacter, copy, BodyVeilShader, go, true });
                DirectSetShader(copy, go, BodyVeilShader);   // the flip happens NOW; the ME call above is persistence only
                _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, copy, "StencilBody_Plus_1", (float)stencilPlus1, go, true });
                // Initial x-ray strength - set ONLY on creation (a re-press never clobbers the
                // per-scene slider tweaks; the existing-copy path above doesn't touch it).
                _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, copy, "XrayAlpha", LiquidWobbleMPBPlugin.CfgVeilAlpha, go, true });
                EnsureVeilQueue(me, copy, go);
                EnsureVeilMainTex(copy, srcBody, cc.name);
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

        // is this renderer parented under the HEAD/NECK branch of the skeleton? HAIR is very
        // commonly worn as an ACCESSORY (extensions, ponytails, ahoge) and attaches under the head/
        // neck bones; the Free-H accessory reveal was stamping CloXray/BodyReveal onto it, replacing
        // its hair shader and changing the character's look (reported - main game only, since the
        // accessory pass is Free-H only). Head/face/hair accessories can NEVER occlude the belly x-ray
        // window, so they must be skipped. Gate on the skeleton attach bones (unambiguous), so even a
        // long ponytail that hangs down to the waist is still excluded (it's under cf_j_head).
        private static readonly HashSet<string> HeadBranchBones = new HashSet<string>
        { "cf_j_neck", "cf_j_head", "cf_s_head", "p_cf_head_bone", "ct_head", "cf_J_FaceRoot", "cf_J_FaceBase" };
        private static bool IsHeadBranchAccessory(Transform t)
        {
            for (var p = t; p != null; p = p.parent)
                if (HeadBranchBones.Contains(p.name)) return true;
            return false;
        }

        // the accessory's declared attach anchor (KK coordinate 'parentKey'). Head/hair/face
        // anchors mean it frames the head and can't cover the belly - skip it.
        private static readonly string[] HeadKeyMarkers = { "head", "hair", "kami", "megane", "earring", "nose", "mouth", "face", "mimi" };
        private static bool IsHeadRegionParent(string parentKey)
        {
            if (string.IsNullOrEmpty(parentKey)) return false;
            string k = parentKey.ToLowerInvariant();
            foreach (var mk in HeadKeyMarkers) if (k.Contains(mk)) return true;
            return false;
        }

        // Read ChaControl.nowCoordinate.accessory.parts[slot].parentKey (the game's authoritative
        // per-accessory attach anchor), resolved lazily off the live objects. Null on any miss - the
        // runtime attach-bone walk still guards, and a bind failure is logged ONCE so it's visible.
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

        /// <summary>
        /// Dump a garment renderer's material array against its submeshes.
        /// Unity pairs materials[i] with submesh i; a material past subMeshCount re-draws the LAST submesh,
        /// and a submesh with no material is not drawn at all. So appending a BodyReveal copy to a
        /// garment whose array is already irregular can leave a real submesh drawing with an x-ray
        /// material - transparent outside the womb window - and the garment (or part of it) disappears.
        /// The body underneath is already masked away by the clothing state, so the result is a HOLE, not
        /// bare skin. The count mismatch is warned about but the actual pairing was never shown; this does.
        /// </summary>
        private static void DumpGarmentPairing(Renderer r, string when)
        {
            if (r == null) return;
            try
            {
                var smr = r as SkinnedMeshRenderer;
                int subs = (smr != null && smr.sharedMesh != null) ? smr.sharedMesh.subMeshCount : -1;
                var mats = r.sharedMaterials;
                var sb = new System.Text.StringBuilder();
                sb.Append("[clothes]   ").Append(when).Append(" '").Append(r.name).Append("': subMeshCount=")
                  .Append(subs < 0 ? "n/a" : subs.ToString()).Append(" materials=").Append(mats.Length);
                // Only flag pairings that actually BREAK. materials > subMeshCount is the normal, intended
                // result of appending the copy: the extra re-draws the last submesh, which for a
                // single-submesh garment is the whole thing - that is how the window is drawn, not a fault.
                // The real faults are a submesh with no material (never drawn) and a MULTI-submesh garment,
                // where the appended copy only ever covers the last one and the rest keep their originals.
                if (subs >= 0 && mats.Length < subs)
                    sb.Append("  <== BROKEN: ").Append(subs - mats.Length).Append(" submesh(es) have NO material and will not draw");
                else if (subs > 1 && mats.Length > subs)
                    sb.Append("  <== PARTIAL: multi-submesh garment, the copy covers ONLY submesh ").Append(subs - 1);
                LiquidWobbleMPBPlugin._logger?.LogInfo(sb.ToString());
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    string where = subs < 0 ? "" : (i < subs ? " -> submesh " + i : " -> NO SUBMESH (re-draws submesh " + (subs - 1) + ")");
                    if (m == null) { LiquidWobbleMPBPlugin._logger?.LogInfo("[clothes]     [" + i + "] (null)" + where); continue; }
                    string sh = m.shader != null ? m.shader.name : "(no shader)";
                    string extra = "";
                    if (m.HasProperty("_StencilRef")) extra += " StencilRef=" + m.GetFloat("_StencilRef");
                    // adopted copies persist values from ANY past build - an early stamp still
                    // writing depth on an ALPHA garment (like the q=3100 sweater) depth-rejects the
                    // garment's own later draw = it visually disappears. Print the whole stamp state.
                    if (m.HasProperty("_StampZWrite")) extra += " StampZWrite=" + m.GetFloat("_StampZWrite").ToString("F0");
                    if (m.HasProperty("_StampZTest")) extra += " StampZTest=" + m.GetFloat("_StampZTest").ToString("F0");
                    if (m.HasProperty("_OutsideOfBodyAlpha")) extra += " OutsideAlpha=" + m.GetFloat("_OutsideOfBodyAlpha");
                    LiquidWobbleMPBPlugin._logger?.LogInfo("[clothes]     [" + i + "] " + m.name + " [" + sh + "] q=" + m.renderQueue + extra + where);
                }
            }
            catch (Exception e)
            { LiquidWobbleMPBPlugin._logger?.LogWarning("[clothes] pairing dump failed on '" + r.name + "': " + e.Message); }
        }

        /// <summary>
        /// A worn-item stamp must not write DEPTH. Correct on the body (the skin already wrote that
        /// depth at 2350) but never on clothes or accessories: a transparent garment writes no depth of
        /// its own, so a copy at 2500 would lay an opaque footprint for it and depth-reject whatever
        /// is behind - which is how a clothes stamp erases a character standing behind her.
        /// Applied to EXISTING copies too, not just new ones: the depth write is a defect, so leaving it
        /// on a copy an older build created would keep that scene broken.
        /// </summary>
        private static void ClearStampDepth(object me, int slot, object objType, Material mat, GameObject go)
        {
            if (mat == null || !mat.HasProperty("_StampZWrite")) return;   // older shader zipmod
            if (Mathf.Approximately(mat.GetFloat("_StampZWrite"), 0f)) return;
            try { _mSetFloat.Invoke(me, new object[] { slot, objType, mat, "StampZWrite", 0f, go, true }); }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: could not clear stamp depth on '" + mat.name + "': " + e.Message); }
        }

        // KK clothes kinds stamped by EnsureClothesReveal: top, bot, bra, shorts, panst.
        // (Gloves/socks/shoes never cover the womb; skipping them keeps ME's record count down.)
        private static readonly int[] ClothesKinds = { 0, 1, 2, 3, 5 };

        // Install (or keep in sync) the translucent garment redraw for one garment SOURCE material.
        // The copy is a clone of the garment, so it carries the garment's own textures and colors;
        // swapping it to the skin veil's shader gives it the window stencil gate and the XrayAlpha
        // fade. XrayAlpha is written ONCE at create (garment default 0.7 - fabric reads better a
        // little more present than the skin veil's 0.9) and never on a
        // re-press - the per-garment slider is hand-owned afterwards, like the skin veil's.
        private static void EnsureGarmentVeil(object me, int slot, object objType, Renderer r, Material src,
                                              string baseName, int stencil, GameObject go, string who, bool allowCreate)
        {
            try
            {
                Material veil = null;
                foreach (var m in r.sharedMaterials)
                    if (m != null && m.name.StartsWith(baseName + ".MECopy") &&
                        m.shader != null && m.shader.name == BodyVeilShader) { veil = m; break; }
                if (veil != null)
                {
                    if (veil.HasProperty("_StencilBody_Plus_1") && Mathf.RoundToInt(veil.GetFloat("_StencilBody_Plus_1")) != stencil + 1)
                        _mSetFloat.Invoke(me, new object[] { slot, objType, veil, "StencilBody_Plus_1", (float)(stencil + 1), go, true });
                    // The queue was written only at CREATE, so a ghost made before the upgrade
                    // stayed on the legacy queue forever. Accepts either stack unless this pass is upgrading.
                    if (UpgradeStack ? veil.renderQueue != GarmentVeilQueueUp
                                     : !QueueOk(veil.renderQueue, GarmentVeilQueue, GarmentVeilQueueUp))
                        SetQueuePersisted(me, slot, objType, veil, go, Q(GarmentVeilQueue, GarmentVeilQueueUp));
                    return;
                }
                // NEVER create on a load. The garment ghost shipped later than the garment stamp, so a
                // scene authored on an older build has a stamp and NO ghost - and an automatic pass that
                // makes one adds a MaterialEditor copy the scene never saved, on every load, to a
                // character that looked right without it. Correct an existing ghost here; creating one is
                // the hotkey's to do, exactly as it is for the stamp itself.
                if (!allowCreate) return;
                var before = new System.Collections.Generic.List<Material>(r.sharedMaterials);
                _mCopyRemove.Invoke(me, new object[] { slot, objType, src, go });
                foreach (var m in r.sharedMaterials)
                    if (m != null && !before.Contains(m)) { veil = m; break; }
                if (veil == null)
                {
                    LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: garment ghost copy not created for '"
                        + baseName + "' on '" + r.name + "' (ME MaterialCopyRemove had no effect).");
                    return;
                }
                _mSetShader.Invoke(me, new object[] { slot, objType, veil, BodyVeilShader, go, true });
                DirectSetShader(veil, go, BodyVeilShader);
                // RE-RESOLVE BY NAME. Setting a shader through MaterialEditor re-assigns the renderer's
                // material array, which re-instances every entry - so the reference captured before that
                // call can be an orphan nothing draws. The writes below still SAVE (ME keys its record by
                // material NAME), which is why the scene comes back correct while the live material sits
                // on its shader default: measured, XrayAlpha read 0.500 three seconds after a write of
                // 0.770, and the saved scene recorded 0.770. Same class as the customMatBody orphan.
                string veilName = BaseName(veil);
                foreach (var m2 in r.sharedMaterials)
                    if (m2 != null && BaseName(m2) == veilName && m2.shader != null && m2.shader.name == BodyVeilShader)
                    {
                        if (!ReferenceEquals(m2, veil))
                            LiquidWobbleMPBPlugin._logger?.LogInfo("MEBridge: garment ghost '" + veilName
                                + "' was re-instanced by the shader change (#" + veil.GetInstanceID()
                                + " -> #" + m2.GetInstanceID() + "); writing to the drawn one.");
                        veil = m2;
                        break;
                    }
                _mSetFloat.Invoke(me, new object[] { slot, objType, veil, "StencilBody_Plus_1", (float)(stencil + 1), go, true });
                _mSetFloat.Invoke(me, new object[] { slot, objType, veil, "XrayAlpha", LiquidWobbleMPBPlugin.CfgGarmentVeilAlpha, go, true });
                // Albedo only. The veil shader's lighting multiplies by `_RampG`, which a garment
                // material does not carry - the sample comes back black and the ghost paints a black
                // silhouette over the window (it read as a dark tint at the shipped 0.7).
                _mSetFloat.Invoke(me, new object[] { slot, objType, veil, "FlatFabric", 1f, go, true });
                // READ BACK. This is the value that decides whether the ghost paints fabric over the
                // window - alpha = textureAlpha * (1 - XrayAlpha) - and it is written exactly once, at
                // create, through MaterialEditor. There is no Material Editor UI in Free-H, so if the
                // write is silently dropped nothing can show it and the fabric simply reads solid.
                // LOOK IT UP AGAIN, do not hand over this reference. ME re-assigns the renderer's material
                // array, so the copy captured above becomes an orphan nothing draws - reading IT reported
                // 0.500 while Material Editor's panel showed the 0.770 that had actually been written, and
                // the saved scene recorded 0.770 too. The check earns its place in Free-H, which has no
                // Material Editor UI: a genuinely dropped write would otherwise show only as solid fabric.
                LiquidWobbleMPBPlugin.VerifyFloatSoon(r, BaseName(veil), BodyVeilShader, "_XrayAlpha",
                    LiquidWobbleMPBPlugin.CfgGarmentVeilAlpha, "garment ghost '" + BaseName(veil) + "'");
                // What the ghost actually has to paint with. The copy is re-shadered from a garment,
                // so any input the veil shader expects may be absent - and an absent one reads as
                // black. Print them once per ghost instead of inferring from the screen.
                Func<Material, string, string> tex = (mm, pn) =>
                {
                    if (!mm.HasProperty(pn)) return pn + "=<no prop>";
                    var tx = mm.GetTexture(pn);
                    return pn + "=" + (tx == null ? "NULL" : "'" + tx.name + "'(" + tx.GetType().Name + ")");
                };
                Func<Material, string, string> col = (mm, pn) =>
                    mm.HasProperty(pn) ? pn + "=" + mm.GetColor(pn).ToString("F2") : pn + "=<no prop>";
                LiquidWobbleMPBPlugin._logger?.LogInfo("MEBridge: garment ghost '" + veil.name + "' inputs - "
                    + tex(veil, "_MainTex") + "  " + tex(veil, "_ColorMask") + "  " + tex(veil, "_RampG") + "  "
                    + col(veil, "_Color") + "  " + col(veil, "_Color2") + "  " + col(veil, "_Color3")
                    + "   | source " + tex(src, "_MainTex") + "  " + col(src, "_Color"));
                SetQueuePersisted(me, slot, objType, veil, go, Q(GarmentVeilQueue, GarmentVeilQueueUp));
            }
            catch (Exception e)
            { LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: garment ghost failed for '" + baseName + "' on '" + who + "': " + e.Message); }
        }

        /// <summary>
        /// Idempotently stamp every WORN (active) torso garment with a BodyReveal copy at the given
        /// stencil - the womb then x-rays through the clothes, and the out-of-body bleed disappears
        /// at the stamped pixels (validated in-game by depth-containment testing).
        /// Mirrors the manual ME recipe: per garment material, Copy Material -> CloXray/BodyReveal
        /// -> StencilRef. ME persists the copies in the scene/card like the body ones.
        /// </summary>
        public static bool EnsureClothesReveal(Component cc, int stencil, bool debug, bool allowCreate)
        {
            // WEARER-ONLY BY DESIGN, now enforced. The sex debug print below exists because a male
            // reaching this path was already suspected: during penetration his cf_j_kokan can sit
            // within wearer-resolution range of the womb, so position (not settings) decides whether
            // he is mis-resolved as the wearer - which stamps HIS garments and, with the body
            // alpha-masked underneath, reads as 'the male lost bodyparts'. Loud + nothing applied.
            if (MainGameWomb.IsMaleChara(cc))
            {
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: EnsureClothesReveal requested on MALE '" + cc.name
                    + "' - clothes/accessory reveal is wearer-only by design. Stamping NOTHING. Send this log.");
                return false;
            }
            Init();
            if (cc == null || _ctrlType == null) return false;
            if (MainGameWomb.IsMaleChara(cc)) return false;   // wearer-only, same as the reveal stamp
            if (_otClothing == null)
            {
                LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: ME ObjectType has no Clothing member — clothes stamping unavailable.");
                return false;
            }

            try
            {
                var me = cc.GetComponent(_ctrlType);
                if (me == null) return false;

                // objClothes is a FIELD in some game builds and a PROPERTY in others (observed:
                // property - the field lookup came back null in-game). Try both, any visibility.
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

                int stamped = 0, updated = 0, untouched = 0;
                if (debug)
                {
                    LiquidWobbleMPBPlugin._logger?.LogInfo("[clothes] '" + cc.name + "': objClothes.Length=" + slots.Length +
                        " otClothing='" + _otClothing + "' kinds=" + string.Join(",", Array.ConvertAll(ClothesKinds, k => k.ToString())));
                    // WHO is being stamped? Clothes reveal is wearer-only, so a male reaching this point at
                    // all is the bug, not the shader. Print the raw sex value beside the verdict.
                    string sexRaw = "?";
                    try
                    {
                        var pSex = cc.GetType().GetProperty("sex", BindingFlags.Instance | BindingFlags.Public);
                        if (pSex != null) sexRaw = Convert.ToInt32(pSex.GetValue(cc, null)).ToString();
                        else { var fSex = cc.GetType().GetField("sex", BindingFlags.Instance | BindingFlags.Public); if (fSex != null) sexRaw = Convert.ToInt32(fSex.GetValue(cc)).ToString(); }
                    }
                    catch { }
                    LiquidWobbleMPBPlugin._logger?.LogInfo("[clothes] TARGET '" + cc.name + "': sex=" + sexRaw
                        + " (0=male) IsMaleChara=" + MainGameWomb.IsMaleChara(cc) + " stencil=" + stencil);
                }
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

                        if (debug) DumpGarmentPairing(r, "BEFORE");

                        // Snapshot the SOURCE materials first (ME appends copies while this runs).
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
                            // "cf_m_bot_skirt01.MECopy1" - strip the suffixes before matching.
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
                                ClearStampDepth(me, kind, _otClothing, existing, go);
                                // Only when it differs, and either stack is accepted unless this pass is
                                // upgrading her - the same guard the veil below uses. An existing garment
                                // carries whatever the scene saved for it, and this runs on every load, so
                                // an unconditional write persisted a queue the scene never asked for onto
                                // a garment that was already correct.
                                if (UpgradeStack ? existing.renderQueue != GarmentStampQueueUp
                                                 : !QueueOk(existing.renderQueue, GarmentStampQueue, GarmentStampQueueUp))
                                    SetQueuePersisted(me, kind, _otClothing, existing, go, Q(GarmentStampQueue, GarmentStampQueueUp));
                                ApplyStampPull(me, kind, _otClothing, existing, src, go, cc.name);
                                EnsureGarmentVeil(me, kind, _otClothing, r, src, baseName, stencil, go, cc.name, allowCreate);
                                continue;
                            }

                            // OPTION C: this garment has no CloXray copy, so the scene never
                            // recorded it as see-through. Only an explicit hotkey press may create one.
                            // An automatic pass - scene load, character reload, body swap - leaves it
                            // alone, which is what makes a deliberately-opaque top stay opaque and stops
                            // a garment added after the last press from being x-rayed unasked. Old scenes
                            // are unaffected: MaterialEditor has always persisted these copies, so a
                            // garment that WAS transparent still arrives with one and takes the branch
                            // above.
                            if (!allowCreate) { untouched++; continue; }

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
                            DirectSetShader(copy, go, BodyRevealShader);   // the flip happens NOW; the ME call above is persistence only
                            _mSetFloat.Invoke(me, new object[] { kind, _otClothing, copy, "StencilRef", (float)stencil, go, true });
                            ClearStampDepth(me, kind, _otClothing, copy, go);
                            SetQueuePersisted(me, kind, _otClothing, copy, go, Q(GarmentStampQueue, GarmentStampQueueUp));
                            ApplyStampPull(me, kind, _otClothing, copy, src, go, cc.name);
                            EnsureGarmentVeil(me, kind, _otClothing, r, src, baseName, stencil, go, cc.name, allowCreate);
                            stamped++;
                            if (debug) DumpGarmentPairing(r, "AFTER  copy of '" + baseName + "'");
                        }
                    }
                }

                if (stamped > 0 || updated > 0 || untouched > 0 || debug)
                    LiquidWobbleMPBPlugin._logger?.LogInfo("MEBridge: clothes x-ray on '" + cc.name + "': " + stamped
                        + " garment material(s) stamped, " + updated + " restenciled, " + untouched
                        + " left opaque (no saved copy" + (allowCreate ? "" : "; automatic pass never creates")
                        + ") (stencil " + stencil + ").");
                return true;
            }
            catch (Exception e)
            {
                LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: EnsureClothesReveal failed on '" + (cc ? cc.name : "?") + "': " + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        // Same stamp for worn ACCESSORIES (jewelry, acc-clothing, skirts built from accessories…) -
        // in Free-H the character is dressed almost entirely by the card, so anything covering the
        // belly must carry the reveal stencil too or it occludes the x-ray window. Mirrors
        // EnsureClothesReveal: one BodyReveal ME copy per source material, per active accessory slot.
        public static bool EnsureAccessoryReveal(Component cc, int stencil, bool debug, bool allowCreate)
        {
            // WEARER-ONLY BY DESIGN, now enforced. The sex debug print below exists because a male
            // reaching this path was already suspected: during penetration his cf_j_kokan can sit
            // within wearer-resolution range of the womb, so position (not settings) decides whether
            // he is mis-resolved as the wearer - which stamps HIS garments and, with the body
            // alpha-masked underneath, reads as 'the male lost bodyparts'. Loud + nothing applied.
            if (MainGameWomb.IsMaleChara(cc))
            {
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: EnsureAccessoryReveal requested on MALE '" + cc.name
                    + "' - clothes/accessory reveal is wearer-only by design. Stamping NOTHING. Send this log.");
                return false;
            }
            Init();
            if (cc == null || _ctrlType == null) return false;
            if (MainGameWomb.IsMaleChara(cc)) return false;   // wearer-only, same as the reveal stamp
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

                int stamped = 0, updated = 0, untouched = 0;
                for (int slot = 0; slot < slots.Length; slot++)
                {
                    var go = slots[slot];
                    if (go == null || !go.activeInHierarchy) continue;   // only accessories that are ON

                    // the accessory's AUTHORITATIVE attach point is stored per-slot in the
                    // coordinate ('parentKey') - read that too, not just the runtime hierarchy. Hair /
                    // head accessories carry a head/hair parentKey (e.g. a_n_headside, ct_hairF); those
                    // can never occlude the belly window, so skip the whole slot regardless of where its
                    // mesh happens to sit in the transform tree.
                    bool headSlot = IsHeadRegionParent(AccessoryParentKey(cc, slot));

                    foreach (var r in go.GetComponentsInChildren<Renderer>(false))
                    {
                        if (r == null || r.sharedMaterials == null) continue;
                        // never stamp head/face/HAIR accessories - they can't occlude the belly
                        // x-ray window and stamping them changed the character's hair look (reported).
                        // Detected by EITHER the coordinate parentKey (headSlot) OR the runtime attach bone.
                        // Also SELF-HEAL: if a PRIOR build already stamped this hair, remove that copy so
                        // re-applying the womb restores the original hair look (MaterialCopyRemove on a
                        // copy material removes it, per ME's toggle semantics).
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
                            if (m.name.Contains(".MECopy")) continue;                                   // a copy, not a source
                            if (m.shader != null && m.shader.name.StartsWith("CloXray/")) continue;     // already ours
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
                                    ClearStampDepth(me, slot, _otAccessory, existing, go);
                                    SetQueuePersisted(me, slot, _otAccessory, existing, go, Q(GarmentStampQueue, GarmentStampQueueUp));
                                    updated++;
                                }
                                EnsureGarmentVeil(me, slot, _otAccessory, r, src, baseName, stencil, go, cc.name, allowCreate);
                                continue;
                            }

                            if (!allowCreate) { untouched++; continue; }   // automatic passes never create
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
                            DirectSetShader(copy, go, BodyRevealShader);   // the flip happens NOW; the ME call above is persistence only
                            ClearStampDepth(me, slot, _otAccessory, copy, go);
                            SetQueuePersisted(me, slot, _otAccessory, copy, go, Q(GarmentStampQueue, GarmentStampQueueUp));
                            _mSetFloat.Invoke(me, new object[] { slot, _otAccessory, copy, "StencilRef", (float)stencil, go, true });
                            EnsureGarmentVeil(me, slot, _otAccessory, r, src, baseName, stencil, go, cc.name, allowCreate);
                            stamped++;
                        }
                    }
                }

                if (stamped > 0 || updated > 0 || debug)
                    LiquidWobbleMPBPlugin._logger?.LogInfo("MEBridge: accessory x-ray on '" + cc.name + "': " + untouched + " left opaque, " + stamped + " material(s) stamped, " + updated + " restenciled (stencil " + stencil + ").");
                return true;
            }
            catch (Exception e)
            {
                LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: EnsureAccessoryReveal failed on '" + (cc ? cc.name : "?") + "': " + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        // The veil MUST sit after the organ or it silently does nothing (BodyVeilQueue).
        // Assigning the shader normally adopts its tag queue; if ME kept a different queue (an old
        // scene pins the pre-2500 stack's value), push it via ME's queue API (persisted) + set it
        // directly (immediate), and say so. This is what converts an old scene's veil.
        // The body stencil stamp must draw BEFORE every organ layer. Newly assigned shaders adopt the
        // tag queue on their own; this exists for the copy adopted from a scene that pins the old one.
        private static void EnsureStampQueue(object me, Material stamp, GameObject go, string who)
        {
            if (stamp == null) return;
            // Accept EITHER stack: a plain load must not drag an upgraded scene back to legacy. The
            // hotkey moves it deliberately; anything else only repairs a value from neither stack.
            if (UpgradeStack ? stamp.renderQueue == BodyStampQueueUp
                             : QueueOk(stamp.renderQueue, BodyStampQueue, BodyStampQueueUp)) return;
            int want = Q(BodyStampQueue, BodyStampQueueUp);
            LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: '" + who + "' body stamp queue is " + stamp.renderQueue
                + " (want " + want + ") - correcting.");
            SetQueuePersisted(me, stamp, go, want);
        }

        private static void EnsureVeilQueue(object me, Material veil, GameObject go)
        {
            if (veil == null) return;
            if (UpgradeStack ? veil.renderQueue == BodyVeilQueueUp
                             : QueueOk(veil.renderQueue, BodyVeilQueue, BodyVeilQueueUp)) return;
            int want = Q(BodyVeilQueue, BodyVeilQueueUp);
            LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: veil copy queue is " + veil.renderQueue + " (want " + want + ") — correcting.");
            SetQueuePersisted(me, veil, go, want);
        }

        // Push a render queue through ME's queue API (persisted with the scene/card) AND set it
        // directly on the material (immediate). Signature varies across ME versions - args filled by
        // parameter type; the second int slot is the queue (first is the slot index 0).
        private static void SetQueuePersisted(object me, Material m, GameObject go, int queue)
        {
            SetQueuePersisted(me, 0, _otCharacter, m, go, queue);
        }

        // garment stamp copies live at queue 2501 - ONE step after the body stamp (2500) - so
        // the body's LimbBlock pass has already written bit7 by the time a garment stamp tests it.
        // Same queue would leave the order to Unity's whim and the sleeve-over-hand block would race.
        private static void SetQueuePersisted(object me, int slot, object objType, Material m, GameObject go, int queue)
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
                        if (pt == typeof(int))            { args[i] = slotFilled ? (object)queue : (object)slot; slotFilled = true; }
                        else if (pt == _objType)          args[i] = objType;
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
    // reflection - soft dependency. Links the male penis FK bones to the female vagina + the womb's penis_target,
    // the same mechanism BetterPenetration's Studio component uses. NodesConstraints.AddConstraint is idempotent
    // (skips an existing pair) and persists constraints with the scene via KKAPI ExtendedSave, so one AddConstraint
    // call is enough for the link to save/load.
    internal static class NodeConstraintBridge
    {
        // ── OLD-SCENE NC PATH MIGRATION ───────────────────────────────────────────────────────────
        // Womb 7.4.0 renamed the item's internal bones to clo_* so they stop colliding with HER bone
        // names (ABMX indexes by bare name, first-match-wins).
        // NodesConstraints saves a constraint as a PATH relative to the object, so a scene authored
        // against the old womb points at names that no longer exist. NC's loader does this:
        //
        //     val = val.Find(childNode.Attributes["parentPath"].Value);
        //     if (val == null) continue;      // <-- constraint SILENTLY DROPPED
        //
        // so hand-made parenting just vanishes, with no error and nothing left to repair afterwards.
        // The fix therefore has to land BEFORE NC parses: prefix LoadSceneGeneric, rewrite the paths in
        // the XmlNode, and let NC resolve them normally. Once the scene is re-saved it carries the new
        // path and this never runs for it again - a one-time migration, not a permanent translation
        // layer.
        //
        // Deliberately conservative: it only touches a path that (a) belongs to a CloXray womb and
        // (b) does NOT currently resolve. A path that still works is never rewritten, and a path with a
        // segment that cannot be accounted for - a PRUNED bone, which no longer exists in any form - is left
        // alone and reported, because half-fixing it would be worse than failing visibly.
        private static bool _migrateInstalled, _migrateAbsentLogged;
        private static int _migrated, _unrecoverable;

        private static Type FindNcType()
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t2 = a.GetType("NodesConstraints.NodesConstraints", false);
                    if (t2 != null) return t2;
                }
                catch { }
            }
            return null;
        }

        internal static void InstallScenePathMigration()
        {
            if (_migrateInstalled) return;
            // DO NOT go through Init() here. It latches `_tried` on its first call, and BepInEx loads this plugin
            // BEFORE NodesConstraints (verified: LiquidWobbleMPB at chainloader line 158, NC at 170), so
            // calling it from Awake resolved nothing and then poisoned the bridge for the whole session.
            // Resolve independently and keep retrying until NC actually exists.
            if (_ncType == null)
            {
                var found = FindNcType();
                if (found == null)
                {
                    // Not loaded YET, or not installed at all. Say so once, late, so "no migration" is
                    // never silent - that silence is exactly what hid this failure the first time.
                    if (!_migrateAbsentLogged && Time.realtimeSinceStartup > 25f)
                    {
                        _migrateAbsentLogged = true;
                        LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: NodesConstraints is not loaded — no constraint paths to migrate.");
                    }
                    return;
                }
                _ncType = found;
            }
            _migrateInstalled = true;
            try
            {
                var m = _ncType.GetMethod("LoadSceneGeneric", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (m == null)
                {
                    LiquidWobbleMPBPlugin._logger?.LogError("CloXray: NodesConstraints.LoadSceneGeneric not found — scenes saved against the "
                        + "pre-7.4.0 womb will silently lose any constraint attached to its bones. The NC path migration is NOT active.");
                    return;
                }
                if (_ncHarmony == null) _ncHarmony = new Harmony("Clo.LiquidWobbleMPB.ncpaths");
                _ncHarmony.Patch(m, prefix: new HarmonyMethod(typeof(NodeConstraintBridge)
                    .GetMethod(nameof(LoadScenePrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: NC scene-path migration armed — constraints saved against the old womb bone "
                    + "names are repaired as the scene loads.");
            }
            catch (Exception e)
            { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: could not arm the NC path migration: " + e.Message); }
        }

        /// <summary>Walk the saved path a level at a time, accepting the stored name or its clo_ form.</summary>
        // The 13 bones womb 7.4.0 DELETED (not renamed). Only a path failing exactly on one of
        // these is provably a womb path broken by the rename; any other unresolvable path (a missing modded
        // accessory, a third-party item) is NC's ordinary silent drop and out of scope.
        private static readonly System.Collections.Generic.HashSet<string> PrunedWombBones =
            new System.Collections.Generic.HashSet<string> {
                "cf_d_ana", "cf_j_ana", "cf_s_ana", "cf_j_spine02", "cf_s_spine02",
                "cf_d_siri_L", "cf_d_siri_R", "cf_d_siri01_L", "cf_d_siri01_R",
                "cf_j_siri_L", "cf_j_siri_R", "cf_s_siri_L", "cf_s_siri_R" };

        private static string MigratePath(Transform root, string saved, out string failedSegment)
        {
            failedSegment = null;
            if (root == null || string.IsNullOrEmpty(saved)) return null;
            if (root.Find(saved) != null) return null;                 // still resolves - leave it alone
            var segs = saved.Split('/');
            var outSegs = new string[segs.Length];
            Transform cur = root;
            for (int i = 0; i < segs.Length; i++)
            {
                Transform next = cur.Find(segs[i]) ?? cur.Find(MainGameWomb.WombBonePrefix + segs[i]);
                if (next == null) { failedSegment = segs[i]; return null; }   // report WHERE it broke
                outSegs[i] = next.name;
                cur = next;
            }
            return string.Join("/", outSegs);
        }

        private static void FixAttr(System.Xml.XmlNode c, string pathAttr, string idxAttr, System.Collections.IList dic)
        {
            try
            {
                if (c.Attributes[pathAttr] == null || c.Attributes[idxAttr] == null) return;
                int idx;
                if (!int.TryParse(c.Attributes[idxAttr].Value, out idx) || idx < 0 || idx >= dic.Count) return;
                var kv = dic[idx];
                var oci = kv.GetType().GetProperty("Value").GetValue(kv, null) as Studio.ObjectCtrlInfo;
                if (oci == null || oci.guideObject == null) return;
                Transform root = oci.guideObject.transformTarget;
                if (root == null) return;
                // NO component-based ownership test here. At NC load time Studio has created the item but
                // The CloXray components are not attached yet, so GetComponentInChildren<WombExpandEffect> was
                // always null and nothing was ever migrated. The test below is inherently safe and needs
                // no ownership question answered: only a path that CURRENTLY FAILS and whose clo_-walked
                // form RESOLVES TO A REAL TRANSFORM is ever rewritten. That can only repair a broken link.

                string saved = c.Attributes[pathAttr].Value;
                string failedSeg;
                string fixedPath = MigratePath(root, saved, out failedSeg);
                if (fixedPath == null)
                {
                    // Blame ourselves ONLY when the walk broke exactly on a bone 7.4.0 deleted. The
                    // previous form fired for ANY unresolvable path on ANY object (missing mods,
                    // changed third-party items) and loudly reported that a womb bone was removed -
                    // in scenes that sometimes contained no womb at all.
                    if (failedSeg != null && PrunedWombBones.Contains(failedSeg))
                    {
                        _unrecoverable++;
                        LiquidWobbleMPBPlugin._logger?.LogError("CloXray: a NodesConstraints link in this scene points at '" + saved
                            + "' on the womb, and '" + failedSeg + "' is one of the bones womb 7.4.0 removed (not renamed) — the link cannot be "
                            + "restored and NC will drop it. Re-make that constraint by hand.");
                    }
                    return;
                }
                c.Attributes[pathAttr].Value = fixedPath;
                _migrated++;
                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: migrated an old NC path on the womb — '" + saved + "' -> '" + fixedPath + "'.");
            }
            catch (Exception e)
            {
                // Never abort the scene load on one bad node - but never swallow it either: a failed
                // migration means NC will silently drop that constraint, which is the exact silence
                // this migration exists to prevent.
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: NC path migration failed on one constraint node - NC will drop it: " + e.Message);
            }
        }

        private static void LoadScenePrefix(System.Xml.XmlNode node, System.Collections.IList dic)
        {
            if (node == null || dic == null) return;
            _migrated = 0; _unrecoverable = 0;
            int seen = 0;
            foreach (System.Xml.XmlNode c in node.ChildNodes)
            {
                seen++;
                FixAttr(c, "parentPath", "parentObjectIndex", dic);
                FixAttr(c, "childPath",  "childObjectIndex",  dic);
            }
            if (AutoBodyReveal.Debug)
                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: NC load — inspected " + seen + " constraint node(s), migrated "
                    + _migrated + ", unrecoverable " + _unrecoverable + ".");
            if (_migrated > 0 || _unrecoverable > 0)
                LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: scene saved against the older womb — " + _migrated
                    + " constraint path(s) migrated to the current bone names"
                    + (_unrecoverable > 0 ? ", " + _unrecoverable + " could NOT be restored (see errors above)" : "")
                    + ". Re-save the scene and this will not be needed again.");
        }

        private const string NcGuid = "com.joan6694.illusionplugins.nodesconstraints";
        private const BindingFlags BF = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static bool _tried;
        private static Type _ncType;
        private static MethodInfo _addConstraint;   // the 10-arg convenience overload
        private static MethodInfo _removeConstraintAt;   // NodesConstraints.RemoveConstraintAt(int) - used by the aim takeover
        private static FieldInfo _fConstraints;     // NodesConstraints._constraints (List<Constraint>)
        private static FieldInfo _fChildTransform;  // Constraint.childTransform
        private static FieldInfo _fParentTransform; // Constraint.parentTransform
        private static FieldInfo _fPositionOffset;  // Constraint.positionOffset (parent-local: NC does parent.TransformPoint(offset))
        private static Harmony _ncHarmony;          // patches the NC pairing-change methods (add / enable-disable / delete)
        private static bool _pairingHooksTried;     // install-once guard for the pairing hooks

        // DIAG: dump every existing constraint (parent/child name + world position) to show what was
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

        // Live constraint count, or -1 if the list can't be read. Callers poll this to wait until
        // NodesConstraints has finished restoring a saved scene / re-binding after a character change:
        // adding while that list is still filling creates a SECOND copy of a link it is about to restore
        // (a scene saved with 2 links loaded back as 4).
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

        // Drops constraints whose parent or child transform has been destroyed - a character replacement
        // kills her half of "vagina -> k_f_dan_entry" and leaves the row behind. That row still counts as
        // "this node already has a constraint", which is what stopped the lost link from ever being
        // rebuilt. A row with a destroyed endpoint can never drive anything, so removing it loses nothing.
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
                    if (ch == null || pa == null) continue;   // dead endpoint: not ours to judge
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

        // Is `node` already wired into ANY existing constraint - as the driven CHILD or as a driving PARENT?
        // The hotkey uses this to NEVER reassign a dan node already targeted by hand (e.g. dan_entry/
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

        // What currently DRIVES `child`, if anything? Returns the constraint's parentTransform, or null when
        // no constraint has `child` as its childTransform. Distinct from HasConstraintForNode, which also
        // answers true when the node is the DRIVER of someone else's constraint.
        public static Transform DriverOf(Transform child)
        {
            Init();
            var inst = Instance();
            if (inst == null || child == null) return null;
            try
            {
                if (_fConstraints == null) _fConstraints = _ncType.GetField("_constraints", BF);
                var list = (_fConstraints != null ? _fConstraints.GetValue(inst) : null) as System.Collections.IList;
                if (list == null) return null;
                foreach (var c in list)
                {
                    if (c == null) continue;
                    if (_fChildTransform == null)  _fChildTransform  = c.GetType().GetField("childTransform", BF);
                    if (_fParentTransform == null) _fParentTransform = c.GetType().GetField("parentTransform", BF);
                    if (_fChildTransform == null || _fParentTransform == null) return null;
                    if ((_fChildTransform.GetValue(c) as Transform) == child)
                        return _fParentTransform.GetValue(c) as Transform;
                }
            }
            catch { }
            return null;
        }

        // The EXISTING parent -> child link, or null. Both ends must match - a node can carry several links.
        private static object FindLink(Transform parentTransform, Transform childTransform)
        {
            Init();
            var inst = Instance();
            if (inst == null || parentTransform == null || childTransform == null) return null;
            try
            {
                if (_fConstraints == null) _fConstraints = _ncType.GetField("_constraints", BF);
                var list = (_fConstraints != null ? _fConstraints.GetValue(inst) : null) as System.Collections.IList;
                if (list == null) return null;
                foreach (var c in list)
                {
                    if (c == null) continue;
                    if (_fChildTransform == null)  _fChildTransform  = c.GetType().GetField("childTransform", BF);
                    if (_fParentTransform == null) _fParentTransform = c.GetType().GetField("parentTransform", BF);
                    if (_fPositionOffset == null)  _fPositionOffset  = c.GetType().GetField("positionOffset", BF);
                    if (_fChildTransform == null || _fParentTransform == null || _fPositionOffset == null)
                    {
                        LiquidWobbleMPBPlugin._logger?.LogError("NodeConstraintBridge: NodesConstraints' Constraint has no childTransform/"
                            + "parentTransform/positionOffset field any more - link offsets cannot be read or set.");
                        return null;
                    }
                    if ((_fChildTransform.GetValue(c) as Transform) == childTransform
                        && (_fParentTransform.GetValue(c) as Transform) == parentTransform) return c;
                }
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogError("NodeConstraintBridge: reading the links failed: " + e.Message); }
            return null;
        }

        /// The position offset of the parent -> child link (parent-local, as NodesConstraints applies it).
        public static bool GetLinkOffset(Transform parentTransform, Transform childTransform, out Vector3 localOffset)
        {
            localOffset = Vector3.zero;
            object c = FindLink(parentTransform, childTransform);
            if (c == null) return false;
            localOffset = (Vector3)_fPositionOffset.GetValue(c);
            return true;
        }

        /// Set the position offset of the EXISTING parent -> child link. False when there is no such link.
        public static bool SetLinkOffset(Transform parentTransform, Transform childTransform, Vector3 localOffset)
        {
            object c = FindLink(parentTransform, childTransform);
            if (c == null) return false;
            _fPositionOffset.SetValue(c, localOffset);
            return true;
        }

        /// The driver of `child` whose name matches `parentName`, or null. A node can carry SEVERAL
        /// constraints - a scene may deliberately stack cf_J_Vagina_root (which wakes BP) and a hand-made sphere
        /// (which does the positioning) on the same k_f_dan_entry - so this asks for the one that
        /// matters instead of taking whichever comes first.
        public static Transform DriverNamed(Transform child, string parentName)
        {
            Init();
            var inst = Instance();
            if (inst == null || child == null) return null;
            try
            {
                if (_fConstraints == null) _fConstraints = _ncType.GetField("_constraints", BF);
                var list = (_fConstraints != null ? _fConstraints.GetValue(inst) : null) as System.Collections.IList;
                if (list == null) return null;
                foreach (var c in list)
                {
                    if (c == null) continue;
                    if (_fChildTransform == null)  _fChildTransform  = c.GetType().GetField("childTransform", BF);
                    if (_fParentTransform == null) _fParentTransform = c.GetType().GetField("parentTransform", BF);
                    if (_fChildTransform == null || _fParentTransform == null) return null;
                    if ((_fChildTransform.GetValue(c) as Transform) != child) continue;
                    var p = _fParentTransform.GetValue(c) as Transform;
                    if (p != null && p.name == parentName) return p;
                }
            }
            catch { }
            return null;
        }

        // Delete the constraint driving `child`, through NodesConstraints' OWN RemoveConstraintAt so its
        // bookkeeping (and the pairing hook) run exactly as they do for a GUI delete. NO FALLBACK: if that
        // method cannot be resolved, log and remove NOTHING, so a takeover never half-happens.
        public static bool RemoveDriverOf(Transform child)
        {
            Init();
            var inst = Instance();
            if (inst == null || child == null) return false;
            try
            {
                if (_fConstraints == null) _fConstraints = _ncType.GetField("_constraints", BF);
                var list = (_fConstraints != null ? _fConstraints.GetValue(inst) : null) as System.Collections.IList;
                if (list == null) return false;
                if (_removeConstraintAt == null)
                    _removeConstraintAt = _ncType.GetMethod("RemoveConstraintAt", BF, null, new[] { typeof(int) }, null);
                if (_removeConstraintAt == null)
                {
                    LiquidWobbleMPBPlugin._logger?.LogError("NodeConstraintBridge: NodesConstraints.RemoveConstraintAt(int) not found - cannot take over the aim on '"
                        + child.name + "'. Leaving the existing constraint untouched (no fallback).");
                    return false;
                }
                for (int i = 0; i < list.Count; i++)
                {
                    var c = list[i];
                    if (c == null) continue;
                    if (_fChildTransform == null) _fChildTransform = c.GetType().GetField("childTransform", BF);
                    if (_fChildTransform == null) return false;
                    if ((_fChildTransform.GetValue(c) as Transform) != child) continue;
                    _removeConstraintAt.Invoke(inst, new object[] { i });
                    return true;
                }
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("NodeConstraintBridge: RemoveDriverOf('" + child.name + "') failed: " + e.Message); }
            return false;
        }

        // Position-only link: parentTransform drives childTransform. Idempotent (NodesConstraints returns null if
        // the pair already exists, either direction). Returns true if added OR already present; false on failure.
        // FAIL-LOUD: a NodesConstraints link only DRIVES if an endpoint resolves to a registered Studio GuideObject
        // at add time (cached, not re-resolved per frame). If a freshly-added one resolved NEITHER, warn - it
        // persists and looks added but won't move anything (the silent-fallback case to surface).
        // `localOffset` is in the PARENT's local space - NodesConstraints places the child at
        // parent.TransformPoint(offset) every frame, so the offset turns and scales with the parent.
        public static bool AddPositionLink(Transform parentTransform, Transform childTransform, string alias, Vector3 localOffset = default(Vector3))
        {
            Init();
            if (_addConstraint == null || parentTransform == null || childTransform == null) return false;
            var inst = Instance();
            if (inst == null) { LiquidWobbleMPBPlugin._logger?.LogWarning("NodeConstraintBridge: no live NodesConstraints instance."); return false; }
            try
            {
                object res = _addConstraint.Invoke(inst, new object[] {
                    true, parentTransform, childTransform, true, localOffset, false, Quaternion.identity, false, Vector3.zero, alias });
                string pair = "'" + parentTransform.name + "' -> '" + childTransform.name + "'";
                if (res == null) { LiquidWobbleMPBPlugin._logger?.LogInfo("NodeConstraintBridge: constraint " + pair + " already present."); return true; }
                // No GuideObject check: NodesConstraints moves the child transform itself every frame, so the
                // link drives whether or not either endpoint is a Studio node.
                LiquidWobbleMPBPlugin._logger?.LogInfo("NodeConstraintBridge: added " + pair + ".");
                return true;
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("NodeConstraintBridge: AddConstraint failed (" + parentTransform.name + " -> " + childTransform.name + "): " + e.Message); return false; }
        }

        // ── NC PAIRING HOOKS ──────────────────────────────────────────────────────────────────────────────────
        // Harmony-postfix the THREE NodesConstraints methods that can change a k_f_dan_entry constraint - AddConstraint
        // (create; the GUI "Add", scene/XML load, copy, and AddPositionLink all funnel through it), the
        // SetConstraintEnabled toggle (the GUI checkbox, the "Link" toggle, and the Timeline interpolable all route
        // through it), and RemoveConstraintAt (delete). Each fires RequestRepair so every womb re-pairs on its next
        // post-IK frame. THIS is what lets WombExpandEffect run pairing fully event-driven with NO fallback poll: a
        // seated womb's entry is NC-pinned, so it only moves via these discrete operations + character (re)load (also
        // wired to RequestRepair). Lazy + install-once + fail-loud per hook: if NodesConstraints renames a method on a
        // future version it is LOGGED and that one event won't fire instantly (diagnosable, not silently masked by a poll).
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
    // BP's "Auto-Target" setting does NOT gate this path - which is why turning Auto-Target Off doesn't stop it.
    //
    // ONLY that by-name path is hooked: AddDanConstraints is called with NULL parents on the reload re-add and the
    // "Enable BP Controller" toggle, but with EXPLICIT parents when Auto-Target re-aims (CheckAutoTarget, which
    // also removes the old links first). So skip ONLY when both parents are null AND the male's k_f_dan_end is
    // ALREADY constrained in NodesConstraints (the re-add is then redundant). That leaves Auto-Target re-aiming,
    // first-time setup, hand-made NC edits, and the hotkey untouched, and is a complete no-op when BP isn't installed.
    // Signals the exact frame an UncensorSelector body rebuild finishes, via a postfix on its
    // ReloadCharacterBody (the same method BetterPenetration patches). Armed per character by the
    // uncensor carry-over; the waiting coroutine keys off Done() instead of polling for bones on a
    // timer. Install failure is loud and the waiter's cap then reports it - no alternate path.
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

        /// Install the hook WITHOUT waiting for the plugin to swap an uncensor itself.
        ///
        /// Install() is otherwise reachable only through Arm(), and only Free-H's forced-BP-uncensor path arms
        /// it. Nothing in Studio does, so without this the patch is never applied there and a hand-made uncensor
        /// change raises nothing at all: the womb loses its penis and stays unpaired until the hotkey. Called from
        /// InstallWombHooks, so the patch exists exactly while a womb is in the scene and a CloXray-free scene
        /// is still never patched. Scans the assemblies once, and retries until UncensorSelector is loaded.
        internal static void EnsureInstalled()
        {
            if (_installed) return;
            Type found = null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.FullName == null || a.FullName.IndexOf("UncensorSelector", StringComparison.OrdinalIgnoreCase) < 0) continue;
                try
                {
                    foreach (var t in a.GetTypes())
                        if (t.Name == "UncensorSelectorController") { found = t; break; }
                }
                catch { }   // an assembly that cannot list its types is not the one
                if (found != null) break;
            }
            if (found == null) return;   // UncensorSelector not loaded yet - the next womb hook install retries
            InstallType(found);
        }

        private static void Install(Component ctl)
        {
            if (_installed || ctl == null) return;
            InstallType(ctl.GetType());
        }

        private static void InstallType(Type controllerType)
        {
            if (_installed || controllerType == null) return;
            _installed = true;
            try
            {
                var m = controllerType.GetMethod("ReloadCharacterBody", BF);
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
                // A body rebuild replaces the material the renderer draws, so the clothing alpha mask
                // has to be re-pushed onto the new one.
                BodyMaterialFix.AdoptRenderedBodyMaterial(cc, "after an uncensor body rebuild");
                // The rebuild also destroys and remakes the penis bones, and a womb pairs to a penis by its
                // k_f_dan_entry. A FULL character (re)load re-pairs through OnCharacterReloaded, but this is a
                // PARTIAL reload and raises no such event, so switching a male's uncensor silently unpaired every
                // womb: the canal stopped reacting while BP - which rebuilds its own targets here - carried on,
                // and only the hotkey brought it back. Event-driven like every other re-pair; nothing polls.
                BPBridge.InvalidateBoneCache();   // ORDER: fresh bones first, so the re-pair below can see them
                WombExpandEffect.RequestRepair();
                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: '" + cc.name + "' finished an uncensor body rebuild"
                    + " - bone cache dropped, every womb re-pairs on its next post-IK frame.");
            }
            catch { }
        }
    }

    /// BODY ALPHA-MASK SYNC - the permanent transparent-torso fix.
    ///
    /// The game keeps the body's clothing alpha mask (and the _alpha_a/_alpha_b body-fade floats) on
    /// ChaControl.customMatBody, and writes there whenever clothes change. MaterialEditor reads
    /// materials through MaterialAPI.GetMaterials -> renderer.materials - Unity's INSTANCING getter,
    /// which CLONES every material on the renderer and assigns the clones back - and its
    /// GetObjectMaterials runs that across EVERY renderer of the character. So a single ME call for
    /// the penis x-ray also clones his BODY material: customMatBody becomes an orphan nobody renders,
    /// the game's later "undressed, clear the mask" writes land on it unseen, and the clone keeps the
    /// mask it was cloned with - the jacket's, which hides the torso. Permanent: removing the x-ray
    /// (or the whole plugin) cannot reach the clone; only a full body rebuild would.
    ///
    /// ME hits the same problem with its own copies and fixes it in ChangeAlphaMaskPostfix /
    /// ChangeClothesTopPostfix by pushing customMatBody's values onto rendBody.sharedMaterials - but
    /// only `if (rendBody.sharedMaterials.Length > 1)`, i.e. only when copies exist. HER body carries
    /// these copies so ME repairs it; HIS body has a single material, so ME's repair skips exactly the
    /// case an ME call from here creates. This is ME's repair without that gate, for the characters
    /// touched. It is a no-op whenever the renderer already holds the game's own material.
    internal static class BodyMaterialFix
    {
        private static Harmony _harmony;
        private static bool _tried;
        private static bool _membersMissingLogged;
        private static readonly System.Collections.Generic.HashSet<int> _touched = new System.Collections.Generic.HashSet<int>();

        /// Characters ME calls have run on - the only ones repaired (never a global side effect).
        public static void MarkTouched(Component cc)
        {
            if (cc == null) return;
            _touched.Add(cc.GetInstanceID());
            TryInstall();
        }


        /// ADOPTION - the one-shot cure: no polling, nothing of ours left running.
        ///
        /// ME's renderer.materials access clones the body material, leaving the game pointing at an
        /// object nobody renders, so every later clothing-mask write is lost (caught in testing:
        /// customMatBody id=-75204 vs the rendered id=-100064). Rather than chase those writes for
        /// ever, hand the game the material that IS on screen: copy its live body state onto the
        /// clone once, then re-point ChaControl.customMatBody at the clone. From then on the game
        /// writes straight to what the renderer draws and dressing/undressing work natively.
        ///
        /// The alternatives are worse: clearing the clone's mask leaves the torso unmasked under
        /// clothes (body geometry then renders inside tight garments), and undressing/redressing to
        /// steal a clean clone races the game's own clothes coroutines. ME's edits on the clone are
        /// kept - nothing is discarded - and this is a no-op when no cloning happened.
        public static void AdoptRenderedBodyMaterial(Component cc, string why)
        {
            try
            {
                if (cc == null) return;
                var cmb  = Member<Material>(cc, "customMatBody");
                var rend = Member<Renderer>(cc, "rendBody");
                if (cmb == null || rend == null || rend.sharedMaterials == null)
                {
                    if (!_membersMissingLogged)
                    {
                        _membersMissingLogged = true;
                        LiquidWobbleMPBPlugin._logger?.LogError("CloXray: ChaControl.customMatBody/rendBody not reachable ("
                            + (cmb == null ? "customMatBody" : "rendBody") + " missing) - the body material cannot be handed back to the game, so undressing may leave the torso hidden. Nothing was changed.");
                    }
                    return;
                }
                Material clone = null;
                foreach (var m in rend.sharedMaterials)
                {
                    if (m == null || ReferenceEquals(m, cmb)) continue;
                    if (m.name.Contains(".MECopy")) continue;                        // ME's copies are extras, never the body slot
                    if (MEBridge.BaseName(m) != MEBridge.BaseName(cmb)) continue;    // only the body material's own clone
                    clone = m; break;
                }
                if (clone == null) return;   // nothing was cloned - the game already owns what is drawn

                // The old material holds the game's current truth; carry it across before switching.
                if (clone.HasProperty("_AlphaMask") && cmb.HasProperty("_AlphaMask")) clone.SetTexture("_AlphaMask", cmb.GetTexture("_AlphaMask"));
                if (clone.HasProperty("_alpha_a")  && cmb.HasProperty("_alpha_a"))    clone.SetFloat("_alpha_a", cmb.GetFloat("_alpha_a"));
                if (clone.HasProperty("_alpha_b")  && cmb.HasProperty("_alpha_b"))    clone.SetFloat("_alpha_b", cmb.GetFloat("_alpha_b"));

                if (!SetMember(cc, "customMatBody", clone))
                {
                    LiquidWobbleMPBPlugin._logger?.LogError("CloXray: ChaControl.customMatBody could not be re-pointed"
                        + " - the game keeps writing the body mask to a material nobody renders, so undressing may leave"
                        + " the torso hidden. What is actually there:" + DescribeMember(cc, "customMatBody"));
                    return;
                }
                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: body material handed back to the game on '" + cc.name + "' (" + why
                    + "): customMatBody id=" + cmb.GetInstanceID() + " had been orphaned by MaterialEditor's material instancing; it now points at the rendered '"
                    + clone.name + "' id=" + clone.GetInstanceID() + ", so clothing alpha writes land on screen again.");
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: body material adoption failed on '" + (cc ? cc.name : "?") + "': " + e.Message); }
        }

        private static bool SetMember(object obj, string name, object value)
        {
            if (obj == null) return false;
            const BindingFlags BI = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            for (var t = obj.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, BI);
                if (f != null) { f.SetValue(obj, value); return true; }
                var p = t.GetProperty(name, BI);
                if (p != null && p.CanWrite) { p.SetValue(obj, value, null); return true; }
                // A GET-ONLY property still has storage behind it. Member<T> reads through the getter and
                // is happy; this used to walk past and report "no writable member", which is how the
                // re-point could fail on a build where the read plainly worked. An auto-property's store
                // is the compiler's backing field; a hand-written one commonly uses the _name form.
                if (p != null)
                {
                    var bf = t.GetField("<" + name + ">k__BackingField", BI) ?? t.GetField("_" + name, BI);
                    if (bf != null && bf.FieldType.IsInstanceOfType(value)) { bf.SetValue(obj, value); return true; }
                }
            }
            return false;
        }

        // What EXISTS under this name, and why each candidate was refused. Called only when SetMember has
        // already failed, so the next log says what the member actually is instead of only that it could
        // not be written - the difference between one more run and another round of guessing.
        private static string DescribeMember(object obj, string name)
        {
            if (obj == null) return "<null object>";
            const BindingFlags BI = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var sb = new System.Text.StringBuilder();
            for (var t = obj.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, BI);
                if (f != null) sb.Append(" [").Append(t.Name).Append(".field ").Append(f.FieldType.Name).Append(']');
                var p = t.GetProperty(name, BI);
                if (p != null) sb.Append(" [").Append(t.Name).Append(".property ").Append(p.PropertyType.Name)
                                 .Append(p.CanWrite ? " read-write" : " READ-ONLY")
                                 .Append(t.GetField("<" + name + ">k__BackingField", BI) != null ? ", auto-backed"
                                       : t.GetField("_" + name, BI) != null ? ", _-backed" : ", no backing field found")
                                 .Append(']');
            }
            return sb.Length == 0 ? "no member of that name anywhere in the type chain" : sb.ToString();
        }

        public static void TryInstall()
        {
            if (_tried) return;
            _tried = true;
            try
            {
                // Resolve the type by name so the hook can be installed at plugin start, with no
                // character in hand.
                var t = AccessTools.TypeByName("ChaControl");
                var m = t != null ? t.GetMethod("ChangeAlphaMask", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) : null;
                if (m == null) { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: ChaControl.ChangeAlphaMask not found - the body alpha mask cannot be kept in sync, so undressing may leave the torso hidden."); return; }
                if (_harmony == null) _harmony = new Harmony("Clo.LiquidWobbleMPB.bodymaterial");
                _harmony.Patch(m, postfix: new HarmonyMethod(typeof(BodyMaterialFix).GetMethod(nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic)));
                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: hooked ChaControl.ChangeAlphaMask - the body alpha mask follows the game onto ME's material clones.");
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: ChangeAlphaMask hook failed: " + e.Message); }
        }

        private static void Postfix(object __instance)
        {
            try
            {
                var cc = __instance as Component;
                if (cc == null) return;
                if (!_touched.Contains(cc.GetInstanceID())) return;
                // Event-driven, never polled: if a later ME call cloned the body material again, this
                // is the moment the game updates body state, so hand it back before the write is lost.
                AdoptRenderedBodyMaterial(cc, "the game updated the body alpha state");
            }
            catch { }
        }


        // customMatBody / rendBody live on ChaInfo, ChaControl's BASE - and either can be a field or a
        // property depending on game version, so walk the whole chain and accept both (a report
        // ChaControl for a field only, found nothing, and the probe never ran).
        private static T Member<T>(object obj, string name) where T : class
        {
            if (obj == null) return null;
            const BindingFlags BI = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            for (var t = obj.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, BI);
                if (f != null) return f.GetValue(obj) as T;
                var p = t.GetProperty(name, BI);
                if (p != null) return p.GetValue(obj, null) as T;
            }
            return null;
        }

    }

    // Finds BetterPenetration's dan-end marker on a character - the cheapest "does this one have a
    // penis?" test in the plugin. It was the duplicate-marker guard until BP 5.1 fixed that upstream;
    // only the lookup is left.
    internal static class BPDanBones
    {
        private const string BpTypeName = "Core_BetterPenetration.BetterPenetrationController";

        // A male's k_f_dan_end (real penis tip), found under its ChaControl. Shared with the on-load penis-bend
        // re-assert gate so a deliberately hand-FK-posed penis can be left alone.
        public static Transform FindDanEnd(Component chaControl)
        {
            if (chaControl == null) return null;
            foreach (var t in chaControl.GetComponentsInChildren<Transform>(true))
                if (t != null && t.name == "k_f_dan_end") return t;
            return null;
        }
    }

    // Reads Studio's GuideObjectManager (Singleton) to tell whether a Transform is a live guide object - the
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
        private const string FallbackBone = "cf_j_kokan";       // vanilla female crotch bone (lowercase j is intentional - NOT cf_J_Kokan, a different aibu bone); fallback when no character has the BP bone
        // A penis is attached to a womb only if its tip (k_f_dan_end) is within this of penis_target.
        // A real couple sits at ~0.1m and a wrong pairing at ~1.2m, so the old 0.5m rejected the obvious
        // mistake but still claimed any male merely STANDING NEAR her - and the aim link is what drags him
        // to the womb, so "in range" is not a passive judgement, it moves the scene. 0.15m keeps the real
        // couple and leaves a bystander alone.
        private const float  PenisWombRange = 0.15f;
        // KEY PRESS CLAIM. A key press takes the penis whose BASE is nearest her canal mouth: the base is where the
        // character actually is, while his aim node can have been left anywhere by an old link. A base farther
        // than this is not at her at all. A hidden penis counts (the key unhides the one it takes); a female counts
        // only while her penis is shown, since every female carries a hidden one at her crotch.
        private const float  KeyClaimRange = 0.35f;
        private const string PenisBaseBone = "cm_J_dan100_00";
        private const int    HotkeyBuild = 15;                  // bump per plugin build so the log identifies the loaded DLL

        public static bool Enabled { get; set; } = true;
        public static bool Debug { get; set; } = false;
        // World-distance within which a womb's entrance counts as "inside this character's vagina".
        // Baked (the F1 entry was dropped in 1.1). A womb in the vagina sits a few cm from cf_J_Vagina_root; a
        // separately-spawned character is far -> excluded.
        public static float MaxRange = 0.15f;

        // Subscribe once to KKAPI CharacterApi.CharacterReloaded on EVERY loaded copy (the install
        // can have more than one KKAPI assembly; subscribing to all + idempotent apply is safe).
        public static void Init()
        {
            if (_subscribed) return;
            _subscribed = true;
            KKAPI.Chara.CharacterApi.CharacterReloaded += OnCharacterReloaded;
            InstallSceneLoadWatch();
            BodyMaterialFix.TryInstall();   // body-material adoption listens from the start, before any hotkey
            LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: subscribed to CharacterApi.CharacterReloaded.");
        }

        // EventHandler<CharacterReloadedEventArgs> via relaxed binding (param typed as the base EventArgs).
        // Install the two BP-interop Harmony patches (dan-dup guard + penis-FK enforcer) once a CloXray womb is
        // actually present. Both are idempotent (a private _applied guard patches at most once) and BP-lazy (no-op
        // until BP's assembly loads, then they take). Called from the womb's OnEnable, the scene-load penis-bend
        // coroutine, and a womb-gated CharacterReloaded - whichever first sees both a womb and BP. Their prefix/postfix
        // additionally early-out on WombExpandEffect.AnyActive, so removing the last womb makes them inert again.
        internal static void InstallWombHooks()
        {
            // Both guards are safe to run alongside a fixed BetterPenetration: the dan guard skips only a
            // REDUNDANT re-add (whichever side gets there first, the other no-ops) and the FK enforcer acts
            // only while BP is actively driving.
            NodeConstraintBridge.InstallPairingHooks();   // instant womb<->penis re-pair on any k_f_dan_entry NodesConstraint add/enable/disable/delete
            UncBodyReloadWatch.EnsureInstalled();         // an uncensor swap remakes the penis bones -> re-pair (Studio never armed this itself)
        }

        // A scene LOAD and a character REPLACEMENT both arrive as CharacterReloaded, but they need
        // opposite handling: on a load NodesConstraints restores the links the scene was saved with, so
        // anything added is a second copy of one that is still coming; on a replacement the links are
        // gone for good and only this rebuilds them. KKAPI's studio save/load API is what tells the
        // two apart. Without it there is no way to tell, so auto-relinking stays off and the apply hotkey remains
        // the way to re-aim - a wrong guess here is what doubles constraints.
        // A scene LOAD and a character REPLACEMENT both arrive as CharacterReloaded and need opposite
        // handling: on a load NodesConstraints restores the links the scene was saved with, so anything added
        // here duplicates a link that is still coming; on a replacement they are gone for good and only this
        // rebuilds them. The window is bracketed by two real events, no wall clock:
        // SceneCustomFunctionController.OnSceneLoad opens it (KKAPI runs every controller BEFORE raising
        // the event) and StudioSaveLoadApi.SceneLoad closes it - that event is raised from
        // SceneLoadComplete, i.e. when the load has FINISHED, which is also why LoadInProgress is already
        // false by then and is not consulted here.
        private static bool _sceneLoading;
        // A body reload - a card swap, an uncensor change - leaves BetterPenetration re-binding its
        // collision agent and NodesConstraints restoring links for a moment. Reads that depend on either
        // legitimately fail during that window, and the right answer is to wait, not to alarm. Stamped
        // by OnCharacterReloaded; BP's own re-init runs ~1s after the event, so the window covers it.
        private static float _bodyReloadUntil;
        internal static bool BodyReloadSettling { get { return Time.unscaledTime < _bodyReloadUntil; } }
        private static float _sceneLoadOpenedAt;
        private static bool _sceneWatchOk, _sceneWatchTried;
        private const float SceneLoadWatchdog = 30f;   // a load that never completes must not disable re-linking for the session
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

        // Called from WobbleSceneController.OnSceneLoad - the load has started.
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

        // WHICH CHARACTER WEARS THIS WOMB. Studio states the answer outright when the item is parented to
        // her - either physically (workspace parenting reparents the item under the character) or through
        // the workspace tree - so ask that first, and only measure distances for a genuinely free-standing
        // item (every main-game womb, and a Studio item that was never parented). The geometric rung is
        // what once matched the MALE standing against her and forced a female body onto him.
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

        // Which Studio object OWNS this transform? Answers the only question the aim takeover cares
        // about: is this one of HER BONES, or is it a placed object?
        //
        // ancestry is NOT ownership. A sphere parented to a character in the workspace is a
        // child of her root, so GetComponentInParent<ChaControl> finds her and calls the sphere "her
        // bone" - which is how an earlier build stole a hand-made sphere aim. Every Studio object registers its
        // own guideObject.transformTarget, so the owner is the DEEPEST target the transform sits
        // under: her bones resolve to her OCIChar, the sphere resolves to its own OCIItem even while
        // parented to her. Studio types are touched only after the IsStudio guard, matching
        // WearerFromStudioTree - the method JITs in the main game either way, so the guard has to be
        // the first thing in it.
        private static Studio.ObjectCtrlInfo OwningStudioObject(Transform t)
        {
            if (!MainGameWomb.IsStudio || t == null) return null;
            try
            {
                var studio = Studio.Studio.Instance;
                if (studio == null || studio.dicObjectCtrl == null) return null;
                Studio.ObjectCtrlInfo best = null; int bestDepth = -1;
                foreach (var kv in studio.dicObjectCtrl)
                {
                    var oci = kv.Value;
                    var tt = (oci != null && oci.guideObject != null) ? oci.guideObject.transformTarget : null;
                    if (tt == null || !t.IsChildOf(tt)) continue;
                    int d = 0; for (var p = tt; p != null; p = p.parent) d++;
                    if (d > bestDepth) { bestDepth = d; best = oci; }
                }
                return best;
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("AutoBodyReveal: studio-owner lookup failed for '" + t.name + "': " + e.Message); return null; }
        }

        // The transform Studio moves for the object that owns `t` (its guide target). Null outside Studio.
        internal static Transform StudioObjectRoot(Transform t)
        {
            if (!MainGameWomb.IsStudio) return null;
            var oci = OwningStudioObject(t);
            return (oci != null && oci.guideObject != null) ? oci.guideObject.transformTarget : null;
        }

        // Walks the womb item's workspace node upward to the first character node above it.
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

        // The womb this character's penis is parked in, or null. Tolerant about which marker exists:
        // right after a body rebuild the BP dan bones can still be missing, so the vanilla crotch bone
        // counts too. Males only - a female is matched as a WEARER, never as a penetrator.
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

        // The reverse direction: the womb this character wears, decided by the SAME resolver, so a
        // parented item can never be claimed by a nearer unrelated womb.
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
            _bodyReloadUntil = Time.unscaledTime + 5f;   // see BodyReloadSettling
            // A (re)load can change the vagina-root list AND the womb<->penis pairing candidates -> refresh both event-driven.
            WombExpandEffect.InvalidateVaginaRoots();
            MainGameWomb.InvalidateTargetFemale();   // same reason: the H target female is resolved by a cached scene scan
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
                // (ME's own CharacterReloaded controller has no ordering guarantee against this one). Deferred, the saved copy is ADOPTED,
                // saved copy (zero stencil writes - the load-reset fix); only a copy that never appears (a genuinely
                // fresh/swapped-in character) is created. See WobbleSceneController.DeferApply.
                if (cc != null) WobbleSceneController.DeferApply(cc);
            }
            catch (Exception ex) { LiquidWobbleMPBPlugin._logger?.LogWarning("AutoBodyReveal.OnCharacterReloaded: " + ex.Message); }
        }

        // On character load/change: if a CloXray womb sits IN this character's vagina (entrance within
        // MaxRange of cf_J_Vagina_root), (re)apply BodyReveal. Proximity-based, NO in-memory arming - so it
        // survives a scene save/load: load a scene whose char has a womb in the vagina -> applies; change
        // that character -> the womb is still in the (new) vagina -> re-applies. A freshly spawned character
        // lands away from the womb -> not applied (the over-trigger fix).
        // The actual worker - run DEFERRED (via WobbleSceneController.DeferApply) so it executes after ME restore.
        public static void ApplyForCharacterNow(Component cc)
        {
            if (!LiquidWobbleMPBPlugin.CfgEnabled) return;   // master toggle OFF -> never stamp materials
            if (cc == null) return;
            // EVERY Q() below this point resolves against MEBridge.UpgradeStack, and that flag is
            // only raised while the one-key hotkey runs. So an AUTOMATIC pass - a scene load, a character
            // reload - read it as false and re-stamped her garments onto the LEGACY queue, undoing the
            // 2478 that MaterialEditor had just restored correctly, while her body stayed at 2479. The
            // clothes stopped being see-through and the log called it "0 stamped, 0 restenciled", because
            // the counters only track stencils and the queue was changed silently.
            //
            // Her BODY STAMP is the record of which stack she is on - it is the half MaterialEditor
            // persists - so read the flag from her instead of from a transient global. The hotkey still
            // forces it true for the character it is applying to; this only fills it in when nothing has.
            bool hadUpgrade = MEBridge.UpgradeStack;
            if (!hadUpgrade) MEBridge.UpgradeStack = MEBridge.StampIsUpgraded(MEBridge.FindBodyStamp(cc));
            try
            {
            // Two anatomy probes: the BP vagina root AND the vanilla crotch bone (excluding any womb item's own
            // copy). In main-game H the vagina root is NOT pose-animated (idles at the standing position), so the
            // womb must be allowed to match via whichever probe is closer.
            Transform vagina = FindChild(cc.transform, VaginaBone);
            Transform crotch = FindChild(cc.transform, FallbackBone);
            if (vagina == null && crotch == null) { if (Debug) LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: '" + cc.name + "' has neither " + VaginaBone + " nor " + FallbackBone + "; skipping."); return; }

            string how;
            WombExpandEffect best = WombOfWearer(cc, out how);
            if (best == null)
            {
                if (Debug) LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: '" + cc.name + "' wears no womb (not parented to one, none within " + MaxRange.ToString("F2") + "m) -> skip.");
                return;
            }
            // The Studio pair hotkey writes a womb's pair straight onto its materials, which ME does
            // not persist - her body stamp does survive the save, so on load it is the only record of
            // which pair the scene used. Match the womb to it BEFORE the pair is read below.
            StudioPairWomb.LoadSync(cc, best);
            int st = best.OrganStencil();
            // overwriteExisting=false: on LOAD/reload, ensure the reveal exists but NEVER re-stamp an existing copy's
            // stencil - the womb's _StencilBody may not be MaterialEditor-restored yet, so a re-derive would clobber
            // a saved non-default pair (8/12/16) with the stale default 4. The hotkey (ApplyAll) uses true.
            if (MEBridge.EnsureBodyReveal(cc, st, Debug, false))
                best.OnBodyRevealApplied();   // restore out-of-body interior+cum (new-spawn default hides them)
            if (LiquidWobbleMPBPlugin.CfgBodyVeil) MEBridge.EnsureBodyVeil(cc, st + 1, Debug, false);
            // AUTOMATIC (deferred from CharacterReloaded: scene load, character swap) -> correct what the
            // scene recorded, create nothing. See the note in EnsureClothesReveal.
            if (LiquidWobbleMPBPlugin.CfgClothesReveal) MEBridge.EnsureClothesReveal(cc, st, Debug, false);
            if (LiquidWobbleMPBPlugin.CfgClothesReveal && !MainGameWomb.IsStudio) MEBridge.EnsureAccessoryReveal(cc, st, Debug, false);
            // Bracket the apply: with the AFTER REMOVE audit this says whether a missing bodypart was
            // already missing before the character was touched, or appeared across the edit.
            MEBridge.AuditGeometry(cc, "AFTER APPLY");
            if (Debug) MEBridge.DumpMaskState(cc, "AFTER APPLY");

            // Studio: a character load or replacement invalidates the penis links (her half of
            // "vagina -> k_f_dan_entry" dies with her old body), so they have to be re-made. Hand that to
            // the deferred path - NodesConstraints restores/re-binds its own list on its own schedule, and
            // linking before it settles duplicates whatever it is about to restore.
            }
            finally { if (!hadUpgrade) MEBridge.UpgradeStack = false; }
        }

        // The penis links have to be re-made after a character change whatever else happened to that
        // character - in particular when the material carry-over handles the swap, which returns
        // "handled" and skips the proximity apply above. So this is called from the deferred path
        // directly rather than living inside it.
        internal static void StudioRelinkFor(Component cc)
        {
            if (!MainGameWomb.IsStudio || cc == null || !LiquidWobbleMPBPlugin.CfgEnabled || !Enabled) return;
            string how;
            WombExpandEffect best = WombOfWearer(cc, out how);
            if (best != null)
                // Remember the body this scene runs on EVERY time the wearer is seen with her vagina bones -
                // including during a scene load, which is when a loaded scene's wearer is first seen at all.
                CaptureWearer(best, cc);
            else
            {
                // A MALE reload rebuilds his body and with it k_f_dan_entry / k_f_dan_end - the CHILD half
                // of BOTH penis links - so a boy swap needs the same re-link a girl swap does (and the same
                // re-derive of his penis x-ray). The re-link is womb-anchored and resolves the wearer on its
                // own, so all this has to do is pick the womb he is in.
                best = WombPenetratedBy(cc);
                if (best == null) return;
                if (TryRestorePenisUncensor(best, cc)) return;   // reload started; the deferred path re-links
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

        // Records on the womb which body its wearer runs: the had-vagina flag plus her body-uncensor
        // GUID, so a later replacement can put the same body on the incoming card. Called from every
        // path that sees the wearer intact (reloads including scene loads, the settled re-link, the
        // apply hotkey). No-op unless the vagina bones are actually present right now.
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

        // Records the MALE's penis/balls uncensor on the womb while he still has BP's dan bones. Called
        // from every path that has him intact: the penis aim, the constraint poll, the apply hotkey.
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
        // nothing to drive and the womb cannot be penetrated. Put back the exact penis uncensor this
        // scene ran on - carrying the scene's own setup over, not picking one. Once per character; a card
        // that ALREADY has BP dan bones is left alone. True = a reload was started (caller should stop).
        internal static bool TryRestorePenisUncensor(WombExpandEffect w, Component male)
        {
            if (w == null || male == null) return false;
            int id = male.GetInstanceID();
            if (FindChild(male.transform, DanEntryBone) != null) { _bpPenisForced.Remove(id); return false; }
            if (string.IsNullOrEmpty(w.PenetratorPenisGuid)) return false;   // this scene never ran on a BP penis
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

        // Poll-side capture (2s cadence from WobbleSceneController). Reload events cannot do this job on
        // a scene LOAD: the wearer's reload fires BEFORE her uncensor body applies, so the vagina bones
        // are not there yet at event time and no later event comes once they are. The constraint list is
        // the cheap, always-correct source - the restored vagina->entry row names her bone directly.
        // Stops scanning once every womb has both the flag and the GUID (womb components are recreated
        // per scene load, so the poll re-arms itself on the next load).
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
                    // The child of this row is k_f_dan_entry - i.e. the MALE - so one row names both halves.
                    var male = pair.Value != null ? FindChaControlOf(pair.Value) : null;
                    if (male != null) CapturePenetrator(best, male);
                }
            }
        }

        // Runs once NodesConstraints has settled (see WobbleSceneController.DeferNodeRelink).
        // Post-settle entry: find who is wearing the womb NOW (the pre-wait character object is gone).
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
                // The swapped-in card came in on a body with no cf_J_Vagina bones, so the penis has
                // nothing to anchor to. Put back the exact uncensor the previous wearer had - carrying
                // the scene's own setup over, not picking one. Once per character: the reload re-enters
                // here with the bones present. Anything else is left to the Uncensor Selector UI.
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
                // Her body is still the one without vagina bones (the carry-over above either could not
                // run or has not finished). Linking now would anchor the penis to the vanilla crotch bone,
                // which is the wrong bone AND leaves a second entry link behind once the real one appears.
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: '" + cc.name + "' has no " + VaginaBone + " yet — not re-linking the penis (it would anchor to the crotch bone). Press the apply hotkey once her body has loaded.");
                return;
            }
            AutoReapply(w, cc);
            int dupes = NodeConstraintBridge.RemoveDuplicatePairs();
            if (dupes > 0) LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: cleaned " + dupes + " duplicate constraint(s) after the re-link.");
            WombExpandEffect.RequestRepair();   // re-pair the womb to the penis on the current links
            WobbleSceneController.DeferBpRebind(w, cc);   // BP cleared her collision agent on the body reload - re-bind once BP settles
        }

        // Where the penis entry is pinned on the receiver: whichever of her BP entry bones the womb
        // actually sits at. A womb placed in the anal slot anchors to her anal root, not her vagina.
        // Seated in none of them: the womb's own canal mouth. `shownOnly` as in AnchorTakenBy.

        private static Transform EntryAnchorFor(WombExpandEffect w, Component receiver, Transform ourEntry, bool shownOnly)
        {
            if (w == null) return null;
            Vector3 mouth = w.EntranceWorld();
            string wn = w.name;
            // A womb that belongs to nobody - standing on its own in the scene - is still entered at its own mouth.
            // Without an entry anchor the marker stays at his crotch, and the womb then reads the penis as withdrawn
            // (the entry-detach gate) and never reacts at all.
            if (receiver == null)
            {
                Transform ownCanal = w.CanalEntryBone;
                if (ownCanal != null)
                {
                    LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: womb '" + wn
                        + "' is worn by nobody - entering the penis at its own canal mouth.");
                    return ownCanal;
                }
            }

            // HER VAGINA IS THE ONE ORIFICE WORTH LINKING, and only one womb gets it. In this game BetterPenetration
            // animates the vaginal side alone - the penis colliders go into her vagina's dynamic bones, while the
            // anal side has a single dynamic bone most bodies do not carry and both "push" features are compiled
            // out for KK/KKS - and a recognised orifice is also what makes BP report depth at all (it reads the
            // parent bone's name: Vagina, Ana or Mouth). So the womb sitting on her vagina takes the BP link, and
            // every other womb - one at her anus, a second womb in the same vagina, a womb standing on its own - is
            // entered at its OWN canal mouth, where the depth is measured by projecting the penis tip onto the canal
            // instead - the same quantity BP reports, since BP holds the shaft straight between its two markers, so
            // the tip's advance past the entry marker IS its depth. BP still bends and squishes the penis into those
            // exactly as it does here.
            Transform vagina = FindChild(receiver.transform, VaginaBone);
            float vagDist = (vagina != null) ? Vector3.Distance(vagina.position, mouth) : float.MaxValue;
            // HER ANUS IS 4.3cm FROM HER VAGINA, well inside the seat range, so a womb placed at the anus would
            // otherwise pass the test below and take the vaginal link - which is what it did. A womb hanging in
            // her a_n_ana point, or simply sitting nearer her anus than her vagina, is not the vaginal one.
            bool atAnus = IsAnalWomb(w, receiver);
            if (atAnus)
                LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: womb '" + wn + "' sits at '" + receiver.name
                    + "'s anus, not her vagina - entering it at its own canal mouth.");
            if (!atAnus && vagina != null && vagDist <= OrificeSeatRange)
            {
                WombExpandEffect nearest = WombNearestTo(receiver, vagina.position);
                if (nearest != null && nearest != w)
                    LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: womb '" + wn + "' shares '" + receiver.name
                        + "'s vagina with '" + nearest.name + "', which sits closer to it, so this one is entered at its own canal mouth.");
                else if (AnchorTakenBy(vagina, ourEntry, shownOnly))
                    LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: womb '" + wn + "' - her " + VaginaBone
                        + " already holds another " + (shownOnly ? "shown " : "") + "male's entry, so this one is entered at its own canal mouth.");
                else
                {
                    if (Debug) LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: womb '" + wn + "' entry anchor = '"
                        + vagina.name + "' (" + (vagDist * 100f).ToString("F1") + "cm from the womb mouth).");
                    return vagina;
                }
            }

            // Anywhere else: the womb's own canal mouth, so the penis enters the womb where it actually is.
            Transform canal = w.CanalEntryBone;
            if (canal != null)
            {
                LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: womb '" + wn + "' is entered at its own canal mouth"
                    + (vagDist < float.MaxValue ? " (her " + VaginaBone + " is " + (vagDist * 100f).ToString("F1") + "cm away)" : "")
                    + " - BetterPenetration will not classify this one, so the plugin computes his compression itself.");
                return canal;
            }

            // No canal marker = the zipmod does not match this DLL (they ship together). The old
            // behavior silently anchored the penis at her nearest orifice - a fallback, and this
            // project does not do fallbacks: loud error, nothing anchored.
            LiquidWobbleMPBPlugin._logger?.LogError("CloXray: womb '" + wn + "' has NO clo_canal_entry marker - the womb zipmod does not "
                + "match this DLL. Update the zipmod; the penis entry is NOT anchored.");
            return null;
        }

        internal const float OrificeSeatRange = 0.10f;  // beyond this the womb is not sitting on that orifice

        /// Her vagina bone, or her anal one - the two a womb can sit on.
        internal static Transform OrificeBone(Component receiver, bool anal)
        {
            return receiver != null ? FindChild(receiver.transform, anal ? AnalBone : VaginaBone) : null;
        }
        private const string AnalBone = "cf_j_ana";     // KK's own anal bone - BP's AnaTarget, and what a womb at her anus sits on
        private const string AnalSlotName = "a_n_ana";  // Studio's anus accessory point, the anal twin of a_n_kokan

        // Does this womb hang in one of her accessory points by that name? The Studio object's parent IS that
        // point (OCIChar.OnAttach parents the item's guide target to the ChaReference object). When the wearer is
        // known, identify the point by the OBJECT - a_n_ana is a real transform in her rig, and Studio's own
        // AccessoryPointInfo.key is that object's name - and fall back to the parent's name only without her.
        // IsChildOf rather than parent ==, so a womb nested one folder deeper inside the point still counts.
        internal static bool InAccessorySlot(WombExpandEffect w, string key, Component receiver = null)
        {
            Transform item = (w != null) ? StudioObjectRoot(w.transform) : null;
            if (item == null) return false;
            Transform point = (receiver != null) ? FindChild(receiver.transform, key) : null;
            if (point != null) return item != point && item.IsChildOf(point);
            return item.parent != null && item.parent.name == key;
        }

        /// Is this womb PHYSICALLY at her anus rather than her vagina? Her anus is ~4.3cm from cf_J_Vagina_root,
        /// well inside the seat range, so proximity to the vagina alone would call an anal womb vaginal - and it
        /// did. The a_n_ana point is the definite answer when the womb hangs in it; otherwise the womb has to SIT
        /// on her anus - inside the seat range AND nearer it than her vagina.
        /// This answers "which orifice is it on", which is the ENTRY ANCHOR question: a womb an author placed at
        /// her anus by hand must not take her vaginal BP link, however it got there. It is NOT the question the
        /// anal hotkey asks - that one owns the a_n_ana point and nothing else (StudioPairWomb.HerWomb).
        internal static bool IsAnalWomb(WombExpandEffect w, Component receiver)
        {
            if (w == null) return false;
            if (InAccessorySlot(w, AnalSlotName, receiver)) return true;
            if (receiver == null || InAccessorySlot(w, "a_n_kokan", receiver)) return false;
            Transform anus = FindChild(receiver.transform, AnalBone);
            if (anus == null) return false;   // no anal bone on this body - only the slot can say, and it did not
            Vector3 mouth = w.EntranceWorld();
            float anusDist = Vector3.Distance(anus.position, mouth);
            if (anusDist > OrificeSeatRange) return false;
            Transform vagina = FindChild(receiver.transform, VaginaBone);
            return vagina == null || anusDist < Vector3.Distance(vagina.position, mouth);
        }

        // A withdrawn test build pinned a womb's OWN entry link 5mm toward the womb's back with a NodesConstraints
        // offset, because the rings open further to the back than the closed mouth the marker sits on. The shipped
        // answer moves the MARKER instead - the womb bundle places clo_canal_entry 4mm back - and leaves every link
        // at ZERO offset. A scene saved by that test build still carries the offset on its link, so the key
        // recognises exactly it and puts it back to zero; any other offset on the link is deliberate and stays.
        private const float WithdrawnTestBackMM = 5f;

        private static bool IsWithdrawnTestOffset(WombExpandEffect w, Transform canal, Vector3 localOffset)
        {
            if (w == null || canal == null) return false;
            Vector3 axis = canal.up;
            Vector3 back = -w.transform.forward;
            back -= Vector3.Dot(back, axis) * axis;
            if (back.sqrMagnitude < 1e-6f) return false;
            Vector3 test = canal.InverseTransformVector(back.normalized * (WithdrawnTestBackMM * 0.001f * Mathf.Abs(w.transform.lossyScale.z)));
            return canal.TransformVector(localOffset - test).magnitude < 0.0005f;
        }

        // Of the wombs this character wears, the one whose canal mouth is nearest a point - used to tell which of
        // several wombs in the same vagina sits at the default placement, since that one takes the BP link.
        private static WombExpandEffect WombNearestTo(Component receiver, Vector3 point)
        {
            WombExpandEffect best = null; float bestD = OrificeSeatRange;
            foreach (var w in UnityEngine.Object.FindObjectsOfType<WombExpandEffect>())
            {
                if (w == null) continue;
                string how;
                if (!ReferenceEquals(ResolveWearer(w, out how), receiver)) continue;
                float d = Vector3.Distance(point, w.EntranceWorld());
                if (d <= bestD) { bestD = d; best = w; }
            }
            return best;
        }

        // Is this bone already driving a DIFFERENT penis's entry? With `shownOnly` (a key press) a hidden
        // character's link does not count: a scene that swaps which male is shown links them all to one orifice.
        private static bool AnchorTakenBy(Transform anchor, Transform ourEntry, bool shownOnly)
        {
            foreach (var pair in NodeConstraintBridge.LivePairs())
            {
                if (pair.Key != anchor || pair.Value == null || pair.Value.name != DanEntryBone || pair.Value == ourEntry) continue;
                if (shownOnly)
                {
                    Component other = FindChaControlOf(pair.Value);
                    if (other != null && !IsCharacterShown(other)) continue;
                }
                return true;
            }
            return false;
        }

        // Is a penetrating penis actually parked in this womb? Gates the uncensor force to real couples -
        // a display womb with nobody in it never triggers a body change.
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
            float gap;
            Transform end = NearestPenisEnd(UnityEngine.Object.FindObjectsOfType<Transform>(), target.position, receiver, out penetrator, out gap);
            return (penetrator != null && end != null && Vector3.Distance(end.position, target.position) <= PenisWombRange) ? penetrator : null;
        }

        // Characters already given a BP body after a replacement. Cleared as soon as the
        // character is seen with vagina bones, so a later swap can force again; while set, it stops the
        // force/reload cycle from repeating on a card that has no BP uncensor available.
        private static readonly System.Collections.Generic.HashSet<int> _bpBodyForced = new System.Collections.Generic.HashSet<int>();

        // Full x-ray stamp of one character for one womb. overwriteExisting=true: callers are explicit
        // actions (the hotkey, or the post-uncensor restore whose body rebuild just destroyed the old
        // copies) - sync the body/veil stencil to the womb's CURRENT pair.
        internal static void StampWombChar(WombExpandEffect w, Component cc, bool allowCreate)
        {
            if (w == null || cc == null) return;
            if (Debug) MEBridge.DumpBodyState(cc, "pre-stamp");   // material-state dump: diagnostics only
            CaptureWearer(w, cc);   // remember her body for a later character replacement
            int st = w.OrganStencil();
            if (MEBridge.EnsureBodyReveal(cc, st, true, true))
                w.OnBodyRevealApplied();   // restore out-of-body interior+cum (new-spawn default hides them)
            if (LiquidWobbleMPBPlugin.CfgBodyVeil) MEBridge.EnsureBodyVeil(cc, st + 1, true, true);
            if (LiquidWobbleMPBPlugin.CfgClothesReveal) MEBridge.EnsureClothesReveal(cc, st, true, allowCreate);
            if (LiquidWobbleMPBPlugin.CfgClothesReveal && !MainGameWomb.IsStudio) MEBridge.EnsureAccessoryReveal(cc, st, true, allowCreate);   // Free-H: accessories dress the card - stamp them too
            // the vanishing-garment report happens right after the HOTKEY, and this path never
            // audited - only the load path did. The audit now also detects the invisible-pairing case.
            MEBridge.AuditGeometry(cc, "AFTER HOTKEY APPLY");
        }

        internal static bool HasVaginaBone(Component cc) { return cc != null && FindChild(cc.transform, VaginaBone) != null; }

        // Re-entry after the body-uncensor carry-over. The uncensor rebuild runs inside UncensorSelector's
        // own coroutine, NOT through ChaControl.Reload, so no KKAPI reload event follows it - this is the
        // only path that re-stamps the x-ray (the rebuild destroyed the old body's material copies) and
        // re-makes the penis links on the restored body.
        internal static void PostUncensorApply(WombExpandEffect w, Component cc)
        {
            if (w == null || cc == null || !LiquidWobbleMPBPlugin.CfgEnabled) return;
            if (SceneLoading) { LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: a scene load started during the uncensor restore - leaving it to the load."); return; }
            LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: '" + cc.name + "' body reloaded with the scene's uncensor - re-stamping the x-ray and re-making the penis links.");
            StampWombChar(w, cc, false);   // body reloaded underneath - not a request to stamp new garments
            int dead = NodeConstraintBridge.RemoveDeadConstraints();
            if (dead > 0) LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: cleared " + dead + " constraint(s) left pointing at the replaced body.");
            AutoReapply(w, cc);
            int dupes = NodeConstraintBridge.RemoveDuplicatePairs();
            if (dupes > 0) LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: cleaned " + dupes + " duplicate constraint(s) after the re-link.");
            WombExpandEffect.RequestRepair();   // re-pair the womb to the penis on the current links
            WobbleSceneController.DeferBpRebind(w, cc);   // BP cleared her collision agent on the body reload - re-bind once BP settles
        }

        /// Her stencil pair moved, and it is HER BODY's - so every womb she wears carries it, not just the one the
        /// key was setting up. SetPairAll has already written the pair onto each of them; this brings along what
        /// hangs off it: each womb's own stack, and the x-ray on the penis that womb already holds. No claiming and
        /// no links are touched - the key sets up ONE hole, the rest only need their stencil to keep matching her.
        internal static void SyncPairAcross(Component girl, WombExpandEffect except)
        {
            if (girl == null) return;
            bool hadUpgrade = MEBridge.UpgradeStack;
            if (!hadUpgrade) MEBridge.UpgradeStack = MEBridge.StampIsUpgraded(MEBridge.FindBodyStamp(girl));
            try
            {
                foreach (var w in UnityEngine.Object.FindObjectsOfType<WombExpandEffect>())
                {
                    if (w == null || w == except) continue;
                    string how;
                    if (!ReferenceEquals(ResolveWearer(w, out how), girl)) continue;
                    SyncWombStack(w, girl, false);
                    Transform target = FindChild(w.transform, "penis_target");
                    if (target == null) continue;
                    int st = w.OrganStencil();
                    foreach (var pair in NodeConstraintBridge.LivePairs())
                    {
                        if (pair.Key != target || pair.Value == null || pair.Value.name != "k_f_dan_end") continue;
                        Component male = FindChaControlOf(pair.Value);
                        if (male == null) continue;
                        MEBridge.EnsurePenisOrgInside(male, st, true);
                        LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: womb '" + w.name + "' moved to stencil "
                            + st + " with '" + girl.name + "' - re-stamped '" + male.name + "'s penis to match.");
                    }
                }
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("AutoBodyReveal: carrying the pair across her other wombs failed: " + e.Message); }
            finally { if (!hadUpgrade) MEBridge.UpgradeStack = false; }
        }

        // Manual hotkey: apply now to every character that has a womb within MaxRange of its vagina
        // (covers the initial placement, where no reload event fires).
        /// `onlyWomb` narrows the pass to that one womb, `onlyFor` to the wombs worn by that character - the pair
        /// keys work on one womb of the selected girl and leave every other couple in the scene exactly as it is,
        /// so a scene is set up one hole at a time. Null for both applies to every womb (the plain apply key).
        /// `claimByTip` picks the penis whose TIP is nearest this womb's aim bone instead of the one whose base is
        /// nearest her canal mouth: for a womb that is not on one of her orifices, where he is pointed is the only
        /// thing that says which penis is meant for it.
        public static void ApplyAll(Component onlyFor = null, WombExpandEffect onlyWomb = null, bool claimByTip = false)
        {
            if (!LiquidWobbleMPBPlugin.CfgEnabled) return;   // master toggle OFF -> hotkey does nothing
            AttachLiquidWobbleSelected();      // bottles etc.: attach the wobble driver to the SELECTED item(s) only
            var wombs = UnityEngine.Object.FindObjectsOfType<WombExpandEffect>();
            LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: manual apply — " + wombs.Length + " womb(s)"
                + (onlyFor != null ? ", only those worn by '" + onlyFor.name + "'" : "") + ", MaxRange=" + MaxRange.ToString("F3") + "m.");
            if (wombs.Length == 0) { LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: manual apply - no CloXray wombs in scene."); return; }
            if (Debug) NodeConstraintBridge.DumpConstraints("constraints BEFORE apply");
            // NEAREST FIRST. A penis can only be claimed once (the constraint guard refuses a second
            // claim on the same k_f_dan_end), so with several wombs in a scene the one processed first
            // wins - and the object scan order is arbitrary. Ordering by how close each womb's aim bone
            // is to its nearest penis makes the closest womb claim it and the rest fall through.
            var scan = UnityEngine.Object.FindObjectsOfType<Transform>();
            var order = new System.Collections.Generic.List<KeyValuePair<float, WombExpandEffect>>();
            foreach (var w0 in wombs)
            {
                if (w0 == null) continue;
                if (onlyWomb != null && w0 != onlyWomb) continue;
                if (onlyWomb == null && onlyFor != null)
                {
                    string howSel;
                    if (!ReferenceEquals(ResolveWearer(w0, out howSel), onlyFor)) continue;
                }
                order.Add(new KeyValuePair<float, WombExpandEffect>(NearestPenisDistance(w0, scan), w0));
            }
            order.Sort((x, y) => x.Key.CompareTo(y.Key));
            if (onlyFor != null && order.Count == 0)
                LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: manual apply - no womb is worn by '" + onlyFor.name + "'.");
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
                    StampWombChar(w, cc, true);   // hotkey
                }
                else LiquidWobbleMPBPlugin._logger?.LogWarning("AutoBodyReveal: manual apply - womb '" + w.name + "' has no wearer (not parented to a character, and no " + VaginaBone + "/" + FallbackBone + " within " + MaxRange.ToString("F2") + "m).");
                // AUTO penis: x-ray + aim the PENETRATOR (the OTHER character that has a penis) at THIS womb. No
                // selection needed, so it can't grab the receiver's own penis or duplicate across both partners.
                SyncWombStack(w, cc, false);
                ApplyPenisForWomb(w, cc, true, claimByTip);   // hotkey = an explicit request to wire this pair up
            }
            // BP learns "this is vaginal" only when a k_f_dan_entry constraint is ADDED. Links the scene already
            // carried were restored before BP existed, and the load-time re-arm skipped her if she had no womb
            // then - so without this a womb added now reads no depth for the whole session.
            System.Func<Component, bool> wearsWomb = her => { string how; return her != null && WombOfWearer(her, out how) != null; };
            int armed = BPBridge.ReArmVaginalTargets(wearsWomb);
            if (armed > 0)
                LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: re-armed BetterPenetration on " + armed + " male(s) for the womb(s) just applied.");
            WombExpandEffect.RequestRepair();   // the hotkey may have added/aimed NC links -> re-pair every womb to its penis now
        }

        // Distance from this womb's aim bone to the nearest penis that could claim it (its own wearer
        // excluded). float.MaxValue when there is none.
        private static float NearestPenisDistance(WombExpandEffect w, Transform[] scan)
        {
            if (w == null) return float.MaxValue;
            Transform target = FindChild(w.transform, "penis_target");
            if (target == null) return float.MaxValue;
            string how;
            Component wearer = ResolveWearer(w, out how);
            Component pen; float gap;
            Transform end = NearestPenisEnd(scan, target.position, wearer, out pen, out gap, target, w.EntranceWorld(), true, w.CanalEntryBone);
            return (pen != null && end != null) ? gap : float.MaxValue;
        }

        // MAIN-GAME direct path: the womb was spawned ON a known character, so skip the proximity
        // search entirely (H characters carry a second, un-animated proxy skeleton whose bones sit at
        // the standing pose - proximity matching against bone names is unreliable there). Stamps the
        // full stack on the given wearer and x-rays the penetrating male, same as the Studio hotkey.
        // Mirror of ApplyForWomb, for the main game's womb toggle: the womb is going away, so the
        // x-ray it justified goes with it. Without this the penis keeps its OrgInside copy and stays
        // visible through her body after the womb is gone. Studio is deliberately excluded - there the
        // edits live in the scene and are the author's to keep.
        public static void RemoveForWomb(Component receiver)
        {
            if (MainGameWomb.IsStudio || receiver == null) return;
            MEBridge.RemoveXrayCopies(receiver, Debug);
            // Her partner's penis copy only goes if no other womb is left in the scene - with a second
            // couple still set up he may be that womb's penetrator too.
            if (MainGameWomb.AnySpawned()) return;
            Component male = MainGameWomb.FindNearestMaleWithPenis(receiver.transform.position, 2f, receiver);
            if (male != null) MEBridge.RemoveXrayCopies(male, Debug);
        }

        public static void ApplyForWomb(WombExpandEffect w, Component cc)
        {
            if (!LiquidWobbleMPBPlugin.CfgEnabled || w == null || cc == null) return;
            // FREE-H IS ALWAYS ON THE SUB-2500 STACK. The opt-in exists for Studio only: a Studio scene
            // persists render queues through Material Editor, so moving the shipped default would leave
            // every saved scene half-converted. The main game has no scene file carrying them, so there
            // is nothing to protect - and no reason to leave the in-window penis unshaded there.
            bool freeH = !MainGameWomb.IsStudio;
            if (freeH) MEBridge.UpgradeStack = true;
            try
            {
            int st = w.OrganStencil();
            LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: direct apply — womb '" + w.name + "' on '" + cc.name + "' stencil=" + st + ".");
            BodyMaterialFix.MarkTouched(cc);   // her body materials get cloned by ME too - keep her mask following the game
            if (MEBridge.EnsureBodyReveal(cc, st, Debug, true))
                w.OnBodyRevealApplied();   // restore out-of-body interior+cum (new-spawn default hides them)
            if (LiquidWobbleMPBPlugin.CfgBodyVeil) MEBridge.EnsureBodyVeil(cc, st + 1, Debug, true);
            // A womb was spawned or apply was pressed - creating is exactly what was asked for.
            if (LiquidWobbleMPBPlugin.CfgClothesReveal) MEBridge.EnsureClothesReveal(cc, st, Debug, true);
            if (LiquidWobbleMPBPlugin.CfgClothesReveal && !MainGameWomb.IsStudio) MEBridge.EnsureAccessoryReveal(cc, st, Debug, true);
            if (MainGameWomb.IsStudio)
            {
                AutoReapply(w, cc);
            }
            else
            {
                // MAIN GAME: BP's k_f_dan_end marker does NOT track the visible penis there (it idles
                // ~0.7m off even mid-insertion), so the Studio marker-distance gate always rejects.
                // The H male IS the penetrator - convert his penis directly, no marker, no aiming (BP
                // aims natively in the main game).
                Component male = MainGameWomb.FindNearestMaleWithPenis(w.transform.position, 2f, cc);
                if (male != null)
                {
                    // these ME calls make ME clone his materials (renderer.materials), which orphans
                    // the game's body material - register him so the clothing alpha mask keeps
                    // following the game, and repair right away in case he is already undressed.
                    BodyMaterialFix.MarkTouched(male);
                    MEBridge.EnsurePenisOrgInside(male, st, Debug, LiquidWobbleMPBPlugin.CfgHPenisOutside,
                                                  LiquidWobbleMPBPlugin.CfgHPenisBottomWindow ? 1f : 0f);
                    MEBridge.EnsureBallsStamp(male, st, Debug);   // occluder stamp - balls block the window like a hand, plain geometry everywhere
                    BodyMaterialFix.AdoptRenderedBodyMaterial(male, "right after our apply");
                    KPlugBridge.ReassertDickMaterials(male);          // the CloXray copies shift the indices KPlug addresses by
                    // KPlug INSTALLED, not KPlug currently-owning. Core.useCustomDick only turns true the
                    // first time KPlug configures a dick, and on some setups this apply runs BEFORE that -
                    // a reporter's log showed the bridge bound and hooked but zero captures, so nothing was
                    // ever restored and he got the raw interaction. Capturing early is harmless: it is
                    // first-write-wins and the restore only fires if KPlug later rewrites the materials.
                    if (KPlugBridge.Present)
                        MEBridge.CapturePenisLook(male, Debug);
                    MainGameWomb.AttachPenisAim(w, male, cc);   // pin BP's inner limit at the womb's penis_target
                    SyncWombStack(w, cc, false);   // the womb item's own materials follow whichever stack she is on
                    LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: main-game penis x-ray on '" + male.name + "' (stencil " + st + ").");
                    if (Debug) MEBridge.DumpXrayChain(male, cc, w);   // KK-vs-KKS chain diff (penis invisible in KKS womb)
                }
                else LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: no male with a penis material within 2m of the womb — penis not x-rayed.");
            }
            // Everything this character will draw has now been written, so the stack is complete and
            // can be checked as a whole. Inside the try, before the flag is restored - the assertion
            // reads material queues, not the flag, so it sees what the frame will actually do.
            MEBridge.AssertStackOrder(cc, w, "hotkey apply");
            }
            finally { if (freeH) MEBridge.UpgradeStack = false; }
        }

        // Re-apply ONLY the penis x-ray copies for a given womb+male: a body reload (the BP5
        // uncensor force, or an uncensor/outfit change) destroys the penis renderer's instanced
        // .MECopy x-ray materials, so BP drives the new penis correctly but it renders INVISIBLE in
        // the womb (KKS: the default penis uncensor isn't BP, so the force ALWAYS reloads -> the
        // penis was always invisible there). Called from the pin's agent watchdog, which already
        // fires on exactly this reload ("BP re-created its agents"). Idempotent: EnsurePenisOrgInside
        // adopts existing copies, recreates only missing ones.
        private static bool _kplugSkipLogged;
        public static void ReapplyMainGamePenisXray(WombExpandEffect w, Component male)
        {
            if (w == null || male == null || MainGameWomb.IsStudio) return;
            if (!LiquidWobbleMPBPlugin.CfgEnabled) return;
            // HIS OWN MATERIAL EDITS FIRST. The reload rebuilds the penis material from the uncensor
            // definition, which carries a stock shader - a reporter's KKUTS penis came back as
            // Shader Forge/main_skin, and a KKUTS-authored material on the stock shader renders WHITE.
            // MaterialEditor has no handler for this partial reload (its re-apply hooks are KKAPI-level),
            // so nothing restores it. Same defect as on her body; the penis lives under
            // ObjectType.Character too, so the same body/face re-apply covers it. Before these copies,
            // so ours are stamped onto the restored material rather than the stock one.
            // ...unless KPlug owns his dick. Its shader and the _MainTex OFFSET that goes with it are one
            // package; restoring only the shader leaves KKUTS sampling at KPlug's ball offset (0, 0.5),
            // which is white outside the body and correct through the window - the exact reported fault.
            // KPlug already keeps his penis looking right on its own, so there is nothing here to repair.
            BodyMaterialFix.MarkTouched(male);
            if (!KPlugBridge.Present) MEBridge.RefreshBodyEdits(male);
            // RefreshBodyEdits runs ME's whole body load path, which clones his materials again.
            BodyMaterialFix.AdoptRenderedBodyMaterial(male, "after our re-apply");
            if (KPlugBridge.Present && !_kplugSkipLogged)
            {
                _kplugSkipLogged = true;
                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: KPlug owns this male's penis materials — not restoring his own shader over them. "
                    + "Shader and texture offset are one package there; changing half of it is what rendered the penis white.");
            }
            int st = w.OrganStencil();
            MEBridge.EnsurePenisOrgInside(male, st, Debug, LiquidWobbleMPBPlugin.CfgHPenisOutside,
                                          LiquidWobbleMPBPlugin.CfgHPenisBottomWindow ? 1f : 0f);
            MEBridge.EnsureBallsStamp(male, st, Debug);   // occluder stamp - balls block the window like a hand, plain geometry everywhere
            KPlugBridge.ReassertDickMaterials(male);          // the CloXray copies shift the indices KPlug addresses by
            LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: penis x-ray RE-APPLIED on '" + male.name + "' after a body reload (stencil " + st + ") — the reload wiped the instanced copies.");
            if (Debug) MEBridge.DumpXrayChain(male, null, w);
        }

        // Same story for HER body. The BP5 body-uncensor force reloads the body mesh, which wipes the
        // instanced cf_m_body .MECopy x-ray materials - so the window closes, and because a Free-H womb
        // is hidden out-of-body it disappears with it. That is the "the first hotkey press did nothing"
        // report: the press worked, then the forced reload undid it. Idempotent, same as the penis.
        public static void ReapplyMainGameBodyXray(WombExpandEffect w, Component female)
        {
            if (w == null || female == null || MainGameWomb.IsStudio) return;
            if (!LiquidWobbleMPBPlugin.CfgEnabled) return;
            MEBridge.RefreshBodyEdits(female);   // her own skin shader, same reason as the penis above
            int st = w.OrganStencil();
            if (MEBridge.EnsureBodyReveal(female, st, Debug, true))
                w.OnBodyRevealApplied();
            if (LiquidWobbleMPBPlugin.CfgBodyVeil) MEBridge.EnsureBodyVeil(female, st + 1, Debug, true);
            if (LiquidWobbleMPBPlugin.CfgClothesReveal)
            {
                // Free-H re-apply after her body was rebuilt: reproduce what she had, so creating is
                // correct here - the copies were lost with the old mesh, not deliberately removed.
                MEBridge.EnsureClothesReveal(female, st, Debug, true);
                MEBridge.EnsureAccessoryReveal(female, st, Debug, true);
            }
            LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: body x-ray RE-APPLIED on '" + female.name
                + "' after a body reload (stencil " + st + ") — the reload wiped the instanced copies.");
            // This method is where the mix came from: it rewrote the garment queues with UpgradeStack
            // false while her body and the womb were upgraded. Check the whole stack right after it.
            MEBridge.AssertStackOrder(female, w, "Free-H re-apply after a body reload");
            // The womb goes invisible about half a second AFTER a successful hotkey press, and this is the
            // only thing that runs in between. This re-apply repairs HER (body, veil, clothes, accessories)
            // but never touches the womb item's own materials - the hotkey path calls SyncWombStack, this
            // one does not. Dump the chain here so the womb's queues and _StencilBody at THIS instant can
            // be compared against the hotkey's, which settles whether the womb's own materials moved or
            // whether the window closed on her side.
            if (Debug) MEBridge.DumpXrayChain(null, female, w);
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

        // Hotkey path: attach the wobble to the SELECTED Studio item(s) only - no scene-wide scan. The
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
        // (OTHER than the womb's owner `receiver`) that carries a real penis (cm_m_dankon) - fully automatic, no
        // selection, so it can't grab the receiver's OWN penis (both partners may be cm_m_dankon) or duplicate. The
        // penis -> CloXray/OrgInside (stencil matched to this womb) + two position NodesConstraints: k_f_dan_entry ->
        // the receiver's vagina, k_f_dan_end -> THIS womb's penis_target. Idempotent (dedup by child) + scene-persistent.
        // The womb ITEM's own materials carry their queue baked into the zipmod, and no Material Editor
        // record - so they cannot be upgraded the way a character copy is. They are DERIVED instead:
        // whichever stack HER body stamp is on, the womb follows. Her stamp queue is persisted by ME, so
        // the marker survives a save, and this runs after ME's restore (the apply is deferred for exactly
        // that reason), which is what makes reading it safe.
        // Direct writes, no ME: these are CloXray's own item materials, never a hand edit.
        // ── THE WOMB ITEM'S SAVED STATE ───────────────────────────────────────────────────────────
        // A womb carries two pieces of state that are NOT MaterialEditor's: which stencil pair it is on
        // and which render-queue stack it follows. Both were written straight onto the live
        // materials and never saved, so every scene load reverted the item to its shipped default
        // (_StencilBody 4 / pair 1, and the queue tags baked into the shaders). Couples already on pair 1
        // survived that by luck; a couple moved to pair 2 came back with her body on 8 and her womb on 4,
        // and the penis vanished from the window. Found 2026-08-27 on a crowded scene, where it hit
        // exactly one womb - the only one not on pair 1.
        //
        // It is stored in CloXray's own scene data (WobbleSceneController), not through ME: ME's item
        // controller is not reachable by a stable type name across its builds, and this is CloXray's state
        // rather than a material edit.
        internal static int WombQueueFor(string shaderName, bool upgraded)
        {
            switch (shaderName)
            {
                case "CloXray/Organ":     return upgraded ? 2482 : 3500;
                case "CloXray/OrgInside": return upgraded ? 2484 : 3502;
                case "CloXray/Liquid":    return upgraded ? 2485 : 3503;
            }
            return -1;
        }

        /// Read back what a womb is currently on, so it can be saved. stencil = -1 when it carries none.
        internal static void ReadWombState(WombExpandEffect w, out int stencil, out bool upgraded)
        {
            stencil = -1; upgraded = false;
            if (w == null) return;
            foreach (var r in w.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || r.sharedMaterials == null) continue;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || m.shader == null) continue;
                    if (stencil < 0 && m.HasProperty("_StencilBody")
                        && (m.shader.name == "CloXray/Organ" || m.shader.name == "CloXray/OrgInside"))
                        stencil = Mathf.RoundToInt(m.GetFloat("_StencilBody"));
                    int qUp = WombQueueFor(m.shader.name, true);
                    if (qUp > 0 && m.renderQueue == qUp) upgraded = true;
                }
            }
        }

        // CloXray_Organ and CloXray_OrgInside both declare _StencilBody = 4, so a womb sitting on 4 is a
        // womb nothing has spoken for. Any other value came from somewhere - MaterialEditor restoring what
        // the scene saved, or an earlier hotkey - and is an answer, not an absence.
        internal const int ShippedStencilBody = 4;

        /// Put a womb on a pair and a stack. Writes only what differs, so a correct womb costs nothing.
        /// Mirrors the two call sites it replaced: stencil on INSTANCE materials (ME's sliders point at
        /// those), queues on the shared ones.
        internal static int ApplyWombState(WombExpandEffect w, int stencil, bool upgraded)
        {
            return ApplyWombState(w, stencil, upgraded, false);
        }

        /// adoptOnly is the LOAD path for a scene that carries no record of its own. Whatever that scene
        /// already determined IS the answer and may not be overwritten. Two things determine it, and
        /// neither of them is nothing: MaterialEditor persists the womb's _StencilBody per material and
        /// restores it before this runs, and the queues come back on the tags baked into the shaders. So
        /// this writes ONLY where a material is still on its SHIPPED default - which is exactly the case
        /// the repair was written for, a womb that really did come back on pair 1 while she is on
        /// another - and it writes no queue at all.
        ///
        /// The queues are left alone deliberately. The legacy runtime layout is 3500/3502/3503, one step
        /// up from the shipped tags 3500/3501/3502, so that the womb's own OrgInside clears the slot the
        /// penis carve inherits (PenisCarveQueue 3501 IS the OrgInside tag - a copy carries it with no
        /// record of its own). That step-off has always been the hotkey's job. Doing it during a load
        /// re-stacks a scene that was authored, tuned and saved without it, which changes how it looks
        /// for a reason its author never asked for.
        internal static int ApplyWombState(WombExpandEffect w, int stencil, bool upgraded, bool adoptOnly)
        {
            if (w == null) return 0;
            int changed = 0, kept = 0;
            var seen = LiquidWobbleMPBPlugin.CfgDebugLog ? new System.Text.StringBuilder() : null;
            foreach (var r in w.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || r.sharedMaterials == null) continue;
                // What the scene handed over, before anything here touches it. Printed for every womb
                // material this method can write, so a value that would have been re-derived is visible
                // beside the one that was actually restored.
                if (seen != null)
                    foreach (var sm in r.sharedMaterials)
                    {
                        if (sm == null || sm.shader == null || WombQueueFor(sm.shader.name, false) < 0) continue;
                        seen.Append("\n    ").Append(MEBridge.BaseName(sm))
                            .Append(" [").Append(sm.shader.name).Append("]  queue=").Append(sm.renderQueue)
                            .Append(sm.HasProperty("_StencilBody")
                                    ? "  stencil=" + Mathf.RoundToInt(sm.GetFloat("_StencilBody")).ToString() : "");
                    }
                bool relevant = false;
                foreach (var sm in r.sharedMaterials)
                    if (sm != null && sm.shader != null
                        && (sm.shader.name == "CloXray/Organ" || sm.shader.name == "CloXray/OrgInside")) { relevant = true; break; }
                if (stencil >= 0 && relevant)
                {
                    foreach (var m in r.materials)
                    {
                        if (m == null || m.shader == null) continue;
                        if (m.shader.name != "CloXray/Organ" && m.shader.name != "CloXray/OrgInside") continue;
                        if (!m.HasProperty("_StencilBody")) continue;
                        int live = Mathf.RoundToInt(m.GetFloat("_StencilBody"));
                        // The scene spoke for this material. Leave the whole trio alone - _Plus_1 and
                        // _ProtectRef belong to the same choice, and rewriting them would split the pair.
                        if (adoptOnly && live != ShippedStencilBody) { kept++; continue; }
                        bool moved = live != stencil;
                        if (moved) { m.SetFloat("_StencilBody", stencil); changed++; }
                        // The companions follow the pair only when the pair actually moved. They can be
                        // authored on their own, so writing them into a material that was already right
                        // is the same overwrite in a smaller place - and it made "wrote 0" untrue. The
                        // hotkey (adoptOnly false) still restates the whole trio, which is its job.
                        if (moved || !adoptOnly)
                        {
                            if (m.HasProperty("_StencilBody_Plus_1")) m.SetFloat("_StencilBody_Plus_1", stencil + 1);
                            if (m.HasProperty("_ProtectRef"))         m.SetFloat("_ProtectRef", 32 + stencil + 1);
                        }
                    }
                }
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || m.shader == null) continue;
                    int want = WombQueueFor(m.shader.name, upgraded);
                    if (want < 0 || m.renderQueue == want) continue;
                    if (adoptOnly)
                    {
                        // A CROSS-STACK MIX is the only queue fault worth correcting on its own. The two
                        // stacks are all-or-nothing: a womb sitting on the sub-2500 numbers while she is
                        // on the 3500s (or the reverse) makes a layer draw on the wrong side of her body
                        // stamp and vanish, and nobody chose that. ANY OTHER number was chosen - a scene
                        // can carry a different queue per womb, deliberately, so that several overlapping
                        // wombs layer the way its author wanted (measured: 3503/3504, 3504/3505,
                        // 3502/3507 across three wombs of one scene). Flattening those to the canonical
                        // set is what changed a scene that had been right for months.
                        if (m.renderQueue != WombQueueFor(m.shader.name, !upgraded)) continue;
                        LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: womb '" + w.name + "' material '"
                            + MEBridge.BaseName(m) + "' sits on the " + (upgraded ? "legacy" : "upgraded")
                            + " stack at " + m.renderQueue + " while she is on the other one - that mix hides a"
                            + " layer, so it moves to " + want + ".");
                    }
                    m.renderQueue = want; changed++;
                }
            }
            if (seen != null && seen.Length > 0)
                LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: SAVED-STATE '" + w.name + "' as the scene restored it"
                    + (adoptOnly ? " (no record of ours - adopting)" : "") + ":" + seen
                    + "\n    -> kept " + kept + ", wrote " + changed
                    + (adoptOnly ? "; queues left as the scene had them." : "."));
            return changed;
        }

        /// automatic = anything no key was pressed for: a character replacement that settled, an
        /// uncensor body reload, the deferred re-apply after a load. Those run on a scene nobody is
        /// editing, so they follow the same rule the load path does - correct a cross-stack MIX, which
        /// hides a layer and nobody chose, and leave every other queue alone. The hotkey still normalises,
        /// because pressing it IS the request to re-stack.
        private static void SyncWombStack(WombExpandEffect w, Component receiver, bool automatic)
        {
            if (w == null || receiver == null) return;
            var stamp = MEBridge.FindBodyStamp(receiver);
            if (stamp == null) return;                       // no x-ray on her yet - nothing to follow
            bool up = MEBridge.StampIsUpgraded(stamp);
            int moved = ApplyWombState(w, -1, up, automatic);   // -1 = queues only; the pair is StudioPairWomb's to set
            if (moved > 0)
                LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: womb '" + w.name + "' -> "
                    + (up ? "UPGRADED" : "legacy") + " stack (" + moved + " material(s)), following '" + receiver.name + "'.");
        }

        // The AUTOMATIC re-apply, for a womb that already exists: a character replacement that has
        // settled, an uncensor body reload that has finished, and a Studio womb spawn.
        // NOT a scene load - that runs ApplyForCharacterNow instead and never reaches here.
        // Nor the hotkey: ApplyAll calls SyncWombStack and ApplyPenisForWomb directly.
        private static void AutoReapply(WombExpandEffect w, Component cc)
        {
            SyncWombStack(w, cc, true);   // no key was pressed: fix a cross-stack mix, touch nothing else
            ApplyPenisForWomb(w, cc);
        }


        // The Studio object for this character, or null. Same dicObjectCtrl walk as the wearer
        // lookup, matched on charInfo rather than on the workspace tree.
        private static Studio.OCIChar OciCharFor(Component cc)
        {
            if (!MainGameWomb.IsStudio || cc == null) return null;
            try
            {
                var studio = Studio.Studio.Instance;
                if (studio == null || studio.dicObjectCtrl == null) return null;
                foreach (var kv in studio.dicObjectCtrl)
                {
                    var och = kv.Value as Studio.OCIChar;
                    if (och != null && ReferenceEquals(och.charInfo, cc)) return och;
                }
            }
            catch (Exception e)
            { LiquidWobbleMPBPlugin._logger?.LogWarning("AutoBodyReveal: Studio object lookup failed for '" + cc.name + "': " + e.Message); }
            return null;
        }

        // Studio hides the penis by default and an x-ray of a hidden penis shows nothing, so applying
        // a womb to this pair turns it on - the same call the character menu's toggle makes. Only in
        // Studio: the main game decides this itself. No fallback: an unresolvable Studio object is
        // reported and nothing is changed.
        private static void EnsurePenisVisible(Component male)
        {
            if (!MainGameWomb.IsStudio || male == null) return;
            var oci = OciCharFor(male);
            if (oci == null)
            {
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: no Studio object for '" + male.name
                    + "' - the penis was not unhidden. Toggle it in the character menu.");
                return;
            }
            if (oci.oiCharInfo != null && oci.oiCharInfo.visibleSon)
            {
                if (Debug) LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: penis already visible on '" + male.name + "'.");
                return;
            }
            oci.SetVisibleSon(true);
            LiquidWobbleMPBPlugin._logger?.LogMessage("CloXray: unhid the penis on '" + male.name + "'.");
        }

        private static void ApplyPenisForWomb(WombExpandEffect w, Component receiver, bool manual = false, bool claimByTip = false)
        {
            // the same fault fixed for garments, on the path that fix does NOT reach. This is
            // called through AutoReapply, not ApplyForCharacterNow, so UpgradeStack was still false here
            // on any automatic pass - and EnsurePenisOrgInside PERSISTS Q(PenisCarveQueue, ...) at 3501
            // legacy, dragging the penis copies off the stack the womb is on. The penis then vanishes,
            // not at load but whenever a deferred re-pair happens to run: "after some time the penis
            // disappeared". Her body stamp is the record of the stack, so read it from her.
            bool hadUpgrade = MEBridge.UpgradeStack;
            if (!hadUpgrade && receiver != null)
                MEBridge.UpgradeStack = MEBridge.StampIsUpgraded(MEBridge.FindBodyStamp(receiver));
            try
            {
                Transform target = FindChild(w.transform, "penis_target");
                if (target == null) { LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: womb '" + w.name + "' has no penis_target bone."); return; }
                var all = UnityEngine.Object.FindObjectsOfType<Transform>();
                Component penetrator; float claimGap;
                Transform end = NearestPenisEnd(all, target.position, receiver, out penetrator, out claimGap, target, w.EntranceWorld(), manual, w.CanalEntryBone, claimByTip);   // the OTHER character's penis, if unclaimed
                // The on-screen note is about the girl the key was pressed on, not whichever womb ran last.
                bool noteHere = PenisTakeover && (PenisNoteFor == null || ReferenceEquals(receiver, PenisNoteFor));
                if (penetrator == null || end == null)
                {
                    LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: womb '" + w.name + "': no penetrator ("
                        + (manual ? "a shown character's penis with its base within " + (KeyClaimRange * 100f).ToString("F0") + "cm of her canal mouth"
                                  : "a cm_m_dankon character other than the receiver") + ") found.");
                    // WHY there is no candidate. A male whose uncensor is not BP-driven carries a penis MESH but no
                    // k_f_dan bones, so he is never a candidate at all - and the plain "no penis base within 35cm"
                    // note then blames DISTANCE, which sends people shuffling the male closer forever. Name the real
                    // reason when a male with a penis is standing right there. (Studio does not change anyone's
                    // uncensor - Free-H forces one, Studio asks; this message is the asking.)
                    Component near = MainGameWomb.FindNearestMaleWithPenis(w.EntranceWorld(), KeyClaimRange, receiver);
                    bool notBP = near != null && FindChild(near.transform, DanEntryBone) == null;
                    if (notBP)
                        LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: '" + near.name + "' is right at her but his penis is NOT BetterPenetration-driven"
                            + " (no " + DanEntryBone + " bone), so nothing can aim or bend it. Give him a BP penis uncensor (\"BP SOS\" or similar) in"
                            + " Character -> Uncensor, then press the key again.");
                    if (noteHere)
                        PenisNote = notBP
                            ? "'" + near.name + "' is close enough, but his penis is not BP - give him a BP penis uncensor, then press again"
                            : "no penis base within " + (KeyClaimRange * 100f).ToString("F0") + "cm of her";
                    return;
                }
                // Is that nearest penis actually IN this womb? With one male and several females-each-with-a-womb,
                // every womb finds the SAME lone male - only the womb he's really in should claim him, else he gets
                // dragged to a far female's vagina. Real couple ~0.1m; a male across the room ~1.2m.
                float penisDist = Vector3.Distance(end.position, target.position);
                // Kept unmodified for the on-screen note: penisDist is zeroed below when a pair is
                // adopted rather than measured, and the note has to say what was actually seen.
                float penisGap = penisDist;
                // The aim constraint is what HOLDS the penis at the womb, so its own marker springs back
                // once the links are deleted - measuring that distance to decide whether to re-create them
                // is circular. Two explicit signals override it: BetterPenetration already has this penis
                // bound to this womb's wearer, or the apply hotkey was pressed for this pair.
                bool bpSaysThisPair = receiver != null &&
                    (UnityEngine.Object)BPBridge.CollisionCharacterOf(penetrator) == (UnityEngine.Object)receiver;

                // ALREADY OURS. If THIS womb's penis_target is what holds his tip, the pairing question was
                // answered earlier, here. Distance says nothing: the aim link is what puts the penis
                // at the womb, so measuring it to decide whether to keep the link is circular, and a tip
                // that has drifted (a pose change, a scaled penis) reads as "someone else's" and gets
                // dropped. Another womb's penis can never reach this point - NearestPenisEnd refuses to
                // return one - so this only ever re-adopts a pair already made here.
                bool heldHere = NodeConstraintBridge.ParentOfChild(end) == target;
                // A key press has already chosen by where the character IS, so none of the tip-distance checks
                // below apply to it: the aim link is what brings the tip to the womb.
                if (manual)
                    LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: womb '" + w.name + "': '" + penetrator.name
                        + "' has the nearest penis base, " + (claimGap * 100f).ToString("F0") + "cm from her canal mouth (tip "
                        + penisDist.ToString("F2") + "m from penis_target" + (heldHere ? ", already held by this womb" : "") + ") - claiming him.");
                if (!manual && penisDist > PenisWombRange && heldHere)
                {
                    LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: womb '" + w.name + "': '" + penetrator.name + "' is "
                        + penisDist.ToString("F2") + "m from penis_target but THIS womb already holds his tip - keeping the pair.");
                    penisDist = 0f;
                }
                // A LONE penis, explicitly asked for. The old guard also required the tip to carry NO
                // constraint at all, so a penis held by a leftover or hand-made link could never be claimed
                // however deliberate the request - the takeover path exists precisely to take those.
                if (!manual && penisDist > PenisWombRange && (bpSaysThisPair || PenisTakeover))
                {
                    LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: womb '" + w.name + "': '" + penetrator.name + "' is "
                        + penisDist.ToString("F2") + "m from penis_target, but "
                        + (bpSaysThisPair ? "BetterPenetration has him bound to '" + (receiver != null ? receiver.name : "?") + "'"
                                          : "the key was pressed for this pair")
                        + " - aiming him at it (the aim link is what brings the penis to the womb).");
                    penisDist = 0f;
                }
                if (!manual && penisDist > PenisWombRange)
                {
                    LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: womb '" + w.name + "': nearest penis ('" + penetrator.name + "') is " + penisDist.ToString("F2") + "m from penis_target (> " + PenisWombRange.ToString("F2") + "m) -> that male is in a DIFFERENT womb; skipping penis x-ray + aiming for this one.");
                    if (noteHere) PenisNote = "penis SKIPPED - nearest ('" + penetrator.name + "') is " + penisDist.ToString("F2") + "m away, another womb's";
                    return;
                }
                // The couple is settled (nearest, in range, not another womb's). Whether the tip is re-aimed is
                // decided with the links below, and the on-screen note reports what that actually did.
                CapturePenetrator(w, penetrator);   // remember his penis uncensor for a later male replacement
                // Was this pair already set up with her? Then the key only completes what is missing below.
                bool preSet = manual && PenisAlreadySetUp(end, target, penetrator,
                                                          SeatOrifice(receiver, w.EntranceWorld()), w.CanalEntryBone,
                                                          NodeConstraintBridge.LivePairs());
                Transform entry  = FindChild(penetrator.transform, DanEntryBone);
                Transform vagina = EntryAnchorFor(w, receiver, entry, manual);
                int st = w.OrganStencil();
                LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: womb '" + w.name + "' -> receiver='" + (receiver != null ? receiver.name : "none") + "' penetrator='" + penetrator.name + "' vagina=" + (vagina != null ? "'" + vagina.name + "'" : "NONE") + " stencil=" + st + ".");
                // Say WHY he was taken. Claiming a penis moves the scene - the aim link drags him to the
                // womb - so the gap that decided it belongs on screen, not only in the log.
                string gapCm = (penisGap * 100f).ToString("F0") + "cm";
                string howClaimed = manual ? (preSet ? "kept, already set up with her"
                                                    : "claimed at " + (claimGap * 100f).ToString("F0") + "cm, penis base to her")
                                  : penisGap <= PenisWombRange ? "claimed at " + gapCm
                                  : heldHere ? "adopted, already this womb's"
                                  : bpSaysThisPair ? "BP binds him to her, " + gapCm + " away"
                                  : "claimed by the key at " + gapCm;

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
                // HOTKEY ONLY, and only for a male. Revealing a hidden penis CHANGES the scene,
                // so it needs an explicit action - nothing is added on load that was not saved in the
                // scene. On the automatic path this fired for every womb: NearestPenisEnd can settle on
                // a female, whose body carries a hidden dan mesh of its own, so loading a scene of wombs
                // and women with no male in it gave the women penises. A futa whose penis is already
                // visible is unaffected either way (the call is a no-op then).
                if (manual)
                {
                    if (MainGameWomb.IsMaleChara(penetrator)) EnsurePenisVisible(penetrator);   // a hidden penis cannot show through the window
                    else LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: '" + penetrator.name
                        + "' is not a male character - its penis visibility is left exactly as the scene saved it.");
                }
                MEBridge.EnsurePenisOrgInside(penetrator, st, true);   // x-ray the penetrator's penis, matched to THIS womb
                MainGameWomb.DumpBPDanOptions(penetrator);             // log this male's BP DanOptions (harvest for the Free-H override)
                string aimOutcome = null;   // what the aim links below actually did, for the on-screen note
                bool entryFailed = false;
                if (NodeConstraintBridge.Available)
                {
                    // NEVER reassign a dan node already targeted by hand (wired EITHER direction - e.g. aimed at a hand-made sphere).
                    if (entry != null && vagina != null)
                    {
                        if (NodeConstraintBridge.HasConstraintForNode(entry))
                        {
                            // A link from this womb's own mouth that still carries that test offset is put back to
                            // zero (see IsWithdrawnTestOffset); anything else on his entry node is left exactly as it is.
                            Vector3 off;
                            if (manual && vagina == w.CanalEntryBone
                                && NodeConstraintBridge.GetLinkOffset(vagina, entry, out off) && IsWithdrawnTestOffset(w, vagina, off)
                                && NodeConstraintBridge.SetLinkOffset(vagina, entry, Vector3.zero))
                                LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: womb '" + w.name + "' - removed the 5mm entry offset"
                                    + " an earlier test build put on its link; the entry sits on clo_canal_entry itself again.");
                            else LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: k_f_dan_entry already has a constraint -> leaving your target, not reassigning.");
                        }
                        else if (!NodeConstraintBridge.AddPositionLink(vagina, entry, ""))   // empty alias -> shows raw "parent -> child" like a manual one
                            entryFailed = true;
                    }
                    // AIM TAKEOVER (2026-08-26). A dan_end already constrained to one of HER BONES is an
                    // auto-aim - the game's, BP's, or a leftover - and the womb is a better target for it, so it is
                    // claimed. A dan_end constrained to an ITEM is a hand-made sphere-aim and is NEVER touched.
                    // That single distinction is the rule; everything else here is just reporting which way it went.
                    Transform driver = NodeConstraintBridge.DriverOf(end);
                    if (driver == null)
                    {
                        if (NodeConstraintBridge.HasConstraintForNode(end))
                        {
                            aimOutcome = "tip left alone (it drives another link)";
                            LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: k_f_dan_end drives someone else's constraint -> leaving it alone.");
                        }
                        else if (!NodeConstraintBridge.AddPositionLink(target, end, ""))
                            aimOutcome = "aim link could not be added";
                    }
                    else if (driver == target)
                    {
                        LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: k_f_dan_end is already aimed at this womb's penis_target.");
                    }
                    else
                    {
                        // OWNERSHIP, not ancestry - see OwningStudioObject. A sphere parented to her is
                        // still the sphere's, and stays hand-owned.
                        var ownerOci = OwningStudioObject(driver);
                        var ownerChar = ownerOci as Studio.OCIChar;
                        Component ownerCc = (ownerChar != null) ? ownerChar.charInfo : null;
                        bool herBone = ownerCc != null && IsFemale(ownerCc);
                        if (!herBone)
                        {
                            string what = ownerOci == null ? "something Studio does not own"
                                        : ownerCc != null ? "a male's bone"
                                        : "'" + (ownerOci.treeNodeObject != null ? ownerOci.treeNodeObject.textName : ownerOci.GetType().Name) + "' - your own object";
                            aimOutcome = "tip left on '" + driver.name + "' (your own aim)";
                            LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: k_f_dan_end is aimed at '" + driver.name
                                + "' (" + what + ") -> leaving your target, not reassigning.");
                        }
                        else if (NodeConstraintBridge.RemoveDriverOf(end))
                        {
                            if (NodeConstraintBridge.AddPositionLink(target, end, ""))
                            {
                                aimOutcome = "aim taken over from her '" + driver.name + "'";
                                LiquidWobbleMPBPlugin._logger?.LogWarning("AutoBodyReveal: TOOK OVER the aim on k_f_dan_end - was '" + driver.name
                                    + "', a bone of '" + ownerCc.name + "' (her own rig = an auto-aim), now this womb's penis_target.");
                            }
                            else aimOutcome = "released from her '" + driver.name + "' but the new aim link failed";
                        }
                        else aimOutcome = "aim on '" + driver.name + "' could not be taken over";
                    }
                }
                else
                {
                    aimOutcome = "not aimed (NodesConstraints missing)";
                    LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: NodesConstraints not present -> penis x-rayed but not aimed.");
                }
                if (entryFailed) aimOutcome = "entry link could not be added" + (aimOutcome != null ? ", " + aimOutcome : "");
                // Ctrl+Shift+W only, and only once THIS womb drives the tip: a KKPE bend would keep the penis off its
                // target. An aim left on the user's own object is the user's pose, bends included.
                if (PenisTakeover && NodeConstraintBridge.Available && NodeConstraintBridge.DriverOf(end) == target)
                {
                    string kkpe = ClearKkpeAimEdits(penetrator);
                    if (kkpe != null) aimOutcome = aimOutcome != null ? aimOutcome + ", " + kkpe : kkpe;
                }
                if (noteHere) PenisNote = "penis x-rayed on '" + penetrator.name + "' (" + howClaimed + ")"
                                             + (aimOutcome != null ? ", " + aimOutcome : "");
            }
            catch (System.Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("AutoBodyReveal: penis-for-womb failed on '" + (w != null ? w.name : "?") + "': " + e.Message); }
            finally { if (!hadUpgrade) MEBridge.UpgradeStack = false; }
        }

        // Her vagina, when this womb sits on it. The only orifice the key ever links an entry to - see EntryAnchorFor.
        // Occupancy is not its business: this answers which orifice the womb IS, not where a new link may go.
        private static Transform SeatOrifice(Component receiver, Vector3 mouth)
        {
            if (receiver == null) return null;
            Transform vagina = FindChild(receiver.transform, VaginaBone);
            return (vagina != null && Vector3.Distance(vagina.position, mouth) <= OrificeSeatRange) ? vagina : null;
        }

        // Is this penis ALREADY SET UP with this womb: its tip aimed at THIS womb's penis_target, or its entry on
        // the womb's own canal mouth, or on her vagina when the womb sits there. Only NodesConstraints count - links
        // that exist in the scene. BetterPenetration keeps a male bound to her after those links are deleted, and
        // even after he is deleted, so its own binding says nothing about how the scene is wired.
        private static bool PenisAlreadySetUp(Transform end, Transform target, Component male, Transform seat,
                                              Transform ownMouth,
                                              System.Collections.Generic.List<KeyValuePair<Transform, Transform>> pairs)
        {
            if (pairs == null) return false;
            Transform entry = null; bool entryLooked = false;
            foreach (var pr in pairs)
            {
                if (pr.Value == end && pr.Key == target) return true;
                if ((seat == null || pr.Key != seat) && (ownMouth == null || pr.Key != ownMouth)) continue;
                if (pr.Value == null || pr.Value.name != DanEntryBone) continue;
                if (!entryLooked) { entry = male != null ? FindChild(male.transform, DanEntryBone) : null; entryLooked = true; }
                if (entry != null && pr.Value == entry) return true;
            }
            return false;
        }

        // Nearest k_f_dan_end under a ChaControl that carries a cm_m_dankon penis, OTHER than `exclude` (the receiver).
        // `myTarget` is the asking womb's aim bone: a penis whose tip is already constrained to a
        // DIFFERENT womb belongs to that womb, so it is skipped and the search moves to the next nearest.
        // Without this, a second womb finds the first womb's penis, sees it taken and gives up instead of
        // looking further - which is why a second couple in the same scene never wired up.
        // Set only for the one-key Studio setup (Ctrl+Shift+W): that key promises a finished scene, so
        // a penis held by a FOREIGN constraint is taken over instead of skipped. A penis held by
        // another womb's penis_target is still off limits - that would break the other couple.
        // What the last one-key press did to the penis, for the on-screen line. Set here because only
        // this layer knows which character was picked and why one was not.
        internal static string PenisNote;
        internal static bool   PenisUpgraded;
        // Whose womb the note is about. The key press applies every womb in the scene, and the line on screen is
        // for the girl it was pressed on. Null = any womb.
        internal static Component PenisNoteFor;

        internal static bool PenisTakeover;

        // REJECTION LOG (Diagnostic log only). Every k_f_dan_end this womb considered and why it lost.
        // A crowded scene reported "no penetrator found" for three wombs after a scene LOAD that had
        // paired fine on the hotkey press, and the reasons were invisible - which is what made it a
        // guess. Nothing here changes a decision; it only says out loud which one was taken.
        private static Transform NearestPenisEnd(Transform[] all, Vector3 pos, Component exclude, out Component owner, out float claimDist, Transform myTarget = null, Vector3? mouth = null, bool shownOnly = false, Transform ownMouth = null, bool byTip = false)
        {
            Transform best = null; float bsq = float.MaxValue; owner = null; claimDist = float.MaxValue;
            // `byTip` is the key pressed on a womb that is not on one of her orifices: nothing about her says which
            // penis belongs to it, so the one aimed nearest its aim bone is the one meant for it.
            bool byBase = shownOnly && mouth.HasValue && !byTip;
            // A PAIR ALREADY SET UP WITH HER KEEPS ITS PENIS, however close anyone else stands: his tip aimed at
            // this womb's penis_target, or his entry on the orifice the womb sits on. Re-picking a wired couple by
            // distance is what would break a scene someone posed or animated by hand. Distance only decides who is
            // taken when nothing is wired yet, so the range limit below does not apply to these.
            Transform bestSet = null; Component ownerSet = null; float bssq = float.MaxValue;
            var livePairs = byBase ? NodeConstraintBridge.LivePairs() : null;
            Transform seatBone = byBase ? SeatOrifice(exclude, mouth.Value) : null;
            var dbg = (Debug && myTarget != null) ? new System.Text.StringBuilder() : null;
            if (dbg != null)
            {
                dbg.Append("AutoBodyReveal: PENIS CANDIDATES for penis_target#").Append(myTarget.GetInstanceID())
                   .Append(" of the womb worn by '").Append(exclude != null ? exclude.name : "?").Append("':")
                   .AppendLine().Append("   target@").Append(pos.ToString("F2"));
                if (mouth.HasValue) dbg.Append("  canalMouth@").Append(mouth.Value.ToString("F2"))
                                       .Append("  (they are ").Append(((mouth.Value - pos).magnitude * 1000f).ToString("F0")).Append("mm apart)");
                dbg.Append(byBase ? "   [key press: BASE toMouth CHOOSES, within " + (KeyClaimRange * 100f).ToString("F0") + "cm]"
                                  : "   [toTarget = what CHOOSES; toMouth = distance to her canal mouth]");
            }
            foreach (var t in all)
            {
                if (t == null || t.name != "k_f_dan_end") continue;
                Component cc = FindChaControlOf(t);
                string who = cc != null ? cc.name : "?";
                if (cc == null) { if (dbg != null) dbg.AppendLine().Append("   REJECT (no ChaControl above it)"); continue; }
                if (cc == exclude) { if (dbg != null) dbg.AppendLine().Append("   ").Append(who).Append(": REJECT - is the receiver herself"); continue; }
                if (shownOnly && !IsCharacterShown(cc)) { if (dbg != null) dbg.AppendLine().Append("   ").Append(who).Append(": REJECT - character is hidden"); continue; }
                if (!MEBridge.HasPenisMaterial(cc)) { if (dbg != null) dbg.AppendLine().Append("   ").Append(who).Append(": REJECT - no cm_m_dankon material"); continue; }
                Transform baseBone = (byBase || dbg != null) ? FindChild(cc.transform, PenisBaseBone) : null;
                float baseDist = (baseBone != null && mouth.HasValue) ? (baseBone.position - mouth.Value).magnitude : float.MaxValue;
                bool preSet = byBase && PenisAlreadySetUp(t, myTarget, cc, seatBone, ownMouth, livePairs);
                if (byBase)
                {
                    string why = null;
                    if (baseBone == null) why = "no " + PenisBaseBone + " bone";
                    else if (!MainGameWomb.IsMaleChara(cc) && !MEBridge.HasVisiblePenisMesh(cc)) why = "a female whose penis is hidden";
                    else if (!preSet && baseDist > KeyClaimRange)
                        why = "penis base " + (baseDist * 100f).ToString("F0") + "cm from her canal mouth (limit " + (KeyClaimRange * 100f).ToString("F0") + "cm)";
                    if (why != null) { if (dbg != null) dbg.AppendLine().Append("   ").Append(who).Append(": REJECT - ").Append(why); continue; }
                }
                float dist = (t.position - pos).magnitude;
                string dists = "toTarget=" + (dist * 1000f).ToString("F0") + "mm"
                             + (mouth.HasValue ? "  toMouth=" + ((t.position - mouth.Value).magnitude * 1000f).ToString("F0") + "mm" : "")
                             + "  end@" + t.position.ToString("F2");
                if (dbg != null)
                {
                    // Where this character physically is, as opposed to where his aim node was left: the
                    // penis base, whose orifice his entry node is linked to, and whom BP binds him to.
                    Transform entryNode = FindChild(cc.transform, DanEntryBone);
                    Transform entryHolder = entryNode != null ? NodeConstraintBridge.ParentOfChild(entryNode) : null;
                    Component entryOwner = entryHolder != null ? FindChaControlOf(entryHolder) : null;
                    Component bpHer = BPBridge.CollisionCharacterOf(cc);
                    dists += "  " + (MainGameWomb.IsMaleChara(cc) ? "male" : "FEMALE")
                           + " penisShown=" + MEBridge.HasVisiblePenisMesh(cc)
                           + (baseBone != null && mouth.HasValue
                              ? "  BASE toMouth=" + (baseDist * 1000f).ToString("F0") + "mm base@" + baseBone.position.ToString("F2")
                              : "  base=?")
                           + "  entry<-" + (entryHolder != null ? "'" + entryHolder.name + "' [" + (entryOwner != null ? entryOwner.name : "item") + "]" : "nothing")
                           + "  BPbinds=" + (bpHer != null ? bpHer.name : "none");
                }
                if (myTarget != null)
                {
                    Transform holder = NodeConstraintBridge.ParentOfChild(t);
                    if (dbg != null)
                        dbg.AppendLine().Append("   ").Append(who).Append(": ").Append(dists).Append("  heldBy=")
                           .Append(holder == null ? "nothing" : holder.name + "#" + holder.GetInstanceID()
                                   + (holder == myTarget ? " (MINE)" : " (foreign)"));
                    if (holder != null && holder != myTarget)
                    {
                        // WHOSE penis this is, and WHETHER TO RE-AIM it, are different questions, and
                        // only the second one cares who holds the tip. Another womb's aim target owning
                        // it answers the first: he is already that womb's, never take him. Any OTHER
                        // holder - a hand-made constraint, a leftover from an old setup - says nothing
                        // about ownership, and rejecting on it left an ordinarily penetrating couple
                        // unshaded: BP's own `cf_J_Vagina_root -> k_f_dan_entry` names her while a
                        // hand-made `cf_j_spine02 -> k_f_dan_end` holds the tip, which is a perfectly
                        // normal scene.
                        //
                        // Nothing here re-aims anything. A hand-made aim is safe because the link is never
                        // CREATED over one (`aimLeftToUser` below applies shaders only) - not because
                        // the penis was refused, which only meant no shaders either.
                        bool anotherWombOwnsIt = holder.name == "penis_target";
                        if (anotherWombOwnsIt)
                        {
                            if (dbg != null) dbg.Append("  -> REJECT: another womb's penis_target owns it");
                            continue;
                        }
                        if (dbg != null) dbg.Append("  -> foreign holder, but not another womb's - his shaders are ours, his aim is not");
                        if (dbg != null) dbg.Append("  -> taking over the foreign link (hotkey)");
                    }
                }
                else if (dbg != null) dbg.AppendLine().Append("   ").Append(who).Append(": ").Append(dists);
                if (preSet && dbg != null) dbg.Append("  -> already set up with her, kept");
                float d = byBase ? baseDist * baseDist : (t.position - pos).sqrMagnitude;
                if (preSet && d < bssq) { bssq = d; bestSet = t; ownerSet = cc; }
                if (d < bsq) { bsq = d; best = t; owner = cc; }
            }
            bool keptSet = bestSet != null;
            if (keptSet) { best = bestSet; owner = ownerSet; bsq = bssq; }
            if (best != null) claimDist = Mathf.Sqrt(bsq);
            if (dbg != null)
            {
                dbg.AppendLine().Append("   => ").Append(best == null ? "NO CANDIDATE SURVIVED"
                    : "chose '" + (owner != null ? owner.name : "?") + "' at " + (claimDist * 1000f).ToString("F0") + "mm"
                      + (byBase ? " (penis base to her canal mouth)" : " (tip to penis_target)")
                      + (keptSet ? " - already set up with her, distance did not decide" : ""));
                LiquidWobbleMPBPlugin._logger?.LogWarning(dbg.ToString());
            }
            return best;
        }

        private static Component FindChaControlOf(Transform t)
        {
            for (var c = t; c != null; c = c.parent) { var cc = c.GetComponent("ChaControl"); if (cc != null) return cc; }
            return null;
        }

        // Is the CHARACTER shown - not his penis, which the key press unhides itself. ChaControl.visibleAll is
        // the character-level flag (BetterPenetration's CoreGame and MoreAccessories key on it); a root switched
        // off entirely counts as hidden too.
        private static bool IsCharacterShown(Component cc)
        {
            var ch = cc as ChaControl;
            return ch != null && ch.gameObject.activeInHierarchy && ch.visibleAll;
        }

        // KKPE BENDS ON THE PENIS. KKPE (KKPE.dll / KKSPE.dll, namespace HSPE) re-applies every saved bone
        // edit after IK, i.e. after BetterPenetration has posed the shaft, so a saved rotation or position on a
        // bone BP poses wins every frame: the penis points away from its target even with its end node sitting
        // exactly on penis_target. Ctrl+Shift+W clears those edits through KKPE's own ResetBoneRot /
        // ResetBonePos (what its reset buttons call), so KKPE's saved data stays consistent. Scale edits are
        // the penis's size and are kept. Reflection, soft dependency: without KKPE there is nothing to clear.
        private static readonly string[] KkpeAimBones =
        {
            // BP's BoneNames.DanBones + BPDanEnd + VirtualDanBones - DanPoints.AimDanPoints writes each one's
            // world position and rotation every frame.
            "cm_J_dan101_00", "cm_J_dan102_00", "cm_J_dan103_00", "cm_J_dan104_00", "cm_J_dan105_00",
            "cm_J_dan106_00", "cm_J_dan107_00", "cm_J_dan108_00", "cm_J_dan109_00", "cm_J_dan119_00",
            "cm_J_vdan102_00", "cm_J_vdan104_00", "cm_J_vdan106_00", "cm_J_vdan108_00",
            // the two nodes the key links to the womb
            DanEntryBone, "k_f_dan_end",
        };
        private static bool _kkpeTried, _kkpeBroken;
        private static Type _kkpePoseCtrl;
        private static FieldInfo _kkpeBonesEditor, _kkpeDirtyBones, _kkpeRot, _kkpePos;
        private static PropertyInfo _kkpeRotHas, _kkpePosHas;
        private static MethodInfo _kkpeResetRot, _kkpeResetPos;

        private static bool KkpeReady()
        {
            if (!_kkpeTried)
            {
                _kkpeTried = true;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type t = null;
                    try { t = asm.GetType("HSPE.PoseController", false); } catch { }
                    if (t != null) { _kkpePoseCtrl = t; break; }
                }
                if (_kkpePoseCtrl != null)
                {
                    const BindingFlags bf = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                    _kkpeBonesEditor = _kkpePoseCtrl.GetField("_bonesEditor", bf);
                    Type editor = _kkpeBonesEditor != null ? _kkpeBonesEditor.FieldType : null;
                    Type data = editor != null ? editor.GetNestedType("TransformData", BindingFlags.NonPublic | BindingFlags.Public) : null;
                    _kkpeDirtyBones = editor != null ? editor.GetField("_dirtyBones", bf) : null;
                    _kkpeRot = data != null ? data.GetField("rotation", bf) : null;
                    _kkpePos = data != null ? data.GetField("position", bf) : null;
                    _kkpeRotHas = _kkpeRot != null ? _kkpeRot.FieldType.GetProperty("hasValue", bf) : null;
                    _kkpePosHas = _kkpePos != null ? _kkpePos.FieldType.GetProperty("hasValue", bf) : null;
                    var sig = new[] { typeof(Transform), typeof(Transform), typeof(bool), typeof(int) };
                    _kkpeResetRot = editor != null ? editor.GetMethod("ResetBoneRot", bf, null, sig, null) : null;
                    _kkpeResetPos = editor != null ? editor.GetMethod("ResetBonePos", bf, null, sig, null) : null;
                    _kkpeBroken = _kkpeDirtyBones == null || _kkpeRotHas == null || _kkpePosHas == null || _kkpeResetRot == null || _kkpeResetPos == null;
                    if (_kkpeBroken)
                        LiquidWobbleMPBPlugin._logger?.LogError("CloXray: KKPE is installed but its bone editor is not the one CloXray knows (bonesEditor="
                            + (_kkpeBonesEditor != null) + " dirtyBones=" + (_kkpeDirtyBones != null) + " hasValue=" + (_kkpeRotHas != null && _kkpePosHas != null)
                            + " resetRot=" + (_kkpeResetRot != null) + " resetPos=" + (_kkpeResetPos != null)
                            + ") - a penis bend saved in KKPE will NOT be cleared by the key.");
                }
            }
            return _kkpePoseCtrl != null && !_kkpeBroken;
        }

        // Clears KKPE rotation/position edits on `male`'s aim bones. Returns the on-screen fragment, or null when
        // there was nothing to clear. Every bone touched is named in the log.
        private static string ClearKkpeAimEdits(Component male)
        {
            if (male == null || !KkpeReady()) return null;
            try
            {
                var oci = OciCharFor(male);
                Transform host = (oci != null && oci.guideObject != null) ? oci.guideObject.transformTarget : null;
                Component pose = host != null ? host.GetComponent(_kkpePoseCtrl) : null;
                if (pose == null) return null;   // KKPE never handled this character: it holds no edits
                object editor = _kkpeBonesEditor.GetValue(pose);
                var dirty = editor != null ? _kkpeDirtyBones.GetValue(editor) as System.Collections.IDictionary : null;
                if (dirty == null || dirty.Count == 0) return null;

                var byName = new Dictionary<string, Transform>();
                foreach (var t in male.GetComponentsInChildren<Transform>(true))
                    if (t != null && !byName.ContainsKey(t.name)) byName[t.name] = t;

                var cleared = new List<string>();
                var kept = new List<string>();
                foreach (string name in KkpeAimBones)
                {
                    Transform bone;
                    if (!byName.TryGetValue(name, out bone) || !dirty.Contains(bone.gameObject)) continue;
                    object data = dirty[bone.gameObject];
                    bool rot = (bool)_kkpeRotHas.GetValue(_kkpeRot.GetValue(data), null);
                    bool pos = (bool)_kkpePosHas.GetValue(_kkpePos.GetValue(data), null);
                    if (!rot && !pos) continue;   // a scale edit only: the penis's size, kept
                    if (rot) _kkpeResetRot.Invoke(editor, new object[] { bone, null, false, 0 });
                    if (pos) _kkpeResetPos.Invoke(editor, new object[] { bone, null, false, 0 });
                    // KKPE refuses to reset a rotation Studio FK drives - report what actually changed.
                    object after = dirty.Contains(bone.gameObject) ? dirty[bone.gameObject] : null;
                    bool rotLeft = after != null && (bool)_kkpeRotHas.GetValue(_kkpeRot.GetValue(after), null);
                    bool posLeft = after != null && (bool)_kkpePosHas.GetValue(_kkpePos.GetValue(after), null);
                    var parts = new List<string>();
                    if (rot && !rotLeft) parts.Add("rotation");
                    if (pos && !posLeft) parts.Add("position");
                    if (parts.Count > 0) cleared.Add(name + " (" + string.Join("+", parts.ToArray()) + ")");
                    if ((rot && rotLeft) || (pos && posLeft)) kept.Add(name);
                }
                if (cleared.Count > 0)
                    LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: cleared KKPE edits that fought BetterPenetration on '" + male.name
                        + "': " + string.Join(", ", cleared.ToArray()) + ". Scale edits kept.");
                if (kept.Count > 0)
                    LiquidWobbleMPBPlugin._logger?.LogWarning("AutoBodyReveal: KKPE kept its edit on '" + male.name + "' " + string.Join(", ", kept.ToArray())
                        + " - Studio FK drives that bone, so KKPE will not reset it. Turn FK off on the penis to let BetterPenetration aim it.");
                if (cleared.Count == 0 && kept.Count == 0) return null;
                string note = cleared.Count > 0 ? "KKPE bend cleared (" + cleared.Count + " bone" + (cleared.Count == 1 ? "" : "s") + ")" : null;
                if (kept.Count > 0) note = (note != null ? note + ", " : "") + "KKPE bend kept on " + kept.Count + " FK bone" + (kept.Count == 1 ? "" : "s");
                return note;
            }
            catch (Exception e)
            {
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: clearing the KKPE edits on '" + male.name + "' penis bones failed - nothing more was changed: " + e);
                return "KKPE bend NOT cleared (see log)";
            }
        }

        // Nearest character (ChaControl) to a world point, within maxRange. Walks up to the ChaControl via
        // GetComponent("ChaControl") (string overload) so there is no ChaControl type reference. Matches the
        // BP vagina bone first; if NO character has it (BP not set up on the female), falls back to the vanilla
        // cf_j_kokan crotch bone (female-gated) so the x-ray still applies without BP. BP scenes are unaffected
        // - the first pass finds candidates and the fallback never runs.
        private static Component FindNearestCharacter(Vector3 pos, float maxRange = float.MaxValue)
        {
            int cand;
            Component best = FindNearestByBone(pos, maxRange, VaginaBone, false, out cand);
            if (best == null)
            {
                // No vagina-root candidate at all (BP-less body) OR every one out of range. The latter is the
                // MAIN-GAME H case: cf_J_Vagina_root is NOT animated with the pose there - it idles at the
                // character's standing position while the real crotch (cf_j_kokan, which the womb mirrors)
                // moves with the animation. The kokan fallback is pose-true in both games.
                LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal:   '" + VaginaBone + "' " + (cand == 0 ? "not present on any character" : "candidate(s) out of range") + " -> fallback to vanilla '" + FallbackBone + "' (female-only).");
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
                // (womb-subtree exclusion dropped: the 7.4.0 womb has no bone by any of the names
                // this is ever asked for - its kokan is clo_-prefixed, so self-match is impossible.)
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
