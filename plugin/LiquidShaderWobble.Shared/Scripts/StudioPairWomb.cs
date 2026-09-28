using System;
using System.Collections.Generic;
using UnityEngine;

namespace LiquidWobbleMPB
{
    /// <summary>
    /// STUDIO one-key setup. Select the girl, press the hotkey: if she has no womb one is
    /// added for her, parented to her and left at a zero local transform, and the whole x-ray stack
    /// is applied - the same work the old hotkey does once a womb exists, minus the find-the-item-
    /// and-place-it-in-her step. Pressing again walks her through the stencil pairs so a second (or
    /// third, fourth) girl in the same scene gets her own pair without opening Material Editor;
    /// after the last pair the x-ray comes off her again, and the next press starts over at the first.
    /// The womb ITEM is never deleted by the hotkey - it may carry animation or hand-made material
    /// edits, so removing it stays a deliberate act in the workspace.
    ///
    /// PLACEMENT: the womb attaches to her `a_n_kokan` accessory point (a character's own tree node
    /// refuses children) at a zero local transform - the same pose a hand-placed womb has, so no
    /// seat maths - and from there it travels with her when she is moved or posed.
    ///
    /// WHAT A "PAIR" IS: one low-5 stencil region value `st` (the girl's body stamp and her womb's
    /// shell) plus `st+1` (her interior/orginside). Everything else is derived - `_ProtectRef` is
    /// `32 | (st+1)`, since that pass reads the low 5 bits and writes bit5. Two girls on the same
    /// pair would see each other's organs through their own bodies, which is what the pairs prevent.
    /// </summary>
    internal static class StudioPairWomb
    {
        // Pairs 4/5, 8/9, 12/13, 16/17. Region 31 is the reserved limb sentinel; 19-30 are free but
        // the shipped slider/manifest ranges stop at four pairs, so four is what this cycles.
        private static readonly int[] Pairs = { 4, 8, 12, 16 };
        // The womb is identified by its BUNDLE, never by the 8205/1/1 numbers in the shipped CSVs:
        // Sideloader re-maps modded studio item IDs at runtime, so those numbers address an
        // unrelated vanilla item in the live game. Studio's own item table is the authority.
        private const string WombBundlePath = "studio/clo/clo_xraywomb.unity3d";
        private const string WombFileName   = "clo_xraywomb";
        // THE ANAL WOMB is item 2 - the one without ovaries - with its uterus collapsed by `remove_womb`, which
        // leaves the plain canal an anus is. It hangs in her a_n_ana point, the anal twin of a_n_kokan.
        private const string AnalBundlePath  = "studio/clo/clo_xraywomb2.unity3d";
        private const string AnalFileName    = "clo_xraywomb2";

        // Where each girl sits in the cycle: instance id -> index into Pairs. Absent = nothing applied
        // by this hotkey yet. Session state only; a saved scene carries the RESULT (her ME-persisted
        // stamp), not the cycle position, and LoadSync below re-derives the womb side from it.
        private static readonly Dictionary<int, int> _step = new Dictionary<int, int>();

        // On-screen line for anything the hotkey does. BepInEx puts a LogLevel.Message on screen by
        // itself - the same call the 1.2 line uses - so there is no GUI code of ours to keep working.
        // _log, NOT _logger: _logger is the diagnostic sink and is null in a shipped build, and this
        // text is on-screen feedback, not bug-report material.
        private static void Say(string s)
        {
            LiquidWobbleMPBPlugin._log?.LogMessage("CloXray: " + s);
        }

