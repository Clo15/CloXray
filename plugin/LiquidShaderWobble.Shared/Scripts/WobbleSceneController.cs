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
    /// Persists the per-item liquid wobble driver with the Studio scene — the same mechanism ComponentUtil and
    /// MaterialEditor use (a KKAPI <see cref="SceneCustomFunctionController"/> over ExtensibleSaveFormat).
    /// On save it records the scene-ids of items carrying a NON-womb <see cref="LiquidWobbleMPBEffect"/>; on
    /// load it re-attaches to exactly those items. The womb re-attaches itself on spawn, so it's excluded.
    /// </summary>
    public class WobbleSceneController : SceneCustomFunctionController
    {
        private const string ItemsKey = "wobbleItemIds";

        // ── Deferred on-load BodyReveal re-apply host ─────────────────────────────────────────────────────
        // AutoBodyReveal.OnCharacterReloaded is a STATIC handler with no coroutine host. Route its re-apply
        // through this SceneController (a MonoBehaviour — already the on-load coroutine host) so it runs AFTER
        // MaterialEditor restores the saved body copy. Otherwise, on a non-default stencil pair, our reload
        // handler can beat ME's and re-derive/stamp the stale default 4 over the user's saved value.
        private static WobbleSceneController _inst;

        public static void DeferApply(Component cc)
        {
            var host = _inst ?? (_inst = UnityEngine.Object.FindObjectOfType<WobbleSceneController>());
            if (host != null && cc != null) host.StartCoroutine(host.DeferredApply(cc));
            else AutoBodyReveal.ApplyForCharacterNow(cc);   // no host (shouldn't happen) -> immediate fallback
        }

        private System.Collections.IEnumerator DeferredApply(Component cc)
        {
            // Yield one frame first so ME's SAME-frame CharacterReloaded restore can run, then poll up to ~5s for
            // the saved BodyReveal copy to appear. Present -> ApplyForCharacterNow ADOPTS it (zero stencil writes).
            // Never appears within the cap -> a genuinely fresh/swapped-in character -> ApplyForCharacterNow creates it.
            yield return null;
            const int FrameCap = 300;   // ~5s @60fps — same headroom the wobble/BP re-attach coroutines use
            for (int f = 0; f < FrameCap; f++)
            {
                if (cc == null) yield break;
                if (MEBridge.MERestoredFor(cc)) break;
                yield return null;
            }
            if (cc != null) AutoBodyReveal.ApplyForCharacterNow(cc);
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

            if (ids == null) { SetExtendedData(null); return; }
            var data = new PluginData();
            data.data[ItemsKey] = ids.ToArray();
            SetExtendedData(data);
        }

        // Re-armed each load; set true while one penis-bend coroutine is in flight so a duplicate OnSceneLoad
        // fire within the SAME load can't start a second one (guard flag, "once per load").
        private bool _penisBendInFlight;

        protected override void OnSceneLoad(SceneOperationKind operation, ReadOnlyDictionary<int, ObjectCtrlInfo> loadedItems)
        {
            _inst = this;   // ensure the deferred-apply coroutine host is available
            if (operation == SceneOperationKind.Clear) return;

            // ── BP penis-bend on-load auto-fix. On load Studio re-applies the scene's saved FK state, which re-enables
            // FK on the penis dan nodes (103/105/107 + foreskin 119 — the only shaft bones that HAVE FK nodes); FK then
            // pins them straight while BP wants to bend the chain. PenisFKBridge.DisablePenisFK clears those bones'
            // per-bone TargetInfo.enable (body FK untouched — they share BoneGroup BODY, so a group toggle is out) so
            // stateless BP owns the bend. Runs independently of the wobble re-attach below (which early-returns when
            // there's no saved liquid item), so this fires on every real load/import. We gate ONLY on PenisFKBridge
            // (studio FK types, present at load) — NOT on BPBridge.Available, which is still false here because BP's
            // assembly loads LAZILY after the scene; the coroutine waits for BP to appear. The PenisFKEnforcer Harmony
            // postfix on OCIChar.ActiveFK keeps FK off across later re-enables. No-op if there's no BP-driven penis.
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

            // Re-attach AFTER the liquid material is restored — MaterialEditor re-applies it on load via its
            // own controller, and the load order between plugins isn't guaranteed. Poll for the CloXray/Liquid
            // material to appear (up to a few seconds), attaching as soon as it does. AttachWobbleTo is idempotent.
            StartCoroutine(ReattachWhenLiquid(ids, loadedItems));
        }

        // Wait until BP finished its load re-init (every controller's danTargetsValid == true), with a frame cap so
        // we never spin forever, THEN re-assert the bend on each gated male. BP re-resolves its dan targets ~1s after
        // load (resetDelay=60 frames); polling danTargetsValid runs us strictly AFTER that so we don't fight it.
        private System.Collections.IEnumerator ReassertPenisBendWhenBPReady()
        {
            // try/finally guarantees the in-flight guard is cleared on EVERY exit (early yield-break, frame-cap,
            // or normal completion) so the next load re-arms. No try/catch around the yields — a real failure is
            // surfaced inside DisablePenisFK (LogWarning), never thrown out of the coroutine.
            try
            {
                const int FrameCap = 300;   // ~5s @60fps — headroom for BP's lazy assembly + resetDelay
                int frames = 0;
                while (frames < FrameCap)
                {
                    // BP loads lazily AFTER OnSceneLoad. Wait until every PENIS-BEARING male (has k_f_dan_end)
                    // finished its re-init (danTargetsValid). IGNORE BP controllers with no penis: the FEMALE also
                    // gets a BP controller whose danTargetsValid stays false forever — it was blocking "all ready"
                    // and forcing the full 5s timeout every load.
                    var males = BPBridge.EnumerateMales();
                    bool anyPenis = false, ready = true;
                    foreach (var m in males)
                    {
                        if (m.chaControl == null) continue;
                        if (BPDanReaddGuard.FindDanEnd(m.chaControl) == null) continue;   // no penis -> ignore
                        anyPenis = true;
                        if (!m.danTargetsValid) ready = false;
                    }
                    if (anyPenis && ready) break;
                    frames++;
                    yield return null;
                }

                // ZERO-EFFECT-WHEN-UNUSED: the penis-FK-off fix exists ONLY so BP can bend the penis INTO a CloXray
                // womb. If this scene has no womb, leave the user's penis FK exactly as they posed it. Item load order
                // isn't guaranteed, so give a womb a moment to instantiate after BP is ready before deciding the scene
                // isn't using one; then bail (the try/finally still clears the in-flight guard on this yield-break).
                for (int g = 0; g < 120 && !WombExpandEffect.EffectiveActive; g++) yield return null;
                if (!WombExpandEffect.EffectiveActive) yield break;
                AutoBodyReveal.InstallWombHooks();   // BP is up and a womb is present (mod on) -> ensure the interop patches are in

                var males2 = BPBridge.EnumerateMales();
                foreach (var m in males2)
                {
                    var cc = m.chaControl;
                    if (cc == null) continue;
                    if (BPDanReaddGuard.FindDanEnd(cc) == null) continue;              // no penis (e.g. the female) -> skip
                    if (!m.danTargetsValid) continue;                                  // BP not driving this male yet -> leave FK alone

                    // BUILD 353 = CURE. The penis dan FK nodes (103/105/107 + foreskin 119) sit in BoneGroup BODY, shared
                    // with the whole skeleton, so a group toggle is out — DisablePenisFK clears ONLY those bones' per-bone
                    // TargetInfo.enable (+ deactivates their guides) so stateless BP owns the bend. The PenisFKEnforcer
                    // Harmony postfix on OCIChar.ActiveFK keeps it off across the load re-apply; this is the first kick.
                    string info;
                    if (PenisFKBridge.DisablePenisFK(cc, out info))
                        LiquidWobbleMPBPlugin._logger?.LogInfo("PenisBend male='" + cc.name + "': " + info);
                }
                // ALWAYS re-check after a short delay: the load re-applies the saved activeFK[] a frame or two AFTER
                // danTargetsValid flips, so the first pass can legitimately find FK still off and then it re-enables.
                // The enforcer postfix normally catches that; this is a belt-and-suspenders second kick in case the
                // postfix wasn't installed in time on this load. Idempotent (no-op once FK is off).
                for (int k = 0; k < 30; k++) yield return null;
                foreach (var m in BPBridge.EnumerateMales())
                {
                    var cc = m.chaControl;
                    if (cc == null || !m.danTargetsValid) continue;
                    if (BPDanReaddGuard.FindDanEnd(cc) == null) continue;
                    string r2;
                    if (PenisFKBridge.DisablePenisFK(cc, out r2))
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
