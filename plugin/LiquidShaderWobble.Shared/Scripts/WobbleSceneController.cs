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
    /// Persists the per-item liquid wobble driver with the Studio scene.
    public class WobbleSceneController : SceneCustomFunctionController
    {
        private const string ItemsKey = "wobbleItemIds";

        // ── Deferred on-load BodyReveal re-apply host ─────────────────────────────────────────────────────
        // AutoBodyReveal.OnCharacterReloaded is a STATIC handler with no coroutine host.
        private static WobbleSceneController _inst;

        public static void DeferApply(Component cc)
        {
            var host = _inst ?? (_inst = UnityEngine.Object.FindObjectOfType<WobbleSceneController>());
            if (host != null && cc != null) host.StartCoroutine(host.DeferredApply(cc));
            else AutoBodyReveal.ApplyForCharacterNow(cc);   // no host (shouldn't happen) -> immediate fallback.
        }

        // Re-make the penis links after a character load/replacement, but only once NodesConstraints has
        // finished restoring (scene load) or re-binding (character change) its own list.
        private static readonly System.Collections.Generic.HashSet<int> _relinkPending = new System.Collections.Generic.HashSet<int>();
        public static void DeferNodeRelink(WombExpandEffect w, Component cc)
        {
            var host = _inst ?? (_inst = UnityEngine.Object.FindObjectOfType<WobbleSceneController>());
            if (host == null || w == null)
            { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: no scene controller to defer the constraint re-link on - the penis links were NOT re-made. Press the apply hotkey to re-aim."); return; }
            if (!_relinkPending.Add(w.GetInstanceID())) return;   // one settle-wait per womb at a time.
            host.StartCoroutine(host.SettleThenRelink(w));
        }

        private System.Collections.IEnumerator SettleThenRelink(WombExpandEffect w)
        {
            // Settled = the constraint count holds steady for 30 frames, after a 1s floor (a scene restore
            // arrives in bursts).
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
            AutoBodyReveal.RelinkNearWomb(w);   // resolves the CURRENT character now, post-swap.
        }

        private System.Collections.IEnumerator DeferredApply(Component cc)
        {
            // Yield one frame first so ME's same-frame CharacterReloaded restore can run, then poll up to
            // ~5s for the saved BodyReveal copy to appear.
            yield return null;
            const int FrameCap = 300;   // ~5s @60fps - same headroom the wobble/BP re-attach coroutines use.
            for (int f = 0; f < FrameCap; f++)
            {
                if (cc == null) yield break;
                if (MEBridge.MERestoredFor(cc)) break;
                yield return null;
            }
            if (cc == null) yield break;
            // (shader-only scenes, womb disabled, lose the x-ray on swap otherwise).
            if (!ShaderCarryOver.OnReloaded(cc))
                AutoBodyReveal.ApplyForCharacterNow(cc);
            // Whichever branch ran, the constraints still have to be re-made.
            AutoBodyReveal.StudioRelinkFor(cc);
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
                    // a bottle-style wobble = has the driver but is NOT a womb (the womb re-attaches on
                    // spawn).
                    if (tt.GetComponentInChildren<LiquidWobbleMPBEffect>(true) != null
                        && tt.GetComponentInChildren<WombExpandEffect>(true) == null)
                        (ids ?? (ids = new List<int>())).Add(kv.Key);
                }

            if (ids == null) { SetExtendedData(null); return; }
            var data = new PluginData();
            data.data[ItemsKey] = ids.ToArray();
            SetExtendedData(data);
        }

        // After the body-uncensor carry-over: wait for UncensorSelector's rebuild to actually deliver the
        // vagina bones (its coroutine, no event when done), then re-stamp + re-link.
        public static void DeferPostUncensorApply(WombExpandEffect w, Component cc)
        {
            var host = _inst ?? (_inst = UnityEngine.Object.FindObjectOfType<WobbleSceneController>());
            if (host == null || w == null || cc == null)
            { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: no scene controller for the post-uncensor re-apply - press the apply hotkey once her body has loaded."); return; }
            host.StartCoroutine(host.AwaitBodyThenApply(w, cc));
        }

        // BP wipes its female collision binding whenever any uncensor body reloads (its prefix runs
        // ClearDanAgent on every controller) and on each of its own rebuilds.
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

        // Called from BPBridge's InitializeDanAgent postfix (suppressed while its own rebind is on the
        // stack).
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
        // female path uses, then re-link womb-anchored so the female stays the receiver.
        public static void DeferPenisRelink(WombExpandEffect w, Component male)
        {
            var host = _inst ?? (_inst = UnityEngine.Object.FindObjectOfType<WobbleSceneController>());
            if (host == null || w == null || male == null)
            { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: no scene controller for the post-uncensor re-link - press the apply hotkey once his penis has loaded."); return; }
            host.StartCoroutine(host.AwaitPenisThenRelink(w, male));
        }

        private System.Collections.IEnumerator AwaitPenisThenRelink(WombExpandEffect w, Component male)
        {
            const int Cap = 600;   // ~10s.
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
            AutoBodyReveal.RelinkNearWomb(w);   // resolves the wearer itself and re-makes both links.
        }

        private System.Collections.IEnumerator AwaitBodyThenApply(WombExpandEffect w, Component cc)
        {
            // Event-driven: UncBodyReloadWatch's postfix marks the exact frame the body swap finished (armed
            // by SetBodyUncensorGuid).
            const int Cap = 600;   // ~10s: if the completion event never fires, say so loudly and stop.
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
            yield return null;   // one frame - MaterialEditor's own reload hooks finish registering the new body.
            if (w == null || cc == null) yield break;
            if (!AutoBodyReveal.HasVaginaBone(cc))
            { LiquidWobbleMPBPlugin._logger?.LogError("CloXray: '" + cc.name + "' reloaded without cf_J_Vagina_root - the restored uncensor has no BP vagina; the penis links were not re-made."); yield break; }
            AutoBodyReveal.PostUncensorApply(w, cc);
        }

        // Wearer-capture poll: records which body the womb's wearer runs.
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

        // Re-armed each load; set true while one penis-bend coroutine is in flight so a duplicate
        // OnSceneLoad fire within the same load can't start a second one (guard flag, "once per load").
        private bool _penisBendInFlight;

        protected override void OnSceneLoad(SceneOperationKind operation, ReadOnlyDictionary<int, ObjectCtrlInfo> loadedItems)
        {
            _inst = this;   // ensure the deferred-apply coroutine host is available.
            if (operation != SceneOperationKind.Clear) AutoBodyReveal.MarkSceneLoadStarted();   // closed by StudioSaveLoadApi.SceneLoad.
            _bpRebindPairs.Clear();   // womb/character objects die with the outgoing scene.
            if (_capturePoll != null) StopCoroutine(_capturePoll);
            _capturePoll = StartCoroutine(WearerCapturePoll());
            if (operation == SceneOperationKind.Clear) return;

            // ── BP penis-bend on-load auto-fix. On load Studio re-applies the scene's saved FK state, which
            // re-enables FK on the penis dan nodes (103/105/107 + foreskin 119.
            if (LiquidWobbleMPBPlugin.CfgEnabled && !_penisBendInFlight && PenisFKBridge.Available)
            {
                _penisBendInFlight = true;
                StartCoroutine(ReassertPenisBendWhenBPReady());
            }

            var data = GetExtendedData();
            object raw;
            if (data == null || !data.data.TryGetValue(ItemsKey, out raw) || raw == null) return;

            int[] ids = raw as int[];
            if (ids == null) { var oa = raw as object[]; if (oa != null) ids = oa.Select(Convert.ToInt32).ToArray(); }
            if (ids == null) return;

            // Re-attach after the liquid material is restored.
            StartCoroutine(ReattachWhenLiquid(ids, loadedItems));
        }

        // Wait until BP finished its load re-init (every controller's danTargetsValid == true), with a frame
        // cap so it never spin forever, THEN re-assert the bend on each gated male.
        private System.Collections.IEnumerator ReassertPenisBendWhenBPReady()
        {
            // try/finally guarantees the in-flight guard is cleared on every exit (early yield-break,
            // frame-cap, or normal completion) so the next load re-arms.
            try
            {
                const int FrameCap = 300;   // ~5s @60fps - headroom for BP's lazy assembly + resetDelay.
                int frames = 0;
                while (frames < FrameCap)
                {
                    // BP loads lazily after OnSceneLoad. Wait until every PENIS-BEARING male (has
                    // k_f_dan_end) finished its re-init (danTargetsValid).
                    var males = BPBridge.EnumerateMales();
                    bool anyPenis = false, ready = true;
                    foreach (var m in males)
                    {
                        if (m.chaControl == null) continue;
                        if (BPDanReaddGuard.FindDanEnd(m.chaControl) == null) continue;   // no penis -> ignore.
                        anyPenis = true;
                        if (!m.danTargetsValid) ready = false;
                    }
                    if (anyPenis && ready) break;
                    frames++;
                    yield return null;
                }

                // ZERO-EFFECT-WHEN-UNUSED: the penis-FK-off fix exists only so BP can bend the penis INTO a
                // CloXray womb.
                for (int g = 0; g < 120 && !WombExpandEffect.EffectiveActive; g++) yield return null;
                if (!WombExpandEffect.EffectiveActive) yield break;
                AutoBodyReveal.InstallWombHooks();   // BP is up and a womb is present (mod on) -> ensure the interop patches.

                var males2 = BPBridge.EnumerateMales();
                foreach (var m in males2)
                {
                    var cc = m.chaControl;
                    if (cc == null) continue;
                    if (BPDanReaddGuard.FindDanEnd(cc) == null) continue;   // no penis (e.g. the female) -> skip.
                    if (!m.danTargetsValid) continue;   // BP not driving this male yet -> leave FK alone.

                    // BUILD 353 = CURE. The penis dan FK nodes (103/105/107 + foreskin 119) sit in BoneGroup
                    // BODY, shared with the whole skeleton, so a group toggle is out.
                    string info;
                    if (BPBridge.ReleaseDanFK(cc, out info))
                        LiquidWobbleMPBPlugin._logger?.LogInfo("PenisBend male='" + cc.name + "': " + info);
                }
                // always re-check after a short delay: the load re-applies the saved activeFK[] a frame or
                // two after danTargetsValid flips, so the first pass can legitimately find FK still off and then it re-enables.
                for (int k = 0; k < 30; k++) yield return null;
                foreach (var m in BPBridge.EnumerateMales())
                {
                    var cc = m.chaControl;
                    if (cc == null || !m.danTargetsValid) continue;
                    if (BPDanReaddGuard.FindDanEnd(cc) == null) continue;
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
                    if (!loadedItems.ContainsKey(id)) continue;   // KKAPI remaps saved ids -> loaded items (handles import).
                    var oci = loadedItems[id];
                    var tt = (oci != null && oci.guideObject != null) ? oci.guideObject.transformTarget : null;
                    if (tt == null) continue;
                    if (tt.GetComponentInChildren<LiquidWobbleMPBEffect>(true) != null) continue;   // already attached.
                    AutoBodyReveal.AttachWobbleTo(tt.gameObject);   // attaches only once the liquid material is present.
                    if (tt.GetComponentInChildren<LiquidWobbleMPBEffect>(true) == null) pending = true;
                }
                if (!pending) yield break;
                t += Time.deltaTime;
                yield return null;
            }
        }
    }
}
