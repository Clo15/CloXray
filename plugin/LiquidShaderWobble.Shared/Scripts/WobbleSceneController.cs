using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Studio;
using ExtensibleSaveFormat;
using KKAPI.Utilities;
using KKAPI.Studio.SaveLoad;

namespace LiquidWobbleMPB
{
    /// <summary>
    /// Persists the per-item liquid wobble driver with the Studio scene - the same mechanism ComponentUtil and
    /// MaterialEditor use (a KKAPI <see cref="SceneCustomFunctionController"/> over ExtensibleSaveFormat).
    /// On save it records the scene-ids of items carrying a NON-womb <see cref="LiquidWobbleMPBEffect"/>; on
    /// load it re-attaches to exactly those items. The womb re-attaches itself on spawn, so it's excluded.
    /// </summary>
    public class WobbleSceneController : SceneCustomFunctionController
    {
        private const string ItemsKey = "wobbleItemIds";
        // per-womb {studio object id, stencil pair, upgraded-stack flag}, flattened 3 ints per
        // womb. The womb is a Studio ITEM and this is CloXray's state, not a MaterialEditor edit, so it
        // rides in CloXray's own scene data. Absent = a scene saved before that record existed -> the legacy repair runs.
        private const string WombKey = "wombPairStack";
        // Studio saves nothing about an item's blendshapes, and the anal womb's uterus is collapsed with one
        // (WombExpandEffect.CollapseShape). Without this the dome comes back on the next load. One owner id per
        // womb that carries it; absent = no such womb, exactly as every scene saved before this.
        private const string CollapsedKey = "wombCollapsed";

        // ── Deferred on-load BodyReveal re-apply host ─────────────────────────────────────────────────────
        // AutoBodyReveal.OnCharacterReloaded is a STATIC handler with no coroutine host. Route its re-apply
        // through this SceneController (a MonoBehaviour - already the on-load coroutine host) so it runs AFTER
        // MaterialEditor restores the saved body copy. Otherwise, on a non-default stencil pair, this reload
        // handler can beat ME's and re-derive/stamp the stale default 4 over the saved value.
        private static WobbleSceneController _inst;

        public static void DeferApply(Component cc)
        {
            var host = _inst ?? (_inst = UnityEngine.Object.FindObjectOfType<WobbleSceneController>());
            if (host != null && cc != null) host.StartCoroutine(host.DeferredApply(cc));
            else AutoBodyReveal.ApplyForCharacterNow(cc);   // no host (shouldn't happen) -> immediate fallback
        }