        /// ONE HOLE PER PRESS. The pair key sets up the womb on her VAGINA, the anal key the one at her ANUS, and
        /// any other womb - one she wears somewhere else, or one standing on its own in the scene - is set up by
        /// SELECTING that womb and pressing the pair key. Nothing else in the scene is touched either way.
        internal static void Trigger(bool anal = false)
        {
            if (!LiquidWobbleMPBPlugin.CfgEnabled || !MainGameWomb.IsStudio) return;

            // A WOMB ITEM in the selection means that womb and no other. Her orifices say nothing about where it
            // sits, so the penis aimed nearest its aim bone is the one meant for it.
            if (!anal)
            {
                WombExpandEffect picked = SelectedWomb();
                if (picked != null) { ApplyToWomb(picked); return; }
            }

            Component girl = SelectedFemale();
            if (girl == null)
            {
                Say(anal ? "select a female character first, then press the anal womb hotkey."
                         : "select a female character - or one womb item - first, then press the pair hotkey.");
                return;
            }

            WombExpandEffect hers = HerWomb(girl, anal);
            int key = girl.GetInstanceID();
            int step;
            if (anal)
            {
                // The anal key never walks the stencil cycle - her pair belongs to her body and the pair key owns
                // it. It adopts whatever she is on, so both her wombs show through the same window.
                step = HerPairIndex(girl);
                if (step < 0) step = FirstFreePairIndex(girl);
                if (step < 0) step = 0;
            }
            else if (!_step.TryGetValue(key, out step))
            {
                // _step is per SESSION, so after a scene load every girl reads as a first press even
                // though the scene already gave her a pair. ADOPT that pair rather than hunting for a
                // free one: it keeps her exactly where the scene put her (the first press then re-applies
                // - which is how a saved setup gets upgraded - and later presses cycle as always), and it
                // is the only thing that works at all once every pair is spoken for.
                step = HerPairIndex(girl);
                if (step < 0)
                {
                    // Genuinely new to the x-ray: lowest pair no OTHER character is using, so a second
                    // girl gets her own with nothing to think about. Past the fourth girl
                    // there is nothing left to hand out - share pair 1 rather than refuse. Two girls on
                    // one pair read the same stencil values, so their windows can show through each
                    // other where they overlap on screen; a press cycles her off it like any other pair.
                    step = FirstFreePairIndex(girl);
                    if (step < 0)
                    {
                        step = 0;
                        LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: all " + Pairs.Length
                            + " stencil pairs are taken - '" + girl.name + "' shares pair 1. Overlapping windows can bleed between the two.");
                    }
                }
            }
            else
            {
                step++;
                if (step >= Pairs.Length)   // walked off the end -> tear the setup down, next press starts over
                {
                    _step.Remove(key);
                    RemoveSetup(girl);
                    Say("x-ray off '" + girl.name + "' (her womb is untouched). Press again for pair 1.");
                    return;
                }
            }

            if (hers == null)
            {
                // A freshly added item has no WombExpandEffect yet: it is attached from the baked
                // component's Start(), which Unity runs on the NEXT frame. Add now, finish in Tick().
                var oci = AddWombFor(girl, anal);
                if (oci == null) return;   // AddWombFor logged the reason; nothing applied (no fallback)
                _pendingItem = oci; _pendingGirl = girl; _pendingPair = step; _pendingAnal = anal;
                _pendingUntil = Time.unscaledTime + 3f;
                Say("adding " + (anal ? "an anal womb" : "a womb") + " for '" + girl.name + "'...");
                return;
            }

            if (!SetPairAll(girl, Pairs[step], null))
            {
                Say("could not set the stencil pair on '" + girl.name + "'s womb - see the log.");
                return;
            }
            if (!anal) _step[key] = step;

            ApplyWithNote(girl, hers, false);
            Say("'" + girl.name + "' -> " + (anal ? "anal womb, " : "") + "pair " + (step + 1) + " of " + Pairs.Length
                + " (stencil " + Pairs[step] + "/" + (Pairs[step] + 1) + ")"
                + (AutoBodyReveal.PenisNote != null ? ", " + AutoBodyReveal.PenisNote : "")
                + (AutoBodyReveal.PenisUpgraded ? ", old single-material penis UPGRADED" : "")
                + (anal ? ". Her pair belongs to the pair hotkey." : ". Press again for the next pair."));
        }

        // A womb just requested from Studio, waiting for its own component to come up.
        private static Studio.ObjectCtrlInfo _pendingItem, _pendingParent; private static Component _pendingGirl;
        private static int _pendingPair; private static float _pendingUntil; private static bool _pendingAnal;

