using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRCSDK2;

namespace VRCViz
{
    public class VRCVizEditor : EditorWindow
    {
        #region Class Definitions

        private class HierarchyNode<T> where T : Component
        {
            public T[] Components;
            public HierarchyNode<T>[] Children;
            public HierarchyNode<Component>[] Targets;

            public static explicit operator HierarchyNode<Component>(HierarchyNode<T> v)
            {
                // Null propagation operator unvailable for C# 3.0 which VRCSDK2 depends on
                HierarchyNode<Component>[] children;
                if (v.Children != null)
                    children = v.Children.Select(s => (HierarchyNode<Component>)s).ToArray();
                else
                    children = null;

                HierarchyNode<Component>[] targets;
                if (v.Targets != null)
                    targets = v.Targets.Select(s => (HierarchyNode<Component>)s).ToArray();
                else
                    targets = null;

                return new HierarchyNode<Component>()
                {
                    Components = v.Components,
                    Children = children,
                    Targets = targets,
                };
            }
        }

        private class TraversedNode<T> where T : Component
        {
            public HierarchyNode<T> Node;
            public int Depth;

            public static implicit operator TraversedNode<Component>(TraversedNode<T> v)
            {
                return new TraversedNode<Component>()
                {
                    Node = (HierarchyNode<Component>)v.Node,
                    Depth = v.Depth
                };
            }
        }

        #endregion Class Definitions

        #region Instance Variables

        private HierarchyNode<Component>[] Roots;

        private VRC_Trigger[] AllTriggers;
        private int AllTargetsHash;

        private ILookup<int, HierarchyNode<Component>> AllNodesLookup;
        private List<HierarchyNode<Component>>[] AllNodeLayers;
        private int AllNodesMaxDepth;

        private Dictionary<HierarchyNode<Component>, Rect> NodeRectCache =
            new Dictionary<HierarchyNode<Component>, Rect>();

        private Vector2 ScrollPosition = Vector2.zero;

        public static Color TriggerColor = new Color(0.25f, 0.9f, 0.25f);
        public static Color TargetColor = new Color(1.0f, 0.5f, 0.25f);
        public static Color ActionColor = new Color(0.0f, 0.75f, 1.0f);
        public static Color ActionToSelfColor = new Color(1.0f, 0.875f, 0.0f);

        #endregion Instance Variables

        #region Hierarchy Methods

        private static HierarchyNode<T>[] GetSubHierarchy<T>(
            Transform transform)
            where T : Component
        {
            var components = transform.GetComponents<T>();
            if (!components.Any())
                return transform
                    .Cast<Transform>()
                    .SelectMany(t => GetSubHierarchy<T>(t))
                    .ToArray();
            var children = transform
                .Cast<Transform>()
                .SelectMany(t => GetSubHierarchy<T>(t))
                .ToArray();
            var nodes = new[] {
                new HierarchyNode<T>()
                {
                    Components = components,
                    Children = children
                }
            };
            return nodes;
        }

        private static IEnumerable<TraversedNode<T>> GetTraversal<T>(HierarchyNode<T> root, int depth = 0)
            where T : Component
        {
            yield return new TraversedNode<T>
            {
                Node = root,
                Depth = depth
            };
            foreach (var child in root.Children)
            {
                foreach (var node in GetTraversal(child, depth + 1))
                    yield return node;
            }
        }

        #endregion Hierarchy Methods

        #region Update Logic

