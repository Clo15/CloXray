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
        public const string OrgInsideShader  = "CloXray/OrgInside";   // applied to a male penis material so it x-rays through the body
        private const string PenisMat        = "cm_m_dankon";         // the male penis material name
        private const string BallsMat        = "cm_m_dan_f";          // the male balls material (o_dan_f)
        // Garment/accessory stamp copies render one step BEFORE the body copy (2500), because the
        // body's LimbBlock pass runs last and overwrites limb pixels with the reserved region 31 -
        // a sleeve stamped afterwards would re-open the limb it is covering (b969). Before the limb
        // feature existed these copies set no queue at all and inherited the shader's own 2500.
        private const int GarmentStampQueue = 2499;
        private const int   BodyVeilQueue    = 3504;   // after the WHOLE womb stack (organ 3500, interior 3502, cum 3503) — XrayAlpha = master fade
        // The "original look" penis copy sits here, keeping the character's OWN shader. Class-level
        // because APPLY (adopt) and REMOVE must agree on the identity — they did not, and the copy leaked.
        private const int   OriginalRedrawQueue = 3502;

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


        // ---- MASK / QUEUE STATE (b909) ----------------------------------------------------------
        // Two questions in one dump, because both are answered by the same per-material state and the
        // repro run is expensive:
        //
        // (1) VANISHING BODYPARTS. Every CloXray shader is ColorMask 0 - we never paint a pixel - so we
        // cannot repaint a garment or a body. Geometry can therefore only go missing three ways:
        // the render array (see AuditGeometry), DEPTH we write, or the GAME's own clothes alpha mask
        // that hides the body under a garment so it cannot clip through. The third is not ours to
        // write, but it IS ours to disturb: an ME material copy carries whatever mask texture was
        // bound at copy time, and a body rebuild re-composites it. So print the mask textures and
        // the queue, per material, and the answer stops being a guess.
        //
        // (2) THE HAND/TORSO REGION MASK. It only works if the copy actually carries _RegionMask with a
        // non-zero cutoff; a null texture or cutoff 0 means masking is silently OFF (by design, so
        // old scenes keep working) and hands still x-ray through. Printing both proves which.
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
                        // SHADER FINGERPRINT (b919). A copy dumped sh=CloXray/BodyReveal with NONE of
                        // the b898+ properties, while the only zipmod on disk (and the only manifest
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

        // ---- GEOMETRY AUDIT (b908) --------------------------------------------------------------
        // "The character loses bodyparts" is a RENDER-ARRAY fault, not a shader one, and Unity gives it
        // exactly three causes. Nothing in the existing dumps tested any of them, so every log we have
        // read was blind to the actual failure:
        // 1. sharedMaterials.Length < subMeshCount -> the trailing submeshes are NEVER DRAWN. This is
        // the one our own code can cause: MaterialCopyRemove is a TOGGLE, and handing it the wrong
        // material removes a slot instead of adding one. Losing a slot silently deletes geometry.
        // 2. a null entry in sharedMaterials -> that submesh is not drawn.
        // 3. renderer disabled / object inactive.
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

                    // Cause 4 (b937): a CloXray copy PAIRED with a submesh. Our stamps are ColorMask 0
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
            // ACCESSORIES NEXT - the missing half of EnsureAccessoryReveal, found via the b908 geometry
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
                // Dropping it on every removal meant a toggle OFF then ON re-captured — and a reporter's
                // sequence is exactly "change pose, x-ray off, x-ray on", by which point KPlug may have
                // already rewritten the materials. We would then snapshot the BROKEN state and restore it
                // faithfully from then on, which is why the fault looked random: it is a race between our
                // re-capture and KPlug's rewrite. Keeping the first good capture removes the race.
                // With our copies gone the renderer is back to what KPlug/the uncensor own. Dumping here
                // is the only way to see that state - every other dump fires while a womb exists.
                if (AutoBodyReveal.Debug && cc != null) DumpXrayChain(cc, null, null);
            AuditGeometry(cc, "AFTER REMOVE");
            if (AutoBodyReveal.Debug) DumpMaskState(cc, "AFTER REMOVE");
            return removed;
        }

        // Hand ME the COPY itself: MaterialCopyRemove branches on the name it is given.
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
                // then leaks: the male keeps a second draw of his penis at 3502 after the womb is gone,
                // and the next apply allocates a fresh index (MECopy1 -> MECopy3) so the layout no longer
                // matches a clean apply. Two draws of a reflective skin shader over one mesh read as a
                // blown-out white penis, which is the reported symptom.
                //
                // Identity is the one the APPLY path already claims as ours (see the copy2 adopt block):
                // a .MECopy on the penis material at exactly the redraw queue. Symmetric by construction -
                // if apply would adopt it, removal must drop it.
                // Compare against the SOURCE name, not the copy's. BaseName gives "cm_m_dankon.MECopy2"
                // and IsPenisMat demands exact equality with "cm_m_dankon", so the b885 version of this
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

                DumpMeShaderRegistry();   // one-shot: what BodyReveal object does ME actually hold?
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
                    // The torso mask shipped AFTER many copies were created and persisted, and this
                    // branch used to return without ever touching it - so an adopted pre-mask copy kept
                    // stamping her hands forever ("bare hands don't block the womb" in live testing).
                    // The mask is OURS, not a user edit: assert it whenever it is absent.
                    QueueRegionMask(me, existing, go, cc.name, debug);
                    // F1 master (b936): Free-H has no ME, so the cutoff follows the toggle. 0 = mask
                    // off = the womb shows through hands/limbs; 0.5 = the shipped default behavior.
                    _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, existing, "RegionMaskCutoff", LiquidWobbleMPBPlugin.CfgLimbMask ? 0.5f : 0f, go, true });
                    // b930: the BODY stamp must not respect the limb block (gate 0) - plain depth
                    // already stops it behind a limb, and gating it only fattened the margins by the
                    // inflate distance. Garment copies keep the shader default (128).
                    return true;
                }

                // Find or create the copy slot (MaterialCopyRemove is a toggle -> only call it to CREATE).
                var srcBody = FindByName(bodyR, "cf_m_body") ?? (bodyR.sharedMaterials.Length > 0 ? bodyR.sharedMaterials[0] : null);
                if (srcBody == null) { if (debug) LiquidWobbleMPBPlugin._logger?.LogWarning("MEBridge: no body material on '" + cc.name + "'."); return false; }
                var copy = FindCopy(bodyR, BaseName(srcBody)) ?? CreateCopyTracked(me, bodyR, srcBody, go, "body reveal");
                if (copy == null) return false;

                _mSetShader.Invoke(me, new object[] { 0, _otCharacter, copy, BodyRevealShader, go, true });
                DirectSetShader(copy, go, BodyRevealShader);   // b940: the flip happens NOW; the ME call above is persistence only
                _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, copy, "StencilRef", (float)stencil, go, true });
                QueueRegionMask(me, copy, go, cc.name, debug);
                _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, copy, "RegionMaskCutoff", LiquidWobbleMPBPlugin.CfgLimbMask ? 0.5f : 0f, go, true });   // F1 master, see the adopted path
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
        // ---- DAN_F SPLIT (b935) -----------------------------------------------------------------
        // o_dan_f is ONE mesh with two roles, split by its own bones (cm_J_dan_f_top = the covering
        // skin at the shaft junction; cm_J_dan_f_L/_R = the sack lobes) and baked offline into two
        // UV masks (tools/make_danf_mask.py, shipped beside the DLL):
        // OCCLUDER (b934 stamp, kept): masked to the SACK - the lobes block the x-ray window like
        // a hand wherever they are frontmost, and never draw through anything;
        // CONTENT (new): a carve+redraw pair like the shaft's, masked to the TOP skin - so the
        // junction region renders in-window with the penis, which removes the white blob that
        // every removal of dan_f content has re-exposed since b920 (it was dan_f's own zone,
        // bare, under the veil).
        // ---- (b934 note: OCCLUDER value; b933's organ value was wrong) --------------------------
        // The balls (cm_m_dan_f) stamp an INERT region value (2) that NO pass consumes - not the
        // shaft's window punch (4), not the organ pipeline (5), not the veil (>=5). b933 used the
        // organ value on the theory that only the WOMB chain would accept it; on screen the PENIS
        // carve's own DepthClear keys on the same 5, so the shaft punched through the balls even in
        // open air (screenshots from behind). With 2 the balls become a pure OCCLUDER of
        // the x-ray window, like a bare hand:
        // - in AIR: nothing consumes 2, the redraw fails against the balls' own depth -> plain;
        // - at the penetration overlap: no window on balls-front pixels -> the real balls show,
        // and the white is impossible (white always required a contentless WINDOW pixel);
        // - behind her body/clothes: the stamp loses the depth test to whatever is in front, so
        // her stamps rule and the balls never draw on top of anything.
        // No planes, no feeds, no state gating.
        private const float BallsOccluderRef = 2f;   // low-5 region value consumed by no pass (0=outside, 4=body, 5=organ)

        private static string MaskPath(string file)
        {
            return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(MEBridge).Assembly.Location), file);
        }

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

                // Occluder-ONLY by design: any in-window dan_f content draws THROUGH clothes and
                // body whenever the window overlaps the balls on screen (the window IS what you see
                // there) - every past balls leak was the old carve or redraw doing exactly that.
                // The whole mesh blocks the window like a hand; cutoff 0 turns the region mask off,
                // so a body rebuild has nothing texture-shaped to lose.
                if (occ == null) occ = take("dan_f occluder");
                if (occ != null)
                {
                    _mSetShader.Invoke(me, new object[] { 0, _otCharacter, occ, BodyRevealShader, go, true });
                    DirectSetShader(occ, go, BodyRevealShader);   // b940: the flip happens NOW; the ME call above is persistence only
                    _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, occ, "StencilRef", BallsOccluderRef, go, true });
                    _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, occ, "StampZWrite", 0f, go, true });
                    _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, occ, "RegionMaskCutoff", 0f, go, true });   // b944: whole mesh occludes (shader default is 0.5!)
                    SetQueuePersisted(me, 0, _otCharacter, occ, go, 2501);
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
                        DirectSetShader(copy, go, OrgInsideShader);   // b940: the flip happens NOW; the ME call above is persistence only
                    }
                    _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, copy, "StencilBody",        (float)stencil,       go, true });
                    _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, copy, "StencilBody_Plus_1", (float)(stencil + 1), go, true });
                    _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, copy, "OutsideOfBodyAlpha", 0f,                   go, true });   // inside-only: the ORIGINAL owns the outside look
                    // Shield (shader v389): the carve writes NEAR depth over the outside-body penis
                    // silhouette so copy2's re-draw is depth-rejected there.
                    _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, copy, "OutsideShieldDepth", 1f,                   go, true });
                    // b948: skin textures alpha-fade at the BASE SEAM (to blend into the torso). The
                    // carve blends mainTex.a - at those texels it painted nothing and the veil shone
                    // through the punch as the white base sliver. In-window the torso is not painted,
                    // so the carve must be opaque; the womb materials never set this and keep their
                    // translucency.
                    _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, copy, "OpaqueMainTex",      1f,                   go, true });
                    if (bottomWindow >= 0f)
                        _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, copy, "BottomWindow",   bottomWindow,         go, true });

                    // SECOND copy (b465) = the ORIGINAL LOOK re-drawn through the window: the user's
                    // shader stays UNTOUCHED on it, only the render queue is raised to 3502 — after
                    // copy1's in-window depth carve (OrgInside, 3501), in the veil's queue (transparent
                    // queues sort back-to-front, so the nearer skin veil still tints over the deeper
                    // penis). In the window the penis now renders with the ORIGINAL shading (KKUTS
                    // effects intact, painting over copy1's OrgInside color at equal depth); outside
                    // the body it re-draws the identical look over the original (benign); inside the
                    // body but outside the window the body depth still occludes it. Identity: OURS = a
                    // .MECopy with a NON-CloXray shader at exactly queue 3502 (user copies keep their
                    // own queue); otherwise created via snapshot-diff, like copy1.
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
                    // b947: BP SoS + KKUTS clip the GLANS at the stock _Cutoff 0.5 - the glans texels sit
                    // under it. In AIR that is a small hole in the original material; in-window the
                    // original-look redraw clips the same texels, nothing repaints the punched depth and
                    // the veil shines bright white through the hole (the "white cap" at the tip - proven
                    // by the b946 balls-removed isolation + _Cutoff=0.50 in the material dump). The
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
                    LiquidWobbleMPBPlugin._logger?.LogInfo("MEBridge: penis x-ray COPIES on '" + cc.name + "': carve '" + copy.name + "' -> " + OrgInsideShader + " (stencil " + stencil + "/" + (stencil + 1) + ") + original-look '" + copy2.name + "' @" + OriginalRedrawQueue + " ('" + dankon.name + "' untouched; in-window look = the original shader).");
                    return true;
                }
                // Stencil pair must match the FEMALE body the penis is seen through.
                _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, dankon, "StencilBody",        (float)stencil,       go, true });
                _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, dankon, "StencilBody_Plus_1", (float)(stencil + 1), go, true });
                _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, dankon, "OutsideOfBodyAlpha", outsideAlpha,        go, true });
                _mSetFloat.Invoke(me, new object[] { 0, _otCharacter, dankon, "OpaqueMainTex",      1f,                  go, true });   // b948: base-seam alpha fade must not blend away in-window
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

        // XRAY-CHAIN dump (b511, KKS penis-invisible-in-womb research): one line per material for the
        // male penis renderer, the female BODY renderer and the womb SMR — name, shader, renderQueue
        // and the stencil/alpha floats the window chain depends on. Diffing a KK dump against a KKS
        // dump pinpoints which link lands differently (queue not applied, float missing on the copy,
        // different original shader/queue...). Debug-gated, called once per penis x-ray apply.
        // ONE-SHOT PER CHARACTER SET (b880). This fires on every penis x-ray apply and runs to ~296 lines
        // a session — it was added to find the real balls material name for the white-balls bug, which is
        // solved. Repeating it now just buries the lines we are actually reading in a bug-report log.
        private static readonly System.Collections.Generic.HashSet<string> _chainDumped = new System.Collections.Generic.HashSet<string>();
        public static void DumpXrayChain(Component male, Component female, Component womb)
        {
            // b884: keyed on POSE too. One dump per character set was right for log volume, but a
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
                        // against Ball (0, 0.5) - and our ME copies inherit whatever it left there. A
                        // half-texture offset samples the wrong region and reads blank, which on screen is
                        // indistinguishable from white. Every chain we have compared was identical in
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
                // BALLS STAMP STATE (b933): shader, queue and the organ-value ref on the dan_f copy.
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
                            // b943: the printer was blind to the carve - it asked only for BodyReveal stamp
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
                // "white tint" — three faults that look identical on screen.
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

        // ---- DEFERRED MASK ASSERT (b921) --------------------------------------------------------
        // ME's SetMaterialShader is DEFERRED: the apply log has repeatedly shown the copy still wearing
        // Shader Forge/main_skin at the moment we finished configuring it, with the flip to BodyReveal
        // landing later. Setting _RegionMask in that gap targets a material that does not HAVE the
        // property yet, so the edit is dropped - and BodyReveal's default mask is WHITE, which stamps
        // EVERYWHERE. That is exactly "bare hands do not block". So the mask is asserted from Update:
        // wait until the copy actually wears a shader with _RegionMask, then set it once.
        private class PendingMask
        {
            public object me; public Material copy; public GameObject go; public string who; public bool debug;
            public int frames; public int setAttempts; public int lastSetFrame;
            public bool reflipTried;   // b939: one mid-wait SetShader retry before the timeout
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
                    // b939: a copy whose flip never lands is not just maskless - it is a FULLY VISIBLE
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
                    if (pm.setAttempts == 0) { _pendingMasks.RemoveAt(i); continue; }   // present before we set - nothing to prove
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

        // ---- MASK HEAL --------------------------------------------------------------------------
        // ME cannot persist texture records for these copies: its replay after a mid-H body rebuild
        // restores shader/floats/queue but NOT the mask, and the default mask is WHITE = passes
        // everywhere. Holding Material references dies with the rebuild too (replay makes new
        // objects), so the registry is reference-free: remember WHAT to look for - character root,
        // base name, shader role, file - and re-feed any matching maskless copy the pump finds.
        // Un-apply needs no bookkeeping: removed copies stop existing and the scan finds nothing.
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

        // Forensics: what does ME's controller ACTUALLY hold after our invoke? The decompiled 4.0.3
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
                // b925: FUNCTION FIRST, persistence second. Six verified ME invokes produced neither a
                // texture on the material nor a record in ME's own UI (decompiled 4.0.3 says both are
                // unconditional - the forensic dump below finds out which link actually breaks). The
                // mask being LIVE must not be hostage to that: load the PNG ourselves and set it on
                // every matching material directly. Reloads re-run this pump, so function survives
                // even if ME never persists the edit.
                bool direct = DirectSetRegionMask(go, copy, path);   // path = this caller's mask file
                if (copy.shader != null) HealMask(go, BaseName(copy), copy.shader.name, path);   // b943: reference-free re-assert after body rebuilds
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
        // The one thing we know for certain about the KPlug case: the penis is CORRECT immediately after
        // the first apply, and only goes wrong later when KPlug re-writes its shader/texture package.
        // So stop reasoning about what the values ought to be - record the whole set while it is known
        // good, and put it back whenever KPlug disturbs it.
        //
        // This is deliberately NOT the b892 approach of syncing from the live original. That copied
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

        // ---- ME SHADER REGISTRY DUMP (b920) -----------------------------------------------------
        // Every disk source is exonerated (one zipmod, current props; no other manifest among 7416
        // declares CloXray; no ME shader cache; no loose abdata bundle) - yet materials end up bound to
        // a CloXray/BodyReveal WITHOUT the b898+ properties. The remaining suspect is MaterialEditor's
        // in-memory shader dictionary. Dump every CloXray entry it holds, with instance id and property
        // fingerprint, so the next log convicts or exonerates it.
        // ---- DIRECT SHADER SET (b940) -----------------------------------------------------------
        // ME's SetMaterialShader applies via name lookups with two silent failure modes (an
        // instanced-materials reference check on the create path, and GetObjectMaterials name misses
        // on the set path), and H-mode body rebuilds re-roll those dice constantly. The result was a
        // copy left wearing the VISIBLE body shader - the blown-white duplicate skin behind weeks of
        // vanishing-garment reports. Same cure as the b925 mask: WE set the shader directly on the
        // exact material references we hold, resolved from ME's own LoadedShaders registry; ME's call
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
                    return true;
                }

                // Fresh copy. MaterialCopyRemove on the SOURCE adds a copy; verify one actually appeared
                // (toggle semantics differ across ME versions.
                var srcBody = FindByName(bodyR, "cf_m_body") ?? (bodyR.sharedMaterials.Length > 0 ? bodyR.sharedMaterials[0] : null);
                if (srcBody == null) return false;
                var copy = FindCopy(bodyR, BaseName(srcBody)) ?? CreateCopyTracked(me, bodyR, srcBody, go, "skin veil");
                if (copy == null) return false;

                _mSetShader.Invoke(me, new object[] { 0, _otCharacter, copy, BodyVeilShader, go, true });
                DirectSetShader(copy, go, BodyVeilShader);   // b940: the flip happens NOW; the ME call above is persistence only
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

        /// <summary>
        /// Dump a garment renderer's material array against its submeshes.
        /// Unity pairs materials[i] with submesh i; a material past subMeshCount re-draws the LAST submesh,
        /// and a submesh with no material is not drawn at all. So appending our BodyReveal copy to a
        /// garment whose array is already irregular can leave a real submesh drawing with an x-ray
        /// material - transparent outside the womb window - and the garment (or part of it) disappears.
        /// The body underneath is already masked away by the clothing state, so the result is a HOLE, not
        /// bare skin. We warn on the count mismatch but have never shown the actual pairing; this does.
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
                // result of appending our copy: the extra re-draws the last submesh, which for a
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
                    // b938: adopted copies persist values from ANY past build - a pre-b899 stamp still
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
        /// its own, so our copy at 2500 would lay an opaque footprint for it and depth-reject whatever
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
        private static readonly int[] ClothesKinds = { 0, 1, 2, 3, 5 };

        /// Idempotently stamp every WORN (active) torso garment with a BodyReveal copy at the given stencil.
        public static bool EnsureClothesReveal(Component cc, int stencil, bool debug)
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
                {
                    LiquidWobbleMPBPlugin._logger?.LogInfo("[clothes] '" + cc.name + "': objClothes.Length=" + slots.Length +
                        " otClothing='" + _otClothing + "' kinds=" + string.Join(",", Array.ConvertAll(ClothesKinds, k => k.ToString())));
                    // WHO are we stamping? Clothes reveal is wearer-only, so a male reaching this point at
                    // all is the bug, not the shader. Print the raw sex value beside our verdict.
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
                                ClearStampDepth(me, kind, _otClothing, existing, go);
                                SetQueuePersisted(me, kind, _otClothing, existing, go, GarmentStampQueue);
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
                            DirectSetShader(copy, go, BodyRevealShader);   // b940: the flip happens NOW; the ME call above is persistence only
                            _mSetFloat.Invoke(me, new object[] { kind, _otClothing, copy, "StencilRef", (float)stencil, go, true });
                            ClearStampDepth(me, kind, _otClothing, copy, go);
                            SetQueuePersisted(me, kind, _otClothing, copy, go, GarmentStampQueue);
                            stamped++;
                            if (debug) DumpGarmentPairing(r, "AFTER  copy of '" + baseName + "'");
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

                int stamped = 0, updated = 0;
                for (int slot = 0; slot < slots.Length; slot++)
                {
                    var go = slots[slot];
                    if (go == null || !go.activeInHierarchy) continue;   // only accessories that are ON

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
                                    SetQueuePersisted(me, slot, _otAccessory, existing, go, GarmentStampQueue);
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
                            DirectSetShader(copy, go, BodyRevealShader);   // b940: the flip happens NOW; the ME call above is persistence only
                            ClearStampDepth(me, slot, _otAccessory, copy, go);
                            SetQueuePersisted(me, slot, _otAccessory, copy, go, GarmentStampQueue);
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
            SetQueuePersisted(me, 0, _otCharacter, m, go, queue);
        }

        // b929: garment stamp copies live at queue 2501 - ONE step after the body stamp (2500) - so
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
    // reflection.
    internal static class NodeConstraintBridge
    {
        // ── OLD-SCENE NC PATH MIGRATION ───────────────────────────────────────────────────────────
        // Womb 7.4.0 renamed the item's internal bones to clo_* so they stop colliding with HER bone
        // names (ABMX indexes by bare name, first-match-wins — ).
        // NodesConstraints saves a constraint as a PATH relative to the object, so a scene authored
        // against the old womb points at names that no longer exist. NC's loader does this:
        //
        // val = val.Find(childNode.Attributes["parentPath"].Value);
        // if (val == null) continue; // <-- constraint SILENTLY DROPPED
        //
        // so the user's parenting just vanishes, with no error and nothing left to repair afterwards.
        // The fix therefore has to land BEFORE NC parses: prefix LoadSceneGeneric, rewrite the paths in
        // the XmlNode, and let NC resolve them normally. Once the scene is re-saved it carries the new
        // path and this never runs for it again — a one-time migration, not a permanent translation
        // layer.
        //
        // Deliberately conservative: it only touches a path that (a) belongs to one of OUR wombs and
        // (b) does NOT currently resolve. A path that still works is never rewritten, and a path with a
        // segment we cannot account for — a PRUNED bone, which no longer exists in any form — is left
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
            // DO NOT go through Init() here. It latches `_tried` on its first call, and BepInEx loads us
            // BEFORE NodesConstraints (verified: LiquidWobbleMPB at chainloader line 158, NC at 170), so
            // calling it from Awake resolved nothing and then poisoned the bridge for the whole session.
            // Resolve independently and keep retrying until NC actually exists.
            if (_ncType == null)
            {
                var found = FindNcType();
                if (found == null)
                {
                    // Not loaded YET, or not installed at all. Say so once, late, so "no migration" is
                    // never silent — that silence is exactly what hid this failure the first time.
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
        // these is provably a womb path we broke; any other unresolvable path (a missing modded
        // accessory, a third-party item) is NC's ordinary silent drop and none of our business.
        private static readonly System.Collections.Generic.HashSet<string> PrunedWombBones =
            new System.Collections.Generic.HashSet<string> {
                "cf_d_ana", "cf_j_ana", "cf_s_ana", "cf_j_spine02", "cf_s_spine02",
                "cf_d_siri_L", "cf_d_siri_R", "cf_d_siri01_L", "cf_d_siri01_R",
                "cf_j_siri_L", "cf_j_siri_R", "cf_s_siri_L", "cf_s_siri_R" };

        private static string MigratePath(Transform root, string saved, out string failedSegment)
        {
            failedSegment = null;
            if (root == null || string.IsNullOrEmpty(saved)) return null;
            if (root.Find(saved) != null) return null;                 // still resolves — leave it alone
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
                // OUR components are not attached yet, so GetComponentInChildren<WombExpandEffect> was
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
                    // changed third-party items) and loudly told the user a womb bone was removed -
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
        private static FieldInfo _fConstraints;     // NodesConstraints._constraints (List<Constraint>)
        private static FieldInfo _fChildTransform;  // Constraint.childTransform
        private static FieldInfo _fParentTransform; // Constraint.parentTransform
        private static Harmony _ncHarmony;          // patches the NC pairing-change methods (add / enable-disable / delete)
        private static bool _pairingHooksTried;     // install-once guard for the pairing hooks

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
            if (_ncType == null || Instance() == null) return;   // NodesConstraints not loaded yet -> retry on a later call
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
                // A body rebuild replaces the material the renderer draws, so the clothing alpha mask
                // has to be re-pushed onto the new one (b957).
                BodyMaterialFix.AdoptRenderedBodyMaterial(cc, "after an uncensor body rebuild");
            }
            catch { }
        }
    }

    /// The permanent invisible-torso fix. ME reads materials through renderer.materials - Unity's
    /// INSTANCING getter, which clones every material on a renderer - and its lookup walks EVERY
    /// renderer of the character, so one ME call also clones the BODY material. The game keeps
    /// writing the clothing alpha mask to ChaControl.customMatBody, now an orphan nobody renders:
    /// undressing clears a mask the screen never sees and the clone keeps the one it was born
    /// with. ME repairs this for its own copies but only when sharedMaterials.Length > 1, which
    /// skips a male whose body carries no copies. The cure is adoption: hand customMatBody the
    /// material that is actually rendered, once - the game then writes straight to the screen.
    internal static class BodyMaterialFix
    {
        private static Harmony _harmony;
        private static bool _tried;
        private static bool _membersMissingLogged;
        private static readonly System.Collections.Generic.HashSet<int> _touched = new System.Collections.Generic.HashSet<int>();

        /// Characters we have run ME calls on — the only ones we repair (never a global side effect).
        public static void MarkTouched(Component cc)
        {
            if (cc == null) return;
            _touched.Add(cc.GetInstanceID());
            TryInstall();
        }


        /// One-shot: copy the game's live mask/_alpha_a/_alpha_b onto the rendered clone, then
        /// re-point ChaControl.customMatBody at it. No polling afterwards; ME's edits on the clone
        /// survive; no-op when nothing was cloned; loud error (and no change) if the field cannot
        /// be re-pointed. Clearing the clone's mask instead would leave the torso unmasked under
        /// clothes, and undress-clone-redress would race the game's own clothes coroutines.
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
                    LiquidWobbleMPBPlugin._logger?.LogError("CloXray: ChaControl.customMatBody could not be re-pointed (no writable field or property) - the game keeps writing the body mask to a material nobody renders, so undressing may leave the torso hidden.");
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
            }
            return false;
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
        // property depending on game version, so walk the whole chain and accept both (b956 asked
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

        // Return false to SKIP BP's AddDanConstraints. Only for the by-name re-add path (both parents null)
        // when the male's k_f_dan_end is already constrained.
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
        public static float MaxRange = 0.15f;

        // Subscribe once to KKAPI CharacterApi.CharacterReloaded on every loaded copy.
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
            NodeConstraintBridge.InstallPairingHooks();   // instant womb<->penis re-pair on any k_f_dan_entry NodesConstraint add/enable/disable/delete
        }

        // A scene LOAD and a character REPLACEMENT both arrive as CharacterReloaded, but they need opposite
        // handling.
        private static bool _sceneLoading;
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
            if (!LiquidWobbleMPBPlugin.CfgEnabled) return;   // master toggle OFF -> never stamp materials
            if (cc == null) return;
            // Two anatomy probes: the BP vagina root AND the vanilla crotch bone (excluding any womb item's
            // own copy).
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
            int st = best.OrganStencil();
            // overwriteExisting=false: on LOAD/reload, ensure the reveal exists but never re-stamp an
            // existing copy's stencil.
            if (MEBridge.EnsureBodyReveal(cc, st, Debug, false))
                best.OnBodyRevealApplied();   // restore out-of-body interior+cum (new-spawn default hides them)
            if (LiquidWobbleMPBPlugin.CfgBodyVeil) MEBridge.EnsureBodyVeil(cc, st + 1, Debug, false);
            if (LiquidWobbleMPBPlugin.CfgClothesReveal) MEBridge.EnsureClothesReveal(cc, st, Debug);
            if (LiquidWobbleMPBPlugin.CfgClothesReveal && !MainGameWomb.IsStudio) MEBridge.EnsureAccessoryReveal(cc, st, Debug);   // Free-H: accessories dress the card — stamp them too
            // Bracket the apply: with the AFTER REMOVE audit this says whether a missing bodypart was
            // already missing before we touched the character, or appeared across our own edit.
            MEBridge.AuditGeometry(cc, "AFTER APPLY");
            if (Debug) MEBridge.DumpMaskState(cc, "AFTER APPLY");

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
            WombExpandEffect.RequestRepair();   // re-pair the womb to the penis on the current links
            WobbleSceneController.DeferBpRebind(w, cc);   // BP cleared her collision agent on the body reload - re-bind once BP settles
        }

        // Where the penis entry is pinned on the receiver: whichever of her BP entry bones the womb
        // actually sits at. A womb placed in the anal slot anchors to her anal root, not her vagina.
        // Falls back to the vanilla crotch bone when she carries no BP bones at all.

        private static Transform EntryAnchorFor(WombExpandEffect w, Component receiver, Transform ourEntry)
        {
            if (receiver == null || w == null) return null;
            Vector3 mouth = w.EntranceWorld();
            string wn = w.name;

            // Seated in one of her BetterPenetration orifices?
            var seats = new System.Collections.Generic.List<KeyValuePair<float, Transform>>();
            foreach (var nm in BpOrifices)
            {
                Transform b = FindChild(receiver.transform, nm);
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
                if (kv.Key > OrificeSeatRange) break;                 // not seated in this one, nor any farther one
                if (AnchorTakenBy(kv.Value, ourEntry)) continue;      // that orifice already has a penis
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

            // No canal marker = the zipmod does not match this DLL (they ship together). The old
            // behavior silently anchored the penis at her nearest orifice - a fallback, and this
            // project does not do fallbacks: loud error, nothing anchored.
            LiquidWobbleMPBPlugin._logger?.LogError("CloXray: womb '" + wn + "' has NO clo_canal_entry marker - the womb zipmod does not "
                + "match this DLL. Update the zipmod; the penis entry is NOT anchored.");
            return null;
        }

        // BP's own entry targets: the vagina and the anus.
        private static readonly string[] BpOrifices = { VaginaBone, "cf_J_Ana_Root" };
        private const float OrificeSeatRange = 0.10f;   // beyond this the womb is not sitting in that orifice

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
            if (Debug) MEBridge.DumpBodyState(cc, "pre-stamp");   // material-state dump: diagnostics only
            CaptureWearer(w, cc);   // remember her body for a later character replacement
            int st = w.OrganStencil();
            if (MEBridge.EnsureBodyReveal(cc, st, true, true))
                w.OnBodyRevealApplied();   // restore out-of-body interior+cum (new-spawn default hides them)
            if (LiquidWobbleMPBPlugin.CfgBodyVeil) MEBridge.EnsureBodyVeil(cc, st + 1, true, true);
            if (LiquidWobbleMPBPlugin.CfgClothesReveal) MEBridge.EnsureClothesReveal(cc, st, true);
            if (LiquidWobbleMPBPlugin.CfgClothesReveal && !MainGameWomb.IsStudio) MEBridge.EnsureAccessoryReveal(cc, st, true);   // Free-H: accessories dress the card - stamp them too
            // b937: the vanishing-garment report happens right after the HOTKEY, and this path never
            // audited - only the load path did. The audit now also detects the invisible-pairing case.
            MEBridge.AuditGeometry(cc, "AFTER HOTKEY APPLY");
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
            WombExpandEffect.RequestRepair();   // re-pair the womb to the penis on the current links
            WobbleSceneController.DeferBpRebind(w, cc);   // BP cleared her collision agent on the body reload - re-bind once BP settles
        }

        // Manual hotkey: apply now to every character that has a womb within MaxRange of its vagina (covers
        // the initial placement, where no reload event fires).
        public static void ApplyAll()
        {
            if (!LiquidWobbleMPBPlugin.CfgEnabled) return;   // master toggle OFF -> hotkey does nothing
            AttachLiquidWobbleSelected();      // bottles etc.: attach the wobble driver to the SELECTED item(s) only
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
                // AUTO penis: x-ray + aim the PENETRATOR (the OTHER character that has a penis) at THIS womb. No
                // selection needed, so it can't grab the receiver's own penis or duplicate across both partners.
                ApplyPenisForWomb(w, cc, true);   // hotkey = an explicit request to wire this pair up
            }
            WombExpandEffect.RequestRepair();   // the hotkey may have added/aimed NC links -> re-pair every womb to its penis now
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
            BodyMaterialFix.MarkTouched(cc);   // b957: her body materials get cloned by ME too - keep her mask following the game
            if (MEBridge.EnsureBodyReveal(cc, st, Debug, true))
                w.OnBodyRevealApplied();   // restore out-of-body interior+cum (new-spawn default hides them)
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
                    // b957: our ME calls make ME clone his materials (renderer.materials), which orphans
                    // the game's body material - register him so the clothing alpha mask keeps
                    // following the game, and repair right away in case he is already undressed.
                    BodyMaterialFix.MarkTouched(male);
                    MEBridge.EnsurePenisOrgInside(male, st, Debug, LiquidWobbleMPBPlugin.CfgHPenisOutside,
                                                  LiquidWobbleMPBPlugin.CfgHPenisBottomWindow ? 1f : 0f);
                    MEBridge.EnsureBallsStamp(male, st, Debug);   // b934: occluder stamp - balls block the window like a hand, plain geometry everywhere
                    BodyMaterialFix.AdoptRenderedBodyMaterial(male, "right after our apply");
                    KPlugBridge.ReassertDickMaterials(male);          // our copies shift the indices KPlug addresses by
                    // KPlug INSTALLED, not KPlug currently-owning. Core.useCustomDick only turns true the
                    // first time KPlug configures a dick, and on some setups our apply runs BEFORE that -
                    // a reporter's log showed the bridge bound and hooked but zero captures, so nothing was
                    // ever restored and he got the raw interaction. Capturing early is harmless: it is
                    // first-write-wins and the restore only fires if KPlug later rewrites the materials.
                    if (KPlugBridge.Present)
                        MEBridge.CapturePenisLook(male, Debug);
                    MainGameWomb.AttachPenisAim(w, male, cc);   // pin BP's inner limit at the womb's penis_target
                    LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: main-game penis x-ray on '" + male.name + "' (stencil " + st + ").");
                    if (Debug) MEBridge.DumpXrayChain(male, cc, w);   // b511: KK-vs-KKS chain diff (penis invisible in KKS womb)
                }
                else LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: no male with a penis material within 2m of the womb — penis not x-rayed.");
            }
        }

        // Re-apply ONLY the penis x-ray copies for a given womb+male (b512): a body reload (the BP5
        // uncensor force, or a user uncensor/outfit change) destroys the penis renderer's instanced
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
            // so nothing restores it. Same defect we fixed for her body; the penis lives under
            // ObjectType.Character too, so the same body/face re-apply covers it. Before our copies,
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
            MEBridge.EnsureBallsStamp(male, st, Debug);   // b934: occluder stamp - balls block the window like a hand, plain geometry everywhere
            KPlugBridge.ReassertDickMaterials(male);          // our copies shift the indices KPlug addresses by
            LiquidWobbleMPBPlugin._logger?.LogInfo("AutoBodyReveal: penis x-ray RE-APPLIED on '" + male.name + "' after a body reload (stencil " + st + ") — the reload wiped the instanced copies.");
            if (Debug) MEBridge.DumpXrayChain(male, null, w);
        }

        // Same story for HER body. The BP5 body-uncensor force reloads the body mesh, which wipes the
        // instanced cf_m_body .MECopy x-ray materials.
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
                Transform end = NearestPenisEnd(all, target.position, receiver, out penetrator, target);   // the OTHER character's penis, if unclaimed
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
                CapturePenetrator(w, penetrator);   // remember his penis uncensor for a later male replacement
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
                MEBridge.EnsurePenisOrgInside(penetrator, st, true);   // x-ray the penetrator's penis, matched to THIS womb
                MainGameWomb.DumpBPDanOptions(penetrator);             // log this male's BP DanOptions (harvest for the Free-H override)
                if (NodeConstraintBridge.Available)
                {
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
                    if (holder != null && holder != myTarget) continue;   // another womb already owns this penis
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