        /// Called every Studio frame. Finishes an add started by Trigger() as soon as the item's
        /// WombExpandEffect exists - normally the very next frame, since the baked component attaches
        /// it from Start(). Gives up loudly (and removes the item) rather than leaving a half-made
        /// womb in the scene.
        internal static void Tick()
        {
            if (_pendingItem == null) return;
            var tr = _pendingItem.guideObject != null ? _pendingItem.guideObject.transformTarget : null;
            WombExpandEffect w = tr != null ? tr.GetComponentInChildren<WombExpandEffect>(true) : null;
            if (w == null && tr != null)
                w = LiquidWobbleMPBPlugin.EnsureWombExpand(tr);             // Start() has not run yet - do its job now
            if (w == null)
            {
                if (Time.unscaledTime < _pendingUntil) return;              // still coming up
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: the womb item never came up as a CloXray womb - removing it. "
                    + "Check that [Clo]XrayWomb1.zipmod matches this plugin version.");
                DeleteNode(_pendingItem);
                Say("could not add a womb - see the log.");
                _pendingItem = null; _pendingGirl = null; _pendingParent = null;
                return;
            }
            // THE COMPONENT EXISTING IS NOT THE WOMB BEING READY. EnsureWombExpand above adds it on the spot, and its
            // Start() - which finds the mesh - runs a frame later. Finishing on that first frame read a womb with no
            // mesh: remove_womb "not on this womb" and no clo_canal_entry, so the anal key left the uterus up and
            // never anchored the penis entry - the log showed the setup finishing before the womb reported ready. A vaginal womb
            // hid it, because its entry anchor is HER bone rather than the womb's own marker.
            if (!w.Ready)
            {
                if (Time.unscaledTime < _pendingUntil) return;              // Start() has not run yet
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: the new womb never finished starting (its mesh was not"
                    + " resolved - see the WombExpandEffect error above) - removing it rather than leaving a half-made womb.");
                DeleteNode(_pendingItem);
                Say("could not add a womb - see the log.");
                _pendingItem = null; _pendingGirl = null; _pendingParent = null;
                return;
            }