        private void UpdateObjects()
        {
            var scenes = GetLoadedScenes().Where(s => s.isLoaded).ToArray();
            var roots = scenes.SelectMany(scene => scene.GetRootGameObjects()).ToArray();
            var nodes = roots.SelectMany(r => GetSubHierarchy<VRC_Trigger>(r.transform));
            var traversal = nodes.SelectMany(node => GetTraversal(node)).ToArray();

            AllTriggers = traversal.SelectMany(n => n.Node.Components).ToArray();
            AllTargetsHash = 0;

            // Dynamically accumulate targets for all of the nodes
            var all_targets = new Dictionary<GameObject, TraversedNode<Component>>();
            foreach (var tnode in traversal)
            {
                var target_objects = tnode.Node.Components
                    .SelectMany(t => t.Triggers)
                    .SelectMany(t => t.Events)
                    .SelectMany(e => e.ParameterObjects)
                    .Where(o => o != null);
                var targets = new List<TraversedNode<Component>>();
                foreach (var target_object in target_objects)
                {
                    // Calculate the target object hash in this loop
                    AllTargetsHash ^= target_object.GetInstanceID();
                    TraversedNode<Component> target;
                    if (!all_targets.TryGetValue(target_object, out target))
                    {
                        target = all_targets[target_object] = new TraversedNode<Component>()
                        {
                            Node = new HierarchyNode<Component>()
                            {
                                Components = new[]
                                {
                                    target_object.transform
                                },
                            },
                            Depth = GetTransformDepth(target_object.transform)
                        };
                    }
                    if (target != null)
                        targets.Add(target);
                }
                tnode.Node.Targets = targets.Select(t => t.Node).ToArray();
            }

            var traversed_targets = all_targets.Values.ToArray();
            var traversed_triggers = traversal.Select(s => (TraversedNode<Component>)s).ToArray();
            var everything = traversed_triggers.Concat(traversed_targets);

            AllNodesLookup = everything.ToLookup(t => t.Depth, t => t.Node);
            AllNodesMaxDepth = everything.Max(node => node.Depth);

            AllNodeLayers = Enumerable.Range(0, AllNodesMaxDepth + 1)
                .Select(i => AllNodesLookup[i].ToList())
                .Where(n => n.Any())
                .ToArray();

            // Calculate the sorting order for each layer based on its parents
            var within0 = GetLayerTargetsWithinLayer(AllNodeLayers[0]).ToArray();
            AllNodeLayers[0].Sort(LookupComparer(within0.ToLookup(e => e.Key, e => e.Value), true));

            var node_priority = GetLayerTargets(AllNodeLayers[0]).ToList();

            for (int i = 1; i < AllNodeLayers.Length; i++)
            {
                // 
                var layer = AllNodeLayers[i];
                var node_priority_lookup = node_priority.ToLookup(e => e.Key, e => e.Value);
                var comparer = LookupComparer(node_priority_lookup, true);
                AllNodeLayers[i].Sort(comparer);

                // Order by proximity to targets within the same layer
                var within = GetLayerTargetsWithinLayer(layer).ToArray();
                var self_node_priority_lookup = within.ToLookup(e => e.Key, e => e.Value);
                var self_comparer = LookupComparer(self_node_priority_lookup, true);
                AllNodeLayers[i].Sort(self_comparer);

                // Add the current layer targets to the working set
                var targets = GetLayerTargets(layer);
                node_priority.AddRange(targets);
            }
        }

        private static IEnumerable<KeyValuePair<HierarchyNode<Component>, int>>
            GetLayerTargets(IList<HierarchyNode<Component>> layer)
        {
            return Enumerable.Range(0, layer.Count)
                    .SelectMany(n => new HierarchyNode<Component>[] { }
                        .Concat(layer[n].Targets ?? new HierarchyNode<Component>[] { })
                        .Concat(layer[n].Children ?? new HierarchyNode<Component>[] { })
                        .Select(t => new KeyValuePair<HierarchyNode<Component>, int>(t, n)));
        }

        private static IEnumerable<KeyValuePair<HierarchyNode<Component>, int>>
            GetLayerTargetsWithinLayer(IList<HierarchyNode<Component>> layer)
        {
            var ordered = Enumerable.Range(0, layer.Count)
                .Select(n => new KeyValuePair<HierarchyNode<Component>, int>(layer[n], n));
            var order_dict = ordered.ToDictionary(e => e.Key, e => e.Value);
            // This sucks without null propagation operator :(
            var foo = ordered
                .Where(e => order_dict.ContainsKey((e.Key.Children != null && e.Key.Children.Any() ? e.Key.Children[0] : null) ??
                        (e.Key.Targets != null && e.Key.Targets.Any() ? e.Key.Targets[0] : null) ??
                        e.Key))
                .Select(
                e => new KeyValuePair<HierarchyNode<Component>, int>(
                    e.Key, order_dict[ // This is just SILLY
                        (e.Key.Children != null && e.Key.Children.Any() ? e.Key.Children[0] : null) ??
                        (e.Key.Targets != null && e.Key.Targets.Any() ? e.Key.Targets[0] : null) ??
                        e.Key
                    ]));
            return foo;
        }

        private void Update()
        {
            var target_objects = AllTriggers
                .SelectMany(t => t.Triggers)
                .SelectMany(t => t.Events)
                .SelectMany(e => e.ParameterObjects);
            var new_hash = 0;
            foreach (var target_object in target_objects)
                new_hash ^= target_object ? target_object.GetInstanceID() : 0;
            if (AllTargetsHash != new_hash)
            {
                UpdateObjects();
                Repaint();
            }
        }

        private void OnEnable()
        {
            UpdateObjects();
            Repaint();
        }

        private void OnHierarchyChange()
        {
            UpdateObjects();
            Repaint();
        }

        #endregion Update Logic

        #region GUI Methods

        private void OnGUI()
        {
            Draw();
            if (GUI.changed)
                Repaint();
        }

        private void Draw()
        {
            try
            {
                ValidateDraw();
            }
            catch (Exception e)
            {
                EditorGUILayout.HelpBox(e.Message, MessageType.Error);
                var style = GUI.skin.textArea;
                style.wordWrap = true;
                EditorGUILayout.TextArea(e.Message + "\n" + e.StackTrace, style, GUILayout.ExpandHeight(true));
                return;
            }
            using (var scroll_view = new EditorGUILayout.ScrollViewScope(ScrollPosition))
            {
                DrawNodes();
                DrawConnections();
                ScrollPosition = scroll_view.scrollPosition;
            }
        }

