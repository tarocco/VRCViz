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

        private ILookup<int, HierarchyNode<Component>> AllNodes;
        private int AllNodesMaxDepth;

        private Dictionary<HierarchyNode<Component>, Rect> NodeRectCache =
            new Dictionary<HierarchyNode<Component>, Rect>();

        #endregion Instance Variables

        public static Color TriggerColor = new Color(0.25f, 0.9f, 0.25f);
        public static Color TargetColor = new Color(1.0f, 0.5f, 0.25f);
        public static Color ActionColor = new Color(0.0f, 0.75f, 1.0f);
        public static Color ActionToSelfColor = new Color(1.0f, 0.875f, 0.0f);

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
            yield return new TraversedNode<T> { Node = root, Depth = depth };
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
                    targets.Add(target);
                }
                tnode.Node.Targets = targets.Select(t => t.Node).ToArray();
            }

            var traversed_targets = all_targets.Values.Select(s => (TraversedNode<Component>)s).ToArray();
            var traversed_triggers = traversal.Select(s => (TraversedNode<Component>)s).ToArray();
            var everything = traversed_triggers.Concat(traversed_targets);

            AllNodes = everything.ToLookup(t => t.Depth, t => t.Node);
            AllNodesMaxDepth = everything.Max(node => node.Depth);
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
            DrawNodes();
            DrawConnections();
        }

        private void DrawNodes()
        {
            GUI.skin.button.fontSize = 12;
            GUI.skin.button.fontStyle = FontStyle.Bold;
            NodeRectCache.Clear();
            for (int i = 0; i <= AllNodesMaxDepth; i++)
            {
                var layer = AllNodes[i];
                using (var vs = new EditorGUILayout.HorizontalScope())
                {
                    foreach (var node in layer)
                    {
                        using (var hs = new EditorGUILayout.VerticalScope())
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
                            if (GUILayout.Button(name, GUILayout.ExpandHeight(true)))
                                Selection.activeObject = node.Components[0];
                            NodeRectCache[node] = GUILayoutUtility.GetLastRect();
                        }
                    }
                }
            }
        }

        private void DrawConnections()
        {
            for (int i = 0; i <= AllNodesMaxDepth; i++)
            {
                var layer = AllNodes[i];
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
                            var begin = SubRectPoint(src_node_rect, 0.5f, 0.75f);
                            var end = SubRectPoint(target_node_rect, 0.5f, 0.25f);
                            Drawing.DrawLine(begin, end, Color.black, 3.0f, true);
                            Drawing.DrawLine(begin, end, Color.black, 3.0f, true);
                            Drawing.DrawLine(begin, end, Color.black, 3.0f, true);
                            Drawing.DrawLine(begin, end, color, 1.5f, true);
                            Drawing.DrawLine(begin, end, color, 1.5f, true);
                            Drawing.DrawLine(begin, end, color, 1.5f, true);
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
            for (depth = 0; transform != null; depth++)
                transform = transform.parent;
            return depth;
        }

        private static Vector2 SubRectPoint(Rect rect, float xt, float yt)
        {
            return rect.min + new Vector2(rect.width * xt, rect.height * yt);
        }

        #endregion Boilerplate & Utilities
    }
}