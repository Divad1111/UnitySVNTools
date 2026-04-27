using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnitySVNTools.Editor
{
    internal sealed class SVNIgnoreSettingsWindow : EditorWindow
    {
        private readonly HashSet<string> selectedItems = new HashSet<string>();
        private Vector2 scrollPosition;
        private int selectionAnchorIndex = -1;

        public static void ShowWindow()
        {
            var window = GetWindow<SVNIgnoreSettingsWindow>(true, "忽略目录配置");
            window.minSize = new Vector2(480f, 280f);
            window.Show();
        }

        private void OnGUI()
        {
            var settings = SVNToolSettings.instance;
            var items = BuildItems(settings);
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("忽略项列表", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("忽略目录或文件中的改动不会显示在改动列表里，也不会参与提交。", MessageType.Info);

            using (var scrollView = new EditorGUILayout.ScrollViewScope(scrollPosition))
            {
                scrollPosition = scrollView.scrollPosition;
                if (items.Count == 0)
                {
                    EditorGUILayout.LabelField("当前没有忽略项。", EditorStyles.centeredGreyMiniLabel);
                }

                for (var index = 0; index < items.Count; index++)
                {
                    DrawItemRow(items[index], index, items);
                }
            }

            GUILayout.FlexibleSpace();

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(items.Count == 0))
                {
                    if (GUILayout.Button("清理", GUILayout.Width(80f)))
                    {
                        settings.ClearIgnoredEntries();
                        selectedItems.Clear();
                        selectionAnchorIndex = -1;
                        SVNToolsWindow.RequestRefreshAll();
                        GUIUtility.ExitGUI();
                    }
                }

                GUILayout.FlexibleSpace();
                if (GUILayout.Button("添加目录", GUILayout.Width(120f)))
                {
                    AddDirectory();
                }
            }
        }

        private void DrawItemRow(IgnoreItem item, int index, List<IgnoreItem> items)
        {
            var rowRect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
            var labelRect = new Rect(rowRect.x, rowRect.y, Mathf.Max(0f, rowRect.width - 80f), rowRect.height);
            var buttonRect = new Rect(rowRect.xMax - 72f, rowRect.y, 72f, rowRect.height);
            var isSelected = selectedItems.Contains(item.Key);

            if (Event.current.type == EventType.Repaint && isSelected)
            {
                EditorGUI.DrawRect(rowRect, new Color(0.24f, 0.54f, 0.92f, 0.18f));
            }

            GUI.Label(labelRect, item.DisplayText);
            if (GUI.Button(buttonRect, "移除"))
            {
                RemoveSelectedItems(item, items);
            }

            HandleRowSelection(rowRect, item, index, items);
        }

        private void HandleRowSelection(Rect rowRect, IgnoreItem item, int index, List<IgnoreItem> items)
        {
            var currentEvent = Event.current;
            if (!rowRect.Contains(currentEvent.mousePosition) || currentEvent.type != EventType.MouseDown || currentEvent.button != 0)
            {
                return;
            }

            var additive = currentEvent.control || currentEvent.command;
            var rangeSelection = currentEvent.shift;
            if (rangeSelection && selectionAnchorIndex >= 0)
            {
                selectedItems.Clear();
                var start = Mathf.Min(selectionAnchorIndex, index);
                var end = Mathf.Max(selectionAnchorIndex, index);
                for (var selectionIndex = start; selectionIndex <= end; selectionIndex++)
                {
                    selectedItems.Add(items[selectionIndex].Key);
                }
            }
            else if (additive)
            {
                if (!selectedItems.Add(item.Key))
                {
                    selectedItems.Remove(item.Key);
                }

                selectionAnchorIndex = index;
            }
            else
            {
                selectedItems.Clear();
                selectedItems.Add(item.Key);
                selectionAnchorIndex = index;
            }

            Repaint();
            currentEvent.Use();
        }

        private void RemoveSelectedItems(IgnoreItem fallbackItem, List<IgnoreItem> items)
        {
            var settings = SVNToolSettings.instance;
            var targets = new List<IgnoreItem>();
            if (selectedItems.Count > 1 && selectedItems.Contains(fallbackItem.Key))
            {
                for (var index = 0; index < items.Count; index++)
                {
                    if (selectedItems.Contains(items[index].Key))
                    {
                        targets.Add(items[index]);
                    }
                }
            }
            else
            {
                targets.Add(fallbackItem);
            }

            for (var index = 0; index < targets.Count; index++)
            {
                var item = targets[index];
                if (item.IsDirectory)
                {
                    settings.RemoveIgnoredDirectory(item.RelativePath);
                }
                else
                {
                    settings.RemoveIgnoredFile(item.RelativePath);
                }
            }

            selectedItems.Clear();
            selectionAnchorIndex = -1;
            SVNToolsWindow.RequestRefreshAll();
            GUIUtility.ExitGUI();
        }

        private static void AddDirectory()
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            var selectedPath = EditorUtility.OpenFolderPanel("选择忽略目录", projectRoot, string.Empty);
            if (string.IsNullOrEmpty(selectedPath))
            {
                return;
            }

            if (!TryGetRelativeDirectory(projectRoot, selectedPath, out var relativePath))
            {
                EditorUtility.DisplayDialog("目录无效", "请选择当前项目目录的子目录", "确定");
                return;
            }

            SVNToolSettings.instance.AddIgnoredDirectory(relativePath);
            SVNToolsWindow.RequestRefreshAll();
        }

        private static bool TryGetRelativeDirectory(string projectRoot, string selectedPath, out string relativePath)
        {
            relativePath = string.Empty;

            var fullProjectRoot = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullSelectedPath = Path.GetFullPath(selectedPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var rootWithSeparator = fullProjectRoot + Path.DirectorySeparatorChar;
            if (!fullSelectedPath.StartsWith(rootWithSeparator, System.StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            relativePath = fullSelectedPath.Substring(rootWithSeparator.Length).Replace('\\', '/').Trim('/');
            return !string.IsNullOrEmpty(relativePath);
        }

        private static List<IgnoreItem> BuildItems(SVNToolSettings settings)
        {
            var items = new List<IgnoreItem>();
            for (var index = 0; index < settings.IgnoredRelativeDirectories.Count; index++)
            {
                items.Add(new IgnoreItem(true, settings.IgnoredRelativeDirectories[index]));
            }

            for (var index = 0; index < settings.IgnoredRelativeFiles.Count; index++)
            {
                items.Add(new IgnoreItem(false, settings.IgnoredRelativeFiles[index]));
            }

            return items;
        }

        private readonly struct IgnoreItem
        {
            public IgnoreItem(bool isDirectory, string relativePath)
            {
                IsDirectory = isDirectory;
                RelativePath = relativePath ?? string.Empty;
                Key = (isDirectory ? "D:" : "F:") + RelativePath;
                DisplayText = (isDirectory ? "[目录] " : "[文件] ") + RelativePath;
            }

            public bool IsDirectory { get; }

            public string RelativePath { get; }

            public string Key { get; }

            public string DisplayText { get; }
        }
    }
}