        // Re-make the penis links after a character load/replacement, but only once NodesConstraints has
        // finished restoring (scene load) or re-binding (character change) its own list. Linking during
        // that window adds a second copy of a link it is about to restore - the "scene saved with 2 links
        // opens with 4" bug.
        // One pending re-link per womb: a single replacement fires CharacterReloaded several times and
        // each would otherwise start its own settle-wait. The character is NOT carried through the wait -
        // a replacement destroys and rebuilds the ChaControl during the wait, so a captured reference dies
        // and a null-check would end the coroutine in silence (the observed symptom: 6 triggers, nothing ran).
        // The womb ITEM survives the swap; the character is resolved fresh after the wait.
        private static readonly System.Collections.Generic.HashSet<int> _relinkPending = new System.Collections.Generic.HashSet<int>();
        public static void DeferNodeRelink(WombExpandEffect w, Component cc)
        {
            var host = _inst ?? (_inst = UnityEngine.Object.FindObjectOfType<WobbleSceneController>());
            if (host == null || w == null)
            { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: no scene controller to defer the constraint re-link on - the penis links were NOT re-made. Press the apply hotkey to re-aim."); return; }
            if (!_relinkPending.Add(w.GetInstanceID())) return;   // one settle-wait per womb at a time
            host.StartCoroutine(host.SettleThenRelink(w));
        }

        private System.Collections.IEnumerator SettleThenRelink(WombExpandEffect w)
        {
            // Settled = the constraint count holds steady for 30 frames, after a 1s floor (a scene restore
            // arrives in bursts). Cap the wait at ~6s and go ahead anyway - a stuck count would otherwise
            // mean no links at all, and the duplicate sweep in RelinkAfterSettle covers a late arrival.
            // Scene LOADS need a long settle (NodesConstraints restores in bursts), but this path only
            // runs for REPLACEMENTS (loads are gated off upstream), where the churn is a few frames of
            // reload events - a short quiet window is enough and saves ~a second per replacement.
            const int Floor = 8, Stable = 5, Cap = 360;
            int wid = w.GetInstanceID();
            int last = NodeConstraintBridge.ConstraintCount, steady = 0;
            for (int f = 0; f < Cap; f++)
            {
                if (w == null) { _relinkPending.Remove(wid); yield break; }
                int now = NodeConstraintBridge.ConstraintCount;
                steady = (now == last) ? steady + 1 : 0;
                last = now;
                if (f >= Floor && steady >= Stable) break;
                yield return null;
            }
            _relinkPending.Remove(wid);
            if (w == null) yield break;
            AutoBodyReveal.RelinkNearWomb(w);   // resolves the CURRENT character now, post-swap
        }

        private System.Collections.IEnumerator DeferredApply(Component cc)
        {
            // Yield one frame first so ME's SAME-frame CharacterReloaded restore can run, then poll up to ~5s for
            // the saved BodyReveal copy to appear. Present -> ApplyForCharacterNow ADOPTS it (zero stencil writes).
            // Never appears within the cap -> a genuinely fresh/swapped-in character -> ApplyForCharacterNow creates it.
            yield return null;
            const int FrameCap = 300;   // ~5s @60fps - same headroom the wobble/BP re-attach coroutines use
            for (int f = 0; f < FrameCap; f++)
            {
                if (cc == null) yield break;
                if (MEBridge.MERestoredFor(cc)) break;
                yield return null;
            }
            if (cc == null) yield break;
            // carry the AUTHOR's CloXray material setup across a Studio character
            // REPLACE (shader-only scenes, womb disabled, lose the x-ray on swap otherwise). Runs HERE
            // - after ME restored the incoming card's own edits - so it can tell "the new card already
            // has these shaders" (adopt/capture) from "a replacement came in without them" (carry over).
            // SNAPSHOT AUTHORITY: if it REPLAYED a snapshot, that is the sole source of truth for
            // this character - do NOT also run the womb-proximity re-apply (no double-stamp; the
            // author's captured settings win). Capture / no-op paths return false and let it run.
            if (!ShaderCarryOver.OnReloaded(cc))
                AutoBodyReveal.ApplyForCharacterNow(cc);
            // Whichever branch ran, the constraints still have to be re-made: a replacement invalidates
            // her half of the penis links, and the carry-over branch above never reaches that code.
            AutoBodyReveal.StudioRelinkFor(cc);
        }


        private static int[] ToIntArray(object raw)
        {
            if (raw == null) return null;
            int[] a = raw as int[];
            if (a != null) return a;
            var oa = raw as object[];
            if (oa == null) return null;
            try { return oa.Select(Convert.ToInt32).ToArray(); } catch { return null; }
        }

        /// Put every womb back on the pair and stack it was saved with. A scene saved by a recent build
        /// carries that record and it is restored verbatim. An older scene carries no record
        /// OF OURS - but that is not the same as carrying nothing, and treating it as nothing is what
        /// made this pass rewrite scenes that were perfectly fine.
        ///
        /// MaterialEditor persists the womb's _StencilBody per material and has restored it by the time
        /// this runs, and the queues come back on the tags baked into the shaders. Both are answers the
        /// scene gave. So the old-scene path ADOPTS (ApplyWombState's adoptOnly): it writes a pair only
        /// where the material is still on the shipped default 4 - the actual symptom, a womb that came
        /// back on pair 1 while she is on another - and it writes no queue at all.
        ///
        /// SAFE ON OLD SCENES:
        /// - a womb with no wearer, or a wearer with no body stamp, is left completely alone;
        /// - the pair is copied from HER stamp, so it can only ever agree with her, never invent;
        /// - a material the scene spoke for keeps what the scene said, whatever her stamp reads;
        /// - queues are the hotkey's, so a scene authored on the shipped tags still loads on them.
        private System.Collections.IEnumerator RestoreWombState(int[] rec, int[] collapsed)
        {
            // MaterialEditor restores her body stamp on its own schedule; the repair reads that stamp, so
            // wait for the wombs to exist and give ME a moment to land. Same reasoning as ReattachWhenLiquid.
            float deadline = Time.unscaledTime + 10f;
            while (Time.unscaledTime < deadline)
            {
                if (UnityEngine.Object.FindObjectOfType<WombExpandEffect>() != null) break;
                yield return null;
            }
            yield return new WaitForSecondsRealtime(1.0f);

            var dic = Studio.Studio.Instance != null ? Studio.Studio.Instance.dicObjectCtrl : null;
            if (dic == null) yield break;

            // The collapsed uterus of an anal womb: a hand-set blendshape Studio does not save, so it is put back
            // from the plugin's own record - and only where the mesh still sits at the shipped 0, never over a value the
            // scene (or its author in KKPE) has since given it.
            if (collapsed != null)
                foreach (int id in collapsed)
                {
                    ObjectCtrlInfo oci;
                    if (!dic.TryGetValue(id, out oci) || oci == null || oci.guideObject == null) continue;
                    var tt = oci.guideObject.transformTarget;
                    if (tt == null) continue;
                    foreach (var w in tt.GetComponentsInChildren<WombExpandEffect>(true))
                    {
                        if (w == null) continue;
                        float cur = w.HandShape(WombExpandEffect.CollapseShape);
                        if (cur < 0f)   // -1 is "no mesh yet" as well as "no channel" - say which, never skip quietly
                            LiquidWobbleMPBPlugin._logger?.LogError("CloXray: womb '" + w.name + "' was saved with its"
                                + " uterus collapsed, but " + (w.Ready ? "its mesh has no '" + WombExpandEffect.CollapseShape
                                + "' channel" : "it had not finished starting") + " - the dome is back up.");
                        else if (cur < 1f && w.SetHandShape(WombExpandEffect.CollapseShape, 100f))
                            LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: womb '" + w.name
                                + "' was saved with its uterus collapsed (anal womb) - put back.");
                        break;
                    }
                }

            if (rec != null && rec.Length >= 3)
            {
                int done = 0;
                for (int i = 0; i + 2 < rec.Length; i += 3)
                {
                    ObjectCtrlInfo oci;
                    if (!dic.TryGetValue(rec[i], out oci) || oci == null || oci.guideObject == null) continue;
                    var tt = oci.guideObject.transformTarget;
                    if (tt == null) continue;
                    // The id is the womb's OWNING object, so the womb is the one directly under it - if
                    // that object holds several (a woman wearing two), they were saved under their own
                    // ids, so take only the womb whose owner really is this object.
                    foreach (var w in tt.GetComponentsInChildren<WombExpandEffect>(true))
                    {
                        if (w == null) continue;
                        if (AutoBodyReveal.ApplyWombState(w, rec[i + 1], rec[i + 2] != 0) > 0) done++;
                        break;
                    }
                }
                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: restored the saved pair+stack on " + done
                    + " of " + (rec.Length / 3) + " womb(s) (the rest already matched).");
                yield break;
            }