        private void ValidateDraw()
        {
            foreach (var layer in AllNodeLayers)
            {
                foreach (var node in layer)
                {
                    Debug.Assert(node.Components != null);
                }
            }
        }

        private void DrawNodes()
        {
            GUI.skin.button.fontSize = 11;
            GUI.skin.button.fontStyle = FontStyle.Normal;
            NodeRectCache.Clear();
            using (var hs = new EditorGUILayout.HorizontalScope())
            {
                foreach (var layer in AllNodeLayers)
                {
                    using (var vs = new EditorGUILayout.VerticalScope(GUILayout.Height(hs.rect.height)))
                    {
                        GUILayout.FlexibleSpace();
                        foreach (var node in layer)
                        {
                            var components = node.Components;
                            string name;
                            if (components.Any())
                                name = components[0].transform.name;
                            else
                                name = "?";
                            GUI.color = TriggerColor;
                            if (components.Length == 1)
                            {
                                var component = components[0];
                                if (!(component is VRC_Trigger))
                                    GUI.color = TargetColor;
                            }
                            if (GUILayout.Button(name, GUILayout.ExpandHeight(true), GUILayout.MaxHeight(64)))
                                Selection.activeObject = node.Components[0];
                            NodeRectCache[node] = GUILayoutUtility.GetLastRect();
                            GUILayout.FlexibleSpace();
                        }
                    }
                }
            }
        }

        private void DrawCurveAuto(Vector2 begin, Vector2 end, Color color)
        {
            var segments = 24;
            var r = Rect.MinMaxRect(begin.x, begin.y, end.x, end.y);
            var x_mid = r.xMin + 0.5f * r.width;
            var p0 = new Vector2(r.xMin, r.yMin);
            var a0 = new Vector2(x_mid, r.yMin);
            var a1 = new Vector2(x_mid, r.yMax);
            var p1 = new Vector2(r.xMax, r.yMax);
            Drawing.DrawBezierLine(p0, a0, p1, a1, Color.black, 3.0f, true, segments);
            Drawing.DrawBezierLine(p0, a0, p1, a1, Color.black, 3.0f, true, segments);
            Drawing.DrawBezierLine(p0, a0, p1, a1, Color.black, 3.0f, true, segments);
            Drawing.DrawBezierLine(p0, a0, p1, a1, color, 2.0f, true, segments);
            Drawing.DrawBezierLine(p0, a0, p1, a1, color, 2.0f, true, segments);
            Drawing.DrawBezierLine(p0, a0, p1, a1, color, 2.0f, true, segments);
        }

        private void DrawConnections()
        {
            for (int i = 0; i <= AllNodesMaxDepth; i++)
            {
                var layer = AllNodesLookup[i];
                foreach (var node in layer)
                {
                    if (node.Targets != null && node.Targets.Any())
                    {
                        var src_node_rect = NodeRectCache[node];
                        foreach (var target_node in node.Targets)
                        {
                            var target_node_rect = NodeRectCache[target_node];
                            var is_to_self = node.Components[0].gameObject == target_node.Components[0].gameObject;
                            var color = is_to_self ? ActionToSelfColor : ActionColor;
                            var begin = SubRectPoint(src_node_rect, 0.9f, 0.5f);
                            var end = SubRectPoint(target_node_rect, 0.1f, 0.5f);
                            DrawCurveAuto(begin, end, color);
                        }
                    }
                }
            }
        }

        #endregion GUI Methods

        #region Boilerplate & Utilities

        [MenuItem("VRChat SDK/VRCViz", priority = 99999)]
        private static void ShowWindow()
        {
            var editor_window = GetWindow<VRCVizEditor>(title: "VRCViz");
            editor_window.Show();
        }

        private static IEnumerable<Scene> GetLoadedScenes()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
                yield return SceneManager.GetSceneAt(i);
        }

        private static int GetTransformDepth(Transform transform)
        {
            int depth;
            for (depth = 0; transform.parent != null; depth++)
                transform = transform.parent;
            return depth;
        }

        private static Vector2 SubRectPoint(Rect rect, float xt, float yt)
        {
            return rect.min + new Vector2(rect.width * xt, rect.height * yt);
        }

        private static Comparison<TKey> LookupComparer<TKey>(ILookup<TKey, int> lookup, bool first, bool reverse = false)
        {
            return (a, b) =>
            {
                int x, y;
                if (lookup.Contains(a))
                    x = first ? lookup[a].First() : lookup[a].Last();
                else
                    x = 0;
                if (lookup.Contains(b))
                    y = first ? lookup[b].First() : lookup[b].Last();
                else
                    y = 0;
                if (reverse)
                    return y - x;
                else
                    return x - y;
            };
        }

        #endregion Boilerplate & Utilities
    }
}