            // Nest her womb under her now that Studio has finished registering the node, then zero
            // the LOCAL transform - in that order, since a re-parent preserves world position.
            if (_pendingParent != null && _pendingParent.treeNodeObject != null && _pendingItem.treeNodeObject != null)
            {
                // The full sequence StudioCustomMasking uses to nest an object it created: re-parent,
                // open the parent node, then refresh the workspace. A bare SetParent leaves the node
                // at the tree root, so the trailing two calls are not cosmetic.
                var wombNode = _pendingItem.treeNodeObject;
                string slot = _pendingAnal ? AnalPointKey : AttachPointKey;
                var parentNode = FindAttachPoint(_pendingParent as Studio.OCIChar, slot);
                if (parentNode == null)
                {
                    LiquidWobbleMPBPlugin._logger?.LogError("CloXray: '" + slot
                        + "' is not among her attach points - leaving the womb where it is.");
                    parentNode = _pendingParent.treeNodeObject;   // logs the refusal below, changes nothing
                }
                Studio.Studio.Instance.treeNodeCtrl.SetParent(wombNode, parentNode);
                parentNode.SetTreeState(Studio.TreeNodeObject.TreeState.Open);
                RefreshWorkspaceUI();
                bool took = wombNode.parent == parentNode
                         || (parentNode.childRoot != null && wombNode.parent == parentNode.childRoot);
                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: nesting the womb under '"
                    + parentNode.textName + "' -> " + (took ? "done" : "REFUSED"));
                if (!took)
                    LiquidWobbleMPBPlugin._logger?.LogError("CloXray: Studio would not nest the womb under '"
                        + _pendingParent.treeNodeObject.textName + "'. The x-ray still works; drag it onto her in the "
                        + "workspace if you want it to travel with her.");
            }
            // PLACEMENT: zero, exactly as a hand-placed womb has it. The item's own bones already
            // seat the mesh at the attach point, so sitting AT a_n_kokan with no offset is the correct
            // pose - the same thing the manual tells people to do by hand. This has to be written
            // AFTER the attach: Studio's OnAttach preserves the item's world pose by converting it
            // into attach-point space, which would otherwise leave a bogus offset and rotation.
            if (_pendingItem.guideObject != null)
            {
                _pendingItem.guideObject.changeAmount.pos = _pendingAnal ? Vector3.zero : VaginalSpawnPos;
                _pendingItem.guideObject.changeAmount.rot = Vector3.zero;
            }

            var girl = _pendingGirl; int step = _pendingPair; bool anal = _pendingAnal;
            _pendingItem = null; _pendingGirl = null; _pendingParent = null; _pendingAnal = false;
            if (girl == null) return;
            if (anal)
            {
                EnsureCollapsed(w);
                // THE ONE THING THE ANAL KEY RELIES ON: the womb it just added has to read as being in her
                // a_n_ana point, because that - and only that - is how the next press recognises it. If the
                // attach did not take, say so here rather than let every press quietly add another womb.
                if (!AutoBodyReveal.InAccessorySlot(w, AnalPointKey, girl))
                    LiquidWobbleMPBPlugin._logger?.LogError("CloXray: the new anal womb is NOT in '" + girl.name
                        + "'s " + AnalPointKey + " point after the attach, so the next press would add another one."
                        + " Attach it to her " + AnalPointKey + " by hand, or delete it and check the log above for"
                        + " why Studio refused the nesting.");
            }
            if (!SetPairAll(girl, Pairs[step], w)) { Say("could not set the stencil pair - see the log."); return; }
            if (!anal) _step[girl.GetInstanceID()] = step;
            ApplyWithNote(girl, w, false);
            Say("'" + girl.name + "' -> " + (anal ? "anal womb added, " : "womb added, ") + "pair " + (step + 1) + " of " + Pairs.Length
                + " (stencil " + Pairs[step] + "/" + (Pairs[step] + 1) + ")"
                + (SharedPair(girl, step) ? " SHARED - windows can bleed where they overlap" : "")
                + (AutoBodyReveal.PenisNote != null ? ", " + AutoBodyReveal.PenisNote : "")
                + (AutoBodyReveal.PenisUpgraded ? ", old single-material penis UPGRADED" : "")
                + (anal ? ". Her pair belongs to the pair hotkey." : ". Press again for the next pair."));
        }

        /// An anus is a canal, not a uterus, so the anal key collapses the dome on the womb IT ADDS - and only on
        /// that one. A womb already at her anus is the author's, shaped how they shaped it, and the key leaves it
        /// alone - the key's job there is to add a womb at her anus when she has none, not to reshape one she has.
        /// Only item 2 carries the channel; on anything else it says so rather than leaving a silent uterus there.
        private static void EnsureCollapsed(WombExpandEffect w)
        {
            if (w == null) return;
            float cur = w.HandShape(WombExpandEffect.CollapseShape);
            if (cur < 0f)
            {
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: womb '" + w.name + "' has no '"
                    + WombExpandEffect.CollapseShape + "' channel, so its uterus stays where her anus is. That channel"
                    + " is on Xray Womb 2 - use that item for her anus, or update [Clo]XrayWomb1.zipmod.");
                return;
            }
            if (cur >= 99f) return;
            if (w.SetHandShape(WombExpandEffect.CollapseShape, 100f))
                LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: womb '" + w.name + "' is her anal womb - collapsed its uterus ("
                    + WombExpandEffect.CollapseShape + " was " + cur.ToString("F0") + ", now 100).");
        }


        // ---- selection / lookup ---------------------------------------------------------------

        // The selected girl AND the Studio object she is - both, from the one lookup. Do NOT derive
        // her object afterwards by walking transforms: a character sitting in a FOLDER matches the
        // folder too (its transform is her ancestor), which parents the womb to the folder's node
        // instead of hers.
        private static Studio.OCIChar _selectedOci;
        // Where the womb hangs off her. Characters do NOT accept tree children themselves
        // (their node has enableAddChild=false); items attach to one of the ACCESSORY POINT
        // nodes Studio builds underneath them.
        private const string AttachPointKey = "a_n_kokan";   // the crotch point ("Group: Crotch"); a_n_waist rides the hips
        private const string AnalPointKey   = "a_n_ana";     // her anus point, where the anal womb hangs
        private static readonly Vector3 VaginalSpawnPos = new Vector3(0f, 0f, 0.005f);   // Studio position of a new vaginal womb in a_n_kokan (+Z = her front)
        private static Component SelectedFemale()
        {
            _selectedOci = null;
            try
            {
                foreach (var oci in KKAPI.Studio.StudioAPI.GetSelectedObjects())
                {
                    var och = oci as Studio.OCIChar;
                    if (och == null || och.charInfo == null) continue;
                    if (MainGameWomb.IsMaleChara(och.charInfo)) continue;
                    _selectedOci = och;
                    return och.charInfo;
                }
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: reading the Studio selection failed: " + e.Message); }
            return null;
        }

        /// A womb ITEM in the Studio selection, if that is what is selected. The pair key then works on that womb
        /// alone - the one way to set up a womb that is on neither of her orifices, or on nobody at all.
        private static WombExpandEffect SelectedWomb()
        {
            try
            {
                foreach (var oci in KKAPI.Studio.StudioAPI.GetSelectedObjects())
                {
                    // An ITEM, never a character: her wombs hang UNDER her, so a selected girl would answer with
                    // one of them and take the whole key down the wrong path.
                    var item = oci as Studio.OCIItem;
                    var tt = (item != null && item.guideObject != null) ? item.guideObject.transformTarget : null;
                    var w = tt != null ? tt.GetComponentInChildren<WombExpandEffect>(true) : null;
                    if (w != null) return w;
                }
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: reading the Studio selection failed: " + e.Message); }
            return null;
        }

        /// Her womb of one kind - the one the key is allowed to manage.
        /// ANAL: the womb hanging in her a_n_ana point, and nothing else. Where a womb SITS cannot answer this
        /// question: her anus is 4.3cm from her vagina, so a womb she wears anywhere on that half of her reads as
        /// "nearer the anus", and the key adopted a lone womb she wore elsewhere instead of adding one at her
        /// anus. A distance limit cannot answer it either, because a lone womb worn elsewhere on that half of her
        /// passes any such test. The slot is placement the author chose, and it is
        /// where this key puts the womb it adds, so it is the one honest answer.
        /// VAGINAL: the a_n_kokan point OR a womb seated on her vagina, because scenes older than this key have
        /// wombs put in her vagina by hand and a slot-only rule would add a second one on top of them.
        /// A womb she wears anywhere else belongs to neither key - select that womb and press the pair key.
        private static WombExpandEffect HerWomb(Component girl, bool anal)
        {
            Transform bone = AutoBodyReveal.OrificeBone(girl, anal);
            string slot = anal ? AnalPointKey : AttachPointKey;
            WombExpandEffect best = null; float bestD = float.MaxValue; bool bestInSlot = false;
            foreach (var w in WombsFor(girl))
            {
                if (w == null) continue;
                bool inSlot = AutoBodyReveal.InAccessorySlot(w, slot, girl);
                if (anal)
                {
                    if (!inSlot) continue;
                }
                else
                {
                    if (AutoBodyReveal.IsAnalWomb(w, girl)) continue;
                    if (!inSlot && (bone == null
                        || Vector3.Distance(bone.position, w.EntranceWorld()) > AutoBodyReveal.OrificeSeatRange)) continue;
                }
                float d = bone != null ? Vector3.Distance(bone.position, w.EntranceWorld()) : 0f;
                if (d < bestD) { bestD = d; best = w; bestInSlot = inSlot; }
            }
            if (best != null)
                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: '" + girl.name + "'s " + (anal ? "anal" : "vaginal")
                    + " womb is '" + best.name + "' (" + (bestInSlot ? "in her " + slot + " point" : "not in " + slot)
                    + ", " + (bone != null ? (bestD * 100f).ToString("F1") + "cm from " + bone.name : "no orifice bone") + ").");
            return best;
        }

        /// The apply itself: takeover on, note addressed to this girl, one womb only.
        private static void ApplyWithNote(Component girl, WombExpandEffect w, bool claimByTip)
        {
            AutoBodyReveal.PenisNote = null; AutoBodyReveal.PenisUpgraded = false;
            // Takeover: this key promises a finished setup, so a penis held by a stray hand-made
            // constraint is claimed rather than skipped (other wombs' penises stay untouchable).
            AutoBodyReveal.PenisTakeover = true; AutoBodyReveal.PenisNoteFor = girl;
            MEBridge.UpgradeStack = true;   // this key opts the character into the sub-2500 stack
            try
            {
                AutoBodyReveal.ApplyAll(girl, w, claimByTip);
                // Her pair is her body's, so her OTHER wombs (and the penises they hold) come along with it.
                if (girl != null) AutoBodyReveal.SyncPairAcross(girl, w);
            }
            finally { AutoBodyReveal.PenisTakeover = false; AutoBodyReveal.PenisNoteFor = null; MEBridge.UpgradeStack = false; }
        }

        /// The pair key with a womb SELECTED. That womb alone: its wearer, if it has one, is stamped and the womb
        /// takes her pair, but her other wombs are left exactly as they are. The penis is the one aimed nearest
        /// its aim bone, since nothing about her placement says which one belongs here.
        private static void ApplyToWomb(WombExpandEffect w)
        {
            string how;
            Component wearer = AutoBodyReveal.ResolveWearer(w, out how);
            if (wearer != null)
            {
                int idx = HerPairIndex(wearer);
                if (idx < 0) idx = FirstFreePairIndex(wearer);
                if (idx < 0) idx = 0;
                if (!SetPair(w, Pairs[idx])) { Say("could not set the stencil pair on '" + w.name + "' - see the log."); return; }
            }
            ApplyWithNote(wearer, w, true);
            Say("womb '" + w.name + "'" + (wearer != null ? " on '" + wearer.name + "'" : " (worn by nobody)")
                + " -> the penis aimed nearest it"
                + (AutoBodyReveal.PenisNote != null ? ", " + AutoBodyReveal.PenisNote : "")
                + (AutoBodyReveal.PenisUpgraded ? ", old single-material penis UPGRADED" : "") + ".");
        }

        /// EVERY womb already serving this girl. Deliberately the SAME resolver the apply path uses,
        /// which matches by parenting and by proximity - so a womb someone dropped near her but not
        /// exactly in the slot counts as hers and no second one is added.
        /// A girl can wear more than one (Womb 1 + Womb 2 in the same scene is a normal setup), and
        /// they all have to be handled together - see SetPairAll.
        private static List<WombExpandEffect> WombsFor(Component girl)
        {
            var list = new List<WombExpandEffect>();
            foreach (var w in UnityEngine.Object.FindObjectsOfType<WombExpandEffect>())
            {
                if (w == null) continue;
                string how;
                var cc = AutoBodyReveal.ResolveWearer(w, out how);
                if (cc != null && cc.GetInstanceID() == girl.GetInstanceID()) list.Add(w);
            }
            return list;
        }

        /// Put ALL of her wombs on one pair. Her BODY carries a single stencil ref, so two
        /// wombs on different pairs cannot both be visible through her - whichever one does not match
        /// her stamp simply stops showing; setting only the first womb found would leave the rest dark.
        private static bool SetPairAll(Component girl, int st, WombExpandEffect extra)
        {
            var list = WombsFor(girl);
            if (extra != null && !list.Contains(extra)) list.Add(extra);
            int ok = 0;
            foreach (var w in list) if (SetPair(w, st)) ok++;
            if (list.Count > 1)
                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: '" + girl.name + "' wears " + list.Count
                    + " wombs - all set to stencil " + st + "/" + (st + 1) + " (her body can only carry one pair).");
            return ok > 0;
        }

        // Is another character already on this pair? Only for the on-screen warning.
        private static bool SharedPair(Component girl, int step)
        {
            foreach (var w in UnityEngine.Object.FindObjectsOfType<WombExpandEffect>())
            {
                if (w == null) continue;
                string how;
                var cc = AutoBodyReveal.ResolveWearer(w, out how);
                if (cc == null || cc.GetInstanceID() == girl.GetInstanceID()) continue;
                if (w.OrganStencil() == Pairs[step]) return true;
            }
            return false;
        }

        // The pair THIS girl is already on, from her own womb, or -1 if she has none. Read from the
        // womb rather than from a material: it is the same value SetPairAll writes and the same one
        // FirstFreePairIndex treats as taken, so the two can never disagree.
        private static int HerPairIndex(Component girl)
        {
            foreach (var w in WombsFor(girl))
            {
                if (w == null) continue;
                int st = w.OrganStencil();
                for (int i = 0; i < Pairs.Length; i++) if (Pairs[i] == st) return i;
            }
            return -1;
        }

        private static int FirstFreePairIndex(Component girl)
        {
            var taken = new HashSet<int>();
            foreach (var w in UnityEngine.Object.FindObjectsOfType<WombExpandEffect>())
            {
                if (w == null) continue;
                string how;
                var cc = AutoBodyReveal.ResolveWearer(w, out how);
                if (cc == null || cc.GetInstanceID() == girl.GetInstanceID()) continue;   // hers doesn't block her
                taken.Add(w.OrganStencil());
            }
            for (int i = 0; i < Pairs.Length; i++) if (!taken.Contains(Pairs[i])) return i;
            return -1;
        }

        // ---- add / remove ---------------------------------------------------------------------

        /// Add "Xray Womb 1" through Studio's own add path (so it appears in the workspace, is
        /// selectable and undoable). Returns the new Studio object; Tick() parents and places it -
        /// the item's CloXray component only exists once its baked Start() has run.
        private static Studio.ObjectCtrlInfo AddWombFor(Component girl, bool anal)
        {
            int group, category, no;
            if (!ResolveWombItemKeys(anal, out group, out category, out no))
            {
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: '" + (anal ? AnalBundlePath : WombBundlePath)
                    + "' is not in Studio's item list - is [Clo]XrayWomb1.zipmod installed? Nothing added.");
                return null;
            }
            Studio.ObjectCtrlInfo girlOci = _selectedOci;   // her own Studio object, from the selection
            if (girlOci == null || girlOci.treeNodeObject == null)
            {
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: '" + girl.name
                    + "' has no Studio workspace node, so a womb cannot be parented to her. Nothing added.");
                return null;
            }
            try
            {
                // The THIRD argument is what actually decides where the node lands. HSPlugins'
                // ParentCharactersToObjects patches exactly this spot, and its injected logic reads
                // `_parentNode == null ? _parent?.treeNodeObject : _parentNode` - so Studio prefers the
                // node handed in here over the ObjectCtrlInfo. Never pass a fresh TreeNodeObject (as
                // the StudioItemIK sample does for its throwaway helpers): that is a detached node,
                // and the item lands at the tree root.
                var item = Studio.AddObjectItem.Load(
                    new Studio.OIItemInfo(group, category, no, Studio.Studio.GetNewIndex()),
                    girlOci, girlOci.treeNodeObject);
                if (item == null)
                {
                    LiquidWobbleMPBPlugin._logger?.LogError("CloXray: Studio refused to add the womb item ("
                        + group + "/" + category + "/" + no + ").");
                    return null;
                }
                // All finish work (attach, placement, pair, apply) lives in Tick(), which waits for
                // the item's own component - attached from its baked Start(), normally next frame.
                _pendingParent = girlOci;
                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: added womb item " + group + "/" + category + "/" + no
                    + " for '" + girl.name + "' - attaching to her next.");
                return item;
            }
            catch (Exception e)
            {
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: adding the womb item failed: " + e);
                return null;
            }
        }

        /// Studio's live item table, keyed group -> category -> id. Modded items are re-numbered by
        /// Sideloader at load, so this is the only trustworthy way to name ours.
        private static bool ResolveWombItemKeys(bool anal, out int group, out int category, out int no)
        {
            group = category = no = -1;
            string bundle = anal ? AnalBundlePath : WombBundlePath, file = anal ? AnalFileName : WombFileName;
            try
            {
                var info = Studio.Info.Instance;
                if (info == null || info.dicItemLoadInfo == null) return false;
                foreach (var g in info.dicItemLoadInfo)
                    foreach (var c in g.Value)
                        foreach (var i in c.Value)
                        {
                            var il = i.Value;
                            if (il == null) continue;
                            if (il.bundlePath == bundle || il.fileName == file)
                            { group = g.Key; category = c.Key; no = i.Key; return true; }
                        }
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: reading Studio's item list failed: " + e.Message); }
            return false;
        }

        /// Her attach-point node for AttachPointKey. Studio maps each of those nodes to a point
        /// index in OCIChar.dicAccessoryPoint, and Info.dicAccessoryPointInfo turns the index into the
        /// a_n_* name - so the slot is found by NAME rather than by position in the tree.
        private static Studio.TreeNodeObject FindAttachPoint(Studio.OCIChar och, string key)
        {
            if (och == null || och.dicAccessoryPoint == null) return null;
            try
            {
                var info = Studio.Info.Instance;
                foreach (var kv in och.dicAccessoryPoint)
                {
                    if (info == null || info.dicAccessoryPointInfo == null) break;
                    Studio.Info.AccessoryPointInfo api;
                    if (!info.dicAccessoryPointInfo.TryGetValue(kv.Value, out api) || api == null) continue;
                    if (api.key == key) return kv.Key;
                }
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: reading her attach points failed: " + e.Message); }
            return null;
        }

        /// Repaint the workspace list after a re-parent. Found by TYPE, not by member name: the
        /// workspace controller is exposed under different names across the games.
        private static void RefreshWorkspaceUI()
        {
            const System.Reflection.BindingFlags ANY = System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            try
            {
                var st = Studio.Studio.Instance; if (st == null) return;
                object wc = null;
                foreach (var f in st.GetType().GetFields(ANY))
                    if (f.FieldType != null && f.FieldType.Name.IndexOf("WorkspaceCtrl", StringComparison.OrdinalIgnoreCase) >= 0)
                    { wc = f.GetValue(st); break; }
                if (wc == null)
                    foreach (var pr in st.GetType().GetProperties(ANY))
                        if (pr.PropertyType != null && pr.PropertyType.Name.IndexOf("WorkspaceCtrl", StringComparison.OrdinalIgnoreCase) >= 0)
                        { wc = pr.GetValue(st, null); break; }
                if (wc == null) { LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: no workspace controller found - the tree may need a click to redraw."); return; }
                var m = wc.GetType().GetMethod("UpdateUI", ANY);
                if (m != null) m.Invoke(wc, null);
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: workspace refresh failed: " + e.Message); }
        }

        private static void DeleteNode(Studio.ObjectCtrlInfo oci)
        {
            try
            {
                if (oci != null && oci.treeNodeObject != null)
                    Studio.Studio.Instance.treeNodeCtrl.DeleteNode(oci.treeNodeObject);
            }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: removing the item failed: " + e.Message); }
        }

        /// End of the cycle: take the x-ray off HER and leave everything else alone. The womb item
        /// stays - it is an object that may carry animation, a re-shape or Material Editor edits,
        /// and a stray keypress must never destroy that. Deleting it is one click in the workspace.
        private static void RemoveSetup(Component girl)
        {
            MEBridge.RemoveXrayCopies(girl, AutoBodyReveal.Debug);
        }

        // ---- the pair itself ------------------------------------------------------------------

        /// Write one pair onto a womb's own materials. Instance materials, matching what the rest of
        /// the womb code edits, so ME's sliders keep pointing at the same objects.
        private static bool SetPair(WombExpandEffect w, int st)
        {
            if (w == null) return false;
            // one writer for the womb item's pair AND stack, so what the hotkey sets is exactly
            // what the scene saves and the loader restores. Keeps the stack it is already on.
            int curSt; bool up;
            AutoBodyReveal.ReadWombState(w, out curSt, out up);
            AutoBodyReveal.ApplyWombState(w, st, up);
            int touched = 0;
            foreach (var r in w.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || r.sharedMaterials == null) continue;
                foreach (var sm in r.sharedMaterials)
                    if (sm != null && sm.shader != null
                        && (sm.shader.name == "CloXray/Organ" || sm.shader.name == "CloXray/OrgInside")) { touched++; break; }
            }
            if (touched == 0)
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: '" + w.name
                    + "' has no CloXray/Organ or /OrgInside material to carry a stencil pair - nothing changed.");
            return touched > 0;
        }

        /// AFTER A SCENE LOAD the two halves of a pair can disagree: MaterialEditor restores HER body
        /// stamp (ME persists those copies), while a womb that nothing spoke for comes back from its
        /// bundle at the shipped 4/5. Her stamp is then the surviving record of which pair the scene
        /// used - copy it back onto the womb rather than inventing scene data.
        ///
        /// BUT a womb that is NOT on the shipped 4 was spoken for: ME persists the womb item's
        /// _StencilBody too, and has restored it by now. A disagreement between her stamp and a womb
        /// carrying a deliberate pair is not damage to repair - it is two saved values, and the scene
        /// is the authority on both. Adopt it and say so; the hotkey is what re-pairs a scene.
        /// Queues are left alone here for the same reason (see ApplyWombState's adoptOnly note).
        internal static void LoadSync(Component girl, WombExpandEffect womb)
        {
            if (girl == null || womb == null || !MainGameWomb.IsStudio) return;
            int bodyRef = MEBridge.ReadBodyStencilRef(girl);
            int wombRef = womb.OrganStencil();
            if (bodyRef <= 0 || bodyRef == wombRef) return;
            bool known = false;
            foreach (int p in Pairs) if (p == bodyRef) { known = true; break; }
            if (!known)
            {
                LiquidWobbleMPBPlugin._logger?.LogError("CloXray: '" + girl.name + "' carries stencil ref " + bodyRef
                    + ", which is not one of the shipped pairs - leaving her womb on " + wombRef + ".");
                return;
            }
            if (wombRef != AutoBodyReveal.ShippedStencilBody)
            {
                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: '" + girl.name + "' loaded on stencil pair " + bodyRef
                    + "/" + (bodyRef + 1) + ", her womb '" + womb.name + "' on " + wombRef + "/" + (wombRef + 1)
                    + " - both were saved with the scene, so the womb keeps what it was given. Press the apply"
                    + " hotkey to put them on one pair.");
                return;
            }
            if (AutoBodyReveal.ApplyWombState(womb, bodyRef, false, true) > 0)
                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: '" + girl.name + "' loaded on stencil pair " + bodyRef
                    + "/" + (bodyRef + 1) + " - her womb came back on the shipped pair, so it was matched to hers.");
        }
    }
}