            // ── LEGACY SCENE (saved before the pair+stack record existed) ─────────────────────────────
            // the first cut gave up after one look and reported "repaired 0, left 24 alone",
            // which said nothing about WHY. MaterialEditor restores her body stamp on its own schedule,
            // so a womb whose wearer has no stamp YET is not a womb to skip - it is one to look at again.
            // Poll until every womb is decided or the deadline passes, then say per womb what happened.
            var wombs = new List<WombExpandEffect>();
            foreach (var kv in dic)
            {
                var tt = (kv.Value != null && kv.Value.guideObject != null) ? kv.Value.guideObject.transformTarget : null;
                var w0 = tt != null ? tt.GetComponentInChildren<WombExpandEffect>(true) : null;
                if (w0 != null && !wombs.Contains(w0)) wombs.Add(w0);   // one entry per womb: a womb parented
            }                                                          // under a character is found twice
            if (wombs.Count == 0) yield break;

            var pending = new List<WombExpandEffect>(wombs);
            int repaired = 0, already = 0;
            var reason = new Dictionary<WombExpandEffect, string>();
            // the wearer cannot change while a scene loads, so resolve it ONCE per womb and let the
            // retry loop re-check only the cheap thing - whether MaterialEditor has restored her stamp yet.
            // ResolveWearer's third tier walks the studio object dictionary; re-running it 40 times per womb
            // was pointless work for an answer that was already known.
            var wearer = new Dictionary<WombExpandEffect, Component>();
            var undecided = new List<WombExpandEffect>();   // removed from the loop but still owed a verdict
            float until = Time.unscaledTime + 10f;
            while (pending.Count > 0 && Time.unscaledTime < until)
            {
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    var w = pending[i];
                    if (w == null) { pending.RemoveAt(i); continue; }
                    Component cc;
                    if (!wearer.TryGetValue(w, out cc) || cc == null)
                    {
                        string how;
                        cc = AutoBodyReveal.ResolveWearer(w, out how);
                        if (cc != null) wearer[w] = cc;
                    }
                    if (cc == null) { reason[w] = "no wearer resolved"; continue; }   // keep looking
                    var stamp = MEBridge.FindBodyStamp(cc);
                    if (stamp == null) { reason[w] = "'" + cc.name + "' has no BodyReveal stamp yet"; continue; }
                    // the stamp is CloXray/BodyReveal, whose property is _StencilRef - _StencilBody
                    // is the ORGAN-side name. Reading the wrong one made every womb fall out of the loop
                    // through a branch that also forgot to count it: "9 wombs: 0, 0, 0". Use the reader
                    // that already exists for exactly this, and never drop a womb without a verdict.
                    int st = MEBridge.ReadBodyStencilRef(cc);
                    if (st <= 0)
                    {
                        reason[w] = "'" + cc.name + "' stamp carries no usable _StencilRef (" + st + ")";
                        pending.RemoveAt(i); undecided.Add(w); continue;
                    }
                    bool up = MEBridge.StampIsUpgraded(stamp);
                    int n = AutoBodyReveal.ApplyWombState(w, st, up, true);   // adopt: never overwrite what the scene set
                    pending.RemoveAt(i); reason.Remove(w);
                    if (n > 0)
                    {
                        repaired++;
                        LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: old scene - womb '" + w.name
                            + "' came back on the shipped pair with nothing to say otherwise; matched it to '"
                            + cc.name + "' (stencil " + st + ", " + n + " value(s) changed). Queues untouched -"
                            + " the hotkey owns those. Save the scene once and the pair is recorded.");
                    }
                    else already++;
                }
                if (pending.Count > 0) yield return new WaitForSecondsRealtime(0.5f);
            }

            undecided.AddRange(pending);
            LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: older scene, no saved pair+stack - " + wombs.Count + " womb(s): "
                + repaired + " repaired, " + already + " already correct, " + undecided.Count + " undecided."
                + (repaired + already + undecided.Count != wombs.Count
                   ? "  (COUNTS DO NOT ADD UP - a womb left the loop without a verdict; that is a bug in this report.)" : ""));
            foreach (var w in undecided)
            {
                string why; if (w == null) continue;
                if (!reason.TryGetValue(w, out why)) why = "never evaluated";
                LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray:   UNDECIDED womb '" + w.name + "' - " + why
                    + " (left exactly as the scene had it).");
            }
        }

        protected override void OnSceneSave()
        {
            List<int> ids = null;
            var dic = Studio.Studio.Instance != null ? Studio.Studio.Instance.dicObjectCtrl : null;
            if (dic != null)
                foreach (var kv in dic)
                {
                    var tt = (kv.Value != null && kv.Value.guideObject != null) ? kv.Value.guideObject.transformTarget : null;
                    if (tt == null) continue;
                    // a bottle-style wobble = has the driver but is NOT a womb (the womb re-attaches on spawn)
                    if (tt.GetComponentInChildren<LiquidWobbleMPBEffect>(true) != null
                        && tt.GetComponentInChildren<WombExpandEffect>(true) == null)
                        (ids ?? (ids = new List<int>())).Add(kv.Key);
                }

            // Every womb's pair + stack, so a load restores exactly what was on screen.
            // Key each womb by the studio object that OWNS it - the DEEPEST guide target it sits
            // under - not by every object it happens to be inside. Scanning the dictionary instead
            // recorded a womb parented to a character under her id as well as its own ("24 wombs" for a
            // scene with 9), and a woman wearing TWO wombs would then have one id resolving to whichever
            // of them GetComponentInChildren returned first. Same ownership rule as the aim takeover.
            List<int> wombs = null, collapsed = null;
            if (dic != null)
                foreach (var w in UnityEngine.Object.FindObjectsOfType<WombExpandEffect>())
                {
                    if (w == null) continue;
                    int st; bool up;
                    AutoBodyReveal.ReadWombState(w, out st, out up);
                    if (st < 0) continue;                       // no CloXray material on it - nothing to record
                    int ownerKey = 0, ownerDepth = -1;
                    foreach (var kv in dic)
                    {
                        var tt = (kv.Value != null && kv.Value.guideObject != null) ? kv.Value.guideObject.transformTarget : null;
                        if (tt == null || !w.transform.IsChildOf(tt)) continue;
                        int d = 0; for (var p = tt; p != null; p = p.parent) d++;
                        if (d > ownerDepth) { ownerDepth = d; ownerKey = kv.Key; }
                    }
                    if (ownerDepth < 0)
                    {
                        LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: womb '" + w.name
                            + "' belongs to no studio object - its pair/stack cannot be saved with the scene.");
                        continue;
                    }
                    if (wombs == null) wombs = new List<int>();
                    wombs.Add(ownerKey); wombs.Add(st); wombs.Add(up ? 1 : 0);
                    if (w.HandShape(WombExpandEffect.CollapseShape) >= 99f)
                    {
                        if (collapsed == null) collapsed = new List<int>();
                        collapsed.Add(ownerKey);
                    }
                }

            if (ids == null && wombs == null && collapsed == null) { SetExtendedData(null); return; }
            var data = new PluginData();
            if (ids != null) data.data[ItemsKey] = ids.ToArray();
            if (collapsed != null) data.data[CollapsedKey] = collapsed.ToArray();
            if (wombs != null)
            {
                data.data[WombKey] = wombs.ToArray();
                LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: saved pair+stack for " + (wombs.Count / 3) + " womb(s) with the scene.");
            }
            SetExtendedData(data);
        }

        // After the body-uncensor carry-over: wait for UncensorSelector's rebuild to actually deliver
        // the vagina bones (its coroutine, no event when done), then re-stamp + re-link. The ChaControl
        // survives an uncensor rebuild (only the body object under it is swapped), so holding cc here is
        // safe - unlike across a character replacement.
        public static void DeferPostUncensorApply(WombExpandEffect w, Component cc)
        {
            var host = _inst ?? (_inst = UnityEngine.Object.FindObjectOfType<WobbleSceneController>());
            if (host == null || w == null || cc == null)
            { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: no scene controller for the post-uncensor re-apply - press the apply hotkey once her body has loaded."); return; }
            host.StartCoroutine(host.AwaitBodyThenApply(w, cc));
        }

        // BP wipes its female collision binding whenever ANY uncensor body reloads (its prefix runs
        // ClearDanAgent on every controller) and on each of its own rebuilds. Instead of waiting those
        // cycles out on timers, the pair is registered here, bound IMMEDIATELY, and re-asserted from
        // BPBridge's InitializeDanAgent postfix after every BP rebuild - the last write is always ours.
        private class BpRebindPair { public WombExpandEffect w; public Component cc, male; public bool kokan, ana; }
        private static readonly System.Collections.Generic.List<BpRebindPair> _bpRebindPairs = new System.Collections.Generic.List<BpRebindPair>();

        public static void DeferBpRebind(WombExpandEffect w, Component cc)
        {
            if (w == null || cc == null) return;
            Component male = AutoBodyReveal.FindPenetratorForWomb(w, cc);
            if (male == null) { LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: no BP penis parked in womb '" + w.name + "' - no collision re-bind needed."); return; }
            bool kokan = true, ana = false;
            foreach (var pair in NodeConstraintBridge.LivePairs())
            {
                if (pair.Value == null || pair.Key == null || pair.Value.name != "k_f_dan_entry") continue;
                if (pair.Key.name.Contains("Ana")) { kokan = false; ana = true; }
                break;
            }
            for (int i = _bpRebindPairs.Count - 1; i >= 0; i--)
                if (_bpRebindPairs[i] == null || _bpRebindPairs[i].w == null || _bpRebindPairs[i].w == w) _bpRebindPairs.RemoveAt(i);
            _bpRebindPairs.Add(new BpRebindPair { w = w, cc = cc, male = male, kokan = kokan, ana = ana });
            BPBridge.InstallDanInitWatch();
            BPBridge.RebindCollisionAgent(male, cc, kokan, ana);
        }

        // Called from BPBridge's InitializeDanAgent postfix (suppressed while CloXray's own rebind is on the
        // stack): BP just rebuilt a dan agent, clearing any collision binding - re-assert ours.
        public static void ReassertBpBindings(Component maleCc)
        {
            for (int i = _bpRebindPairs.Count - 1; i >= 0; i--)
            {
                var p = _bpRebindPairs[i];
                if (p == null || p.w == null || p.cc == null || p.male == null) { _bpRebindPairs.RemoveAt(i); continue; }
                if (maleCc != null && !ReferenceEquals(p.male, maleCc)) continue;
                BPBridge.RebindCollisionAgent(p.male, p.cc, p.kokan, p.ana);
            }
        }

        // After the MALE's penis uncensor was restored: wait for the same body-reload completion event the
        // female path uses (UpdateUncensor reloads body+penis+balls together and raises nothing itself),
        // then re-link womb-anchored so the female stays the receiver.
        public static void DeferPenisRelink(WombExpandEffect w, Component male)
        {
            var host = _inst ?? (_inst = UnityEngine.Object.FindObjectOfType<WobbleSceneController>());
            if (host == null || w == null || male == null)
            { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: no scene controller for the post-uncensor re-link - press the apply hotkey once his penis has loaded."); return; }
            host.StartCoroutine(host.AwaitPenisThenRelink(w, male));
        }

        private System.Collections.IEnumerator AwaitPenisThenRelink(WombExpandEffect w, Component male)
        {
            const int Cap = 600;   // ~10s
            int f = 0;
            for (; f < Cap; f++)
            {
                if (w == null || male == null) { UncBodyReloadWatch.Clear(male); yield break; }
                if (UncBodyReloadWatch.Done(male)) break;
                yield return null;
            }
            UncBodyReloadWatch.Clear(male);
            if (w == null || male == null) yield break;
            if (f >= Cap)
            { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: the body-reload completion event never fired for '" + male.name + "' within 10s of restoring his penis uncensor - press the apply hotkey."); yield break; }
            yield return null;
            if (w == null || male == null) yield break;
            AutoBodyReveal.RelinkNearWomb(w);   // resolves the wearer itself and re-makes both links
        }

        private System.Collections.IEnumerator AwaitBodyThenApply(WombExpandEffect w, Component cc)
        {
            // Event-driven: UncBodyReloadWatch's postfix marks the exact frame the body swap finished
            // (armed by SetBodyUncensorGuid). No bone polling, no fixed settle delay.
            const int Cap = 600;   // ~10s: if the completion event never fires, say so loudly and stop
            int f = 0;
            for (; f < Cap; f++)
            {
                if (w == null || cc == null) { UncBodyReloadWatch.Clear(cc); yield break; }
                if (UncBodyReloadWatch.Done(cc)) break;
                yield return null;
            }
            UncBodyReloadWatch.Clear(cc);
            if (w == null || cc == null) yield break;
            if (f >= Cap)
            { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: the body-reload completion event never fired for '" + cc.name + "' within 10s of the uncensor restore - press the apply hotkey."); yield break; }
            yield return null;   // one frame - MaterialEditor's own reload hooks finish registering the new body
            if (w == null || cc == null) yield break;
            if (!AutoBodyReveal.HasVaginaBone(cc))
            { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: '" + cc.name + "' reloaded without cf_J_Vagina_root - the restored uncensor has no BP vagina; the penis links were not re-made."); yield break; }
            AutoBodyReveal.PostUncensorApply(w, cc);
        }

        // Wearer-capture poll: records which body the womb's wearer runs on (see
        // AutoBodyReveal.CaptureWearersFromConstraints for why events alone cannot).
        private Coroutine _capturePoll;
        private System.Collections.IEnumerator WearerCapturePoll()
        {
            var wait = new WaitForSeconds(2f);
            while (true)
            {
                yield return wait;
                try { AutoBodyReveal.CaptureWearersFromConstraints(); }
                catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: wearer-capture poll failed: " + e.Message); }
            }
        }

        // Re-armed each load; set true while one penis-bend coroutine is in flight so a duplicate OnSceneLoad
        // fire within the SAME load can't start a second one (guard flag, "once per load").
        private bool _penisBendInFlight;

        protected override void OnSceneLoad(SceneOperationKind operation, ReadOnlyDictionary<int, ObjectCtrlInfo> loadedItems)
        {
            _inst = this;   // ensure the deferred-apply coroutine host is available
            if (operation != SceneOperationKind.Clear) AutoBodyReveal.MarkSceneLoadStarted();   // closed by StudioSaveLoadApi.SceneLoad
            _bpRebindPairs.Clear();   // womb/character objects die with the outgoing scene
            // So do the carry-over snapshots, and for a sharper reason: Studio re-uses its object ids
            // across scenes, so a snapshot of slot 3 from the last scene would be REPLAYED onto slot 3
            // of this one - stamping CloXray copies onto a character the loaded scene never gave any.
            // Import adds to the live scene with fresh ids, so its snapshots stay valid.
            if (operation != SceneOperationKind.Import) ShaderCarryOver.ForgetAll();
            if (_capturePoll != null) StopCoroutine(_capturePoll);
            _capturePoll = StartCoroutine(WearerCapturePoll());
            if (operation == SceneOperationKind.Clear) return;

            // ── BP penis-bend on-load auto-fix. On load Studio re-applies the scene's saved FK state, which re-enables
            // FK on the penis dan nodes (103/105/107 + foreskin 119 - the only shaft bones that HAVE FK nodes); FK then
            // pins them straight while BP wants to bend the chain. BP 5.1's own ReleaseDanBonesFromFK frees them
            // again, and this is the kick that calls it after a load. Runs independently of the wobble re-attach
            // below (which early-returns when there's no saved liquid item), so it fires on every real load/import.
            // Deliberately NOT gated on BPBridge.Available: that is still false here because BP's assembly loads
            // LAZILY after the scene, so the coroutine waits for BP to appear instead. No-op without a BP penis.
            if (LiquidWobbleMPBPlugin.CfgEnabled && !_penisBendInFlight)
            {
                _penisBendInFlight = true;
                StartCoroutine(ReassertPenisBendWhenBPReady());
            }

            var data = GetExtendedData();
            // Womb pair+stack: restore the record if the scene carries it, otherwise repair an older scene.
            object rawW = null, rawC = null;
            if (data != null) data.data.TryGetValue(WombKey, out rawW);
            if (data != null) data.data.TryGetValue(CollapsedKey, out rawC);
            StartCoroutine(RestoreWombState(ToIntArray(rawW), ToIntArray(rawC)));

            object raw;
            if (data == null || !data.data.TryGetValue(ItemsKey, out raw) || raw == null) return;

            int[] ids = raw as int[];
            if (ids == null) { var oa = raw as object[]; if (oa != null) ids = oa.Select(Convert.ToInt32).ToArray(); }
            if (ids == null) return;

            // Re-attach AFTER the liquid material is restored - MaterialEditor re-applies it on load via its
            // own controller, and the load order between plugins isn't guaranteed. Poll for the CloXray/Liquid
            // material to appear (up to a few seconds), attaching as soon as it does. AttachWobbleTo is idempotent.
            StartCoroutine(ReattachWhenLiquid(ids, loadedItems));
        }

        // Wait until BP finished its load re-init (every controller's danTargetsValid == true), with a frame cap so
        // this never spins forever, THEN re-assert the bend on each gated male. BP re-resolves its dan targets ~1s after
        // load (resetDelay=60 frames); polling danTargetsValid lands strictly AFTER that, so the two never fight.
        private System.Collections.IEnumerator ReassertPenisBendWhenBPReady()
        {
            // try/finally guarantees the in-flight guard is cleared on EVERY exit (early yield-break, frame-cap,
            // or normal completion) so the next load re-arms. No try/catch around the yields - a real failure is
            // surfaced inside DisablePenisFK (LogWarning), never thrown out of the coroutine.
            try
            {
                const int FrameCap = 300;   // ~5s @60fps - headroom for BP's lazy assembly + resetDelay
                int frames = 0;
                while (frames < FrameCap)
                {
                    // BP loads lazily AFTER OnSceneLoad. Wait until every PENIS-BEARING male (has k_f_dan_end)
                    // finished its re-init (danTargetsValid). IGNORE BP controllers with no penis: the FEMALE also
                    // gets a BP controller whose danTargetsValid stays false forever - it was blocking "all ready"
                    // and forcing the full 5s timeout every load.
                    var males = BPBridge.EnumerateMales();
                    bool anyPenis = false, ready = true;
                    foreach (var m in males)
                    {
                        if (m.chaControl == null) continue;
                        if (BPDanBones.FindDanEnd(m.chaControl) == null) continue;   // no penis -> ignore
                        anyPenis = true;
                        if (!m.danTargetsValid) ready = false;
                    }
                    if (anyPenis && ready) break;
                    frames++;
                    yield return null;
                }

                // ZERO-EFFECT-WHEN-UNUSED: the penis-FK-off fix exists ONLY so BP can bend the penis INTO a CloXray
                // womb. If this scene has no womb, leave the penis FK exactly as it was posed. Item load order
                // isn't guaranteed, so give a womb a moment to instantiate after BP is ready before deciding the scene
                // isn't using one; then bail (the try/finally still clears the in-flight guard on this yield-break).
                for (int g = 0; g < 120 && !WombExpandEffect.EffectiveActive; g++) yield return null;
                if (!WombExpandEffect.EffectiveActive) yield break;
                AutoBodyReveal.InstallWombHooks();   // BP is up and a womb is present (mod on) -> ensure the interop patches are in

                // re-arm BP's orifice. Studio BP only learns "this is vaginal" from its Harmony
                // patch on NodesConstraints.AddConstraint, and on a LOAD the constraints are restored
                // before BP's dan agent exists - SetCollisionAgent early-returns on !danTargetsValid, and
                // nothing ever re-fires it, so BP reports no depth for the rest of the session. This is
                // already standing exactly where that can be fixed: BP ready, womb present.
                // GATED ON A CLOXRAY WOMB: the female the constraint names must be wearing one,
                // so a scene that merely has BP in it is never touched.
                // BP being ready is NOT NodesConstraints being ready. The scan said it plainly:
                //   chaM_001: k_f_dan_entry has no cf_J_Vagina_root constraint (it has no constraint at all)
                // - the scan ran before the scene's constraints were restored, so there was nothing to read.
                // Wait for the constraint count to hold steady, the same settle the relink path already
                // uses (a scene restore arrives in bursts, so a count that stops moving is the signal).
                {
                    int lastN = NodeConstraintBridge.ConstraintCount, steady = 0;
                    for (int f = 0; f < 480 && steady < 30; f++)   // ~8s cap, 30 still frames = settled
                    {
                        yield return null;
                        int nowN = NodeConstraintBridge.ConstraintCount;
                        if (nowN == lastN) steady++; else { steady = 0; lastN = nowN; }
                    }
                }
                // the settle is a heuristic - 30 still frames LOOKS settled, and a restore that
                // pauses that long would fool it. So try again a couple of times instead of trusting one
                // reading. BOUNDED and it stops the moment it succeeds: 3 attempts, 1s apart, worst case
                // 2 extra seconds and two extra scans of a handful of BP controllers, once per load.
                // Not a poll - it cannot outlive the coroutine, and success ends it immediately.
                System.Func<Component, bool> gate = her =>
                {
                    string how;
                    return her != null && AutoBodyReveal.WombOfWearer(her, out how) != null;
                };
                const int Attempts = 3;
                int armed = 0;
                for (int a = 0; a < Attempts && armed == 0; a++)
                {
                    if (a > 0) yield return new WaitForSecondsRealtime(1f);
                    armed = BPBridge.ReArmVaginalTargets(gate, report: a == Attempts - 1 || armed > 0);
                }
                if (armed > 0)
                    LiquidWobbleMPBPlugin._logger?.LogInfo("CloXray: re-armed BetterPenetration on " + armed
                        + " male(s) after the scene load - BP had lost the vaginal target that NodesConstraints restored.");

                var males2 = BPBridge.EnumerateMales();
                foreach (var m in males2)
                {
                    var cc = m.chaControl;
                    if (cc == null) continue;
                    if (BPDanBones.FindDanEnd(cc) == null) continue;              // no penis (e.g. the female) -> skip
                    if (!m.danTargetsValid) continue;                                  // BP not driving this male yet -> leave FK alone

                    // The penis dan FK nodes (103/105/107 + foreskin 119) sit in BoneGroup BODY, shared with the whole
                    // skeleton, so a group toggle is out - BP's ReleaseDanBonesFromFK clears ONLY those bones so its
                    // stateless driver owns the bend. This is the first kick after a load.
                    string info;
                    if (BPBridge.ReleaseDanFK(cc, out info))
                        LiquidWobbleMPBPlugin._logger?.LogInfo("PenisBend male='" + cc.name + "': " + info);
                }
                // ALWAYS re-check after a short delay: the load re-applies the saved activeFK[] a frame or two AFTER
                // danTargetsValid flips, so the first pass can legitimately find FK still off and then it re-enables.
                // Idempotent (no-op once FK is off).
                for (int k = 0; k < 30; k++) yield return null;
                foreach (var m in BPBridge.EnumerateMales())
                {
                    var cc = m.chaControl;
                    if (cc == null || !m.danTargetsValid) continue;
                    if (BPDanBones.FindDanEnd(cc) == null) continue;
                    string r2;
                    if (BPBridge.ReleaseDanFK(cc, out r2))
                        LiquidWobbleMPBPlugin._logger?.LogInfo("PenisBend: re-disabled penis FK on '" + cc.name + "' (late re-check: " + r2 + ").");
                }
            }
            finally
            {
                _penisBendInFlight = false;
            }
        }

        private System.Collections.IEnumerator ReattachWhenLiquid(int[] ids, ReadOnlyDictionary<int, ObjectCtrlInfo> loadedItems)
        {
            float t = 0f;
            while (t < 8f)
            {
                bool pending = false;
                foreach (var id in ids)
                {
                    if (!loadedItems.ContainsKey(id)) continue;        // KKAPI remaps saved ids -> loaded items (handles import)
                    var oci = loadedItems[id];
                    var tt = (oci != null && oci.guideObject != null) ? oci.guideObject.transformTarget : null;
                    if (tt == null) continue;
                    if (tt.GetComponentInChildren<LiquidWobbleMPBEffect>(true) != null) continue;   // already attached
                    AutoBodyReveal.AttachWobbleTo(tt.gameObject);                                     // attaches only once the liquid material is present
                    if (tt.GetComponentInChildren<LiquidWobbleMPBEffect>(true) == null) pending = true;
                }
                if (!pending) yield break;
                t += Time.deltaTime;
                yield return null;
            }
        }
    }
}
