using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace LiquidWobbleMPB
{
    /// <summary>
    /// One-shot survey of the Free-H UI, written to the log so the Free-H x-ray button can be built
    /// against what is ACTUALLY on screen instead of a guessed hierarchy path.
    ///
    /// It reports every live Button under the H canvas with its screen position, so "the cluster in
    /// the bottom right" is identified by measurement rather than by assuming which HSprite field
    /// sits where. The named HSprite anchors are printed alongside, since those are the fields a
    /// plugin can reach without a hard-coded path.
    ///
    /// Debug-Log gated and read-only: it never touches a single UI object. Re-arms when the H scene
    /// ends, so each Free-H entry produces one fresh survey.
    /// </summary>
    internal static class HSceneUIProbe
    {
        private static bool _dumped;
        private static Component _seenSprite;

        /// Called every frame from the plugin's Update. Cheap when idle: one FindObjectOfType on a
        /// type resolved once, and only while Diagnostic log is on.
        internal static void Pump()
        {
            if (!LiquidWobbleMPBPlugin.CfgDebugLog) { _dumped = false; _seenSprite = null; return; }
            if (MainGameWomb.IsStudio) return;

            Component sprite = FindHSprite();
            if (sprite == null) { _dumped = false; _seenSprite = null; return; }   // left H -> re-arm
            if (_dumped && ReferenceEquals(sprite, _seenSprite)) return;
            _dumped = true; _seenSprite = sprite;

            try { Dump(sprite); }
            catch (Exception e) { LiquidWobbleMPBPlugin._logger?.LogWarning("CloXray: H-scene UI survey failed: " + e.Message); }
        }

        // HSprite is global in KK and lives under the H namespace in KKS, so resolve by name from the
        // game assembly rather than referencing the type. FAIL LOUD: if neither name resolves, say
        // so once, because a silent miss looks exactly like "not in an H scene".
        private static Type _spriteType;
        private static bool _typeTried;
        private static Component FindHSprite()
        {
            if (!_typeTried)
            {
                _typeTried = true;
                _spriteType = Type.GetType("HSprite, Assembly-CSharp") ?? Type.GetType("H.HSprite, Assembly-CSharp");
                if (_spriteType == null)
                    LiquidWobbleMPBPlugin._logger?.LogError("CloXray: HSprite type not found (tried 'HSprite' and 'H.HSprite') - the Free-H UI survey cannot run.");
            }
            if (_spriteType == null) return null;
            return UnityEngine.Object.FindObjectOfType(_spriteType) as Component;
        }

        private static string PathOf(Transform t)
        {
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }

        private static void Dump(Component sprite)
        {
            var root = sprite.transform;
            var canvas = root.GetComponentInParent<Canvas>();
            var sb = new StringBuilder();
            sb.Append("CloXray FREE-H UI SURVEY (Diagnostic log; read-only)");
            sb.AppendLine().Append("  HSprite: ").Append(PathOf(root));
            sb.AppendLine().Append("  screen: ").Append(Screen.width).Append('x').Append(Screen.height);
            if (canvas != null)
            {
                sb.AppendLine().Append("  canvas: ").Append(PathOf(canvas.transform))
                  .Append("  renderMode=").Append(canvas.renderMode)
                  .Append("  scale=").Append(canvas.transform.lossyScale.x.ToString("F3"));
            }
            else sb.AppendLine().Append("  canvas: NONE above HSprite");

            // Named anchors: the buttons a plugin can reach as HSprite fields, no path guessing.
            sb.AppendLine().Append("  -- named HSprite anchors --");
            foreach (var f in new[] { "btnEnd", "btnTrespassing", "btnImmediatelyFinishFemale", "btnImmediatelyFinishMale",
                                      "objBaseMainMenu", "objMainMenuOpen", "objImmediatelyFinish" })
            {
                Transform t = null;
                try
                {
                    var fi = sprite.GetType().GetField(f, System.Reflection.BindingFlags.Instance
                                                        | System.Reflection.BindingFlags.Public
                                                        | System.Reflection.BindingFlags.NonPublic);
                    var v = fi != null ? fi.GetValue(sprite) : null;
                    var go = v as GameObject; if (go != null) t = go.transform;
                    var cp = v as Component;  if (cp != null) t = cp.transform;
                }
                catch { }
                sb.AppendLine().Append("     ").Append(f.PadRight(28));
                if (t == null) { sb.Append("(null or missing)"); continue; }
                sb.Append(Describe(t)).Append("   ").Append(PathOf(t));
            }

            // Every live button, sorted bottom-right first. THIS is what identifies the cluster.
            var buttons = new List<Button>();
            foreach (var b in root.GetComponentsInChildren<Button>(true))
                if (b != null && b.gameObject.activeInHierarchy) buttons.Add(b);
            buttons.Sort((a, b) =>
            {
                float sa = a.transform.position.x - a.transform.position.y;   // larger = further right and lower
                float sb2 = b.transform.position.x - b.transform.position.y;
                return sb2.CompareTo(sa);
            });
            sb.AppendLine().Append("  -- live buttons, most bottom-right first (").Append(buttons.Count).Append(" active) --");
            int n = 0;
            foreach (var b in buttons)
            {
                if (++n > 25) { sb.AppendLine().Append("     ... ").Append(buttons.Count - 25).Append(" more not listed"); break; }
                sb.AppendLine().Append("     ").Append(Describe(b.transform)).Append("   ").Append(PathOf(b.transform));
            }

            // The immediate family of the most bottom-right button: the layout a new sibling would join.
            if (buttons.Count > 0)
            {
                var parent = buttons[0].transform.parent;
                if (parent != null)
                {
                    sb.AppendLine().Append("  -- siblings of '").Append(buttons[0].name).Append("' under ").Append(PathOf(parent)).Append(" --");
                    var lay = parent.GetComponent<LayoutGroup>();
                    sb.AppendLine().Append("     layout group: ").Append(lay != null ? lay.GetType().Name : "none (free placement)");
                    for (int i = 0; i < parent.childCount && i < 20; i++)
                    {
                        var c = parent.GetChild(i);
                        sb.AppendLine().Append("     ").Append(c.gameObject.activeSelf ? " " : "-").Append(Describe(c))
                          .Append("   ").Append(c.name)
                          .Append(c.GetComponent<Button>() != null ? "   [Button]" : "")
                          .Append(c.GetComponent<Image>() != null ? " [Image]" : "");
                    }
                }
            }
            LiquidWobbleMPBPlugin._logger?.LogWarning(sb.ToString());
        }

        private static string Describe(Transform t)
        {
            var rt = t as RectTransform;
            Vector3 p = t.position;
            if (rt == null) return "pos=(" + p.x.ToString("F0") + "," + p.y.ToString("F0") + ")";
            return "screen=(" + p.x.ToString("F0") + "," + p.y.ToString("F0") + ")"
                 + " size=" + rt.rect.width.ToString("F0") + "x" + rt.rect.height.ToString("F0")
                 + " anchor=(" + rt.anchorMin.x.ToString("F1") + "," + rt.anchorMin.y.ToString("F1") + ")-("
                 + rt.anchorMax.x.ToString("F1") + "," + rt.anchorMax.y.ToString("F1") + ")"
                 + " anchoredPos=(" + rt.anchoredPosition.x.ToString("F0") + "," + rt.anchoredPosition.y.ToString("F0") + ")";
        }
    }
}
