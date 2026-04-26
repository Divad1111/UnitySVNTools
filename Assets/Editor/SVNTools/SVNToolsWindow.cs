using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnitySVNTools.Editor
{
    internal sealed class SVNToolsWindow : EditorWindow
    {
        private static readonly Color AccentColor = new Color(0.24f, 0.54f, 0.92f);
        private static readonly Color SelectionColor = new Color(0.24f, 0.54f, 0.92f, 0.18f);
        private static readonly Color MutedTextColor = new Color(0.58f, 0.62f, 0.68f);

        private readonly List<SVNStatusEntry> statusEntries = new List<SVNStatusEntry>();
        private readonly HashSet<string> selectedPaths = new HashSet<string>();

        private SVNRepositoryInfo repositoryInfo;
        private Vector2 changeListScroll;
        private string commitMessage = string.Empty;
        private string focusedPath = string.Empty;
        private string lastStatusMessage = "Ready";
        private string currentOperationLabel = string.Empty;
        private volatile string currentOperationStep = string.Empty;
        private DateTime lastRefreshTime = DateTime.MinValue;
        private bool repositoryAvailable;
        private bool isBusy;

        private GUIStyle sectionTitleStyle;
        private GUIStyle mutedLabelStyle;
        private GUIStyle pathButtonStyle;
        private GUIStyle commitTextStyle;
        private GUIStyle badgeStyle;

        [MenuItem("Window/SVN Tools")]
        private static void OpenWindow()
        {
            var window = GetWindow<SVNToolsWindow>();
            window.titleContent = new GUIContent("SVN Tools");
            window.minSize = new Vector2(760f, 560f);
            window.Show();
        }

        internal static void RequestRefreshAll()
        {
            var windows = Resources.FindObjectsOfTypeAll<SVNToolsWindow>();
            foreach (var window in windows)
            {
                window.RefreshStatusAsync(false);
                window.Repaint();
            }
        }

        private void OnEnable()
        {
            RefreshStatusAsync(false);
        }

        private void OnFocus()
        {
            if (!isBusy)
            {
                RefreshStatusAsync(false);
            }
        }

        private void Update()
        {
            if (isBusy)
            {
                Repaint();
            }
        }

        private void OnGUI()
        {
            if (!EnsureStyles())
            {
                return;
            }

            DrawHeader();
            EditorGUILayout.Space(8f);
            DrawCommitArea();
            EditorGUILayout.Space(8f);
            DrawChangesArea();
            GUILayout.FlexibleSpace();
            DrawStatusBar();
        }

        private void DrawHeader()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
                {
                    GUILayout.Label("SOURCE CONTROL", EditorStyles.miniBoldLabel, GUILayout.Width(120f));
                    GUILayout.Label(repositoryAvailable ? repositoryInfo.WorkingCopyRoot : "未检测到 SVN 工作副本", EditorStyles.boldLabel, GUILayout.ExpandWidth(true));

                    using (new EditorGUI.DisabledScope(isBusy))
                    {
                        if (GUILayout.Button("刷新", EditorStyles.toolbarButton, GUILayout.Width(52f)))
                        {
                            RefreshStatusAsync(true);
                        }
                    }

                    if (GUILayout.Button("...", EditorStyles.toolbarButton, GUILayout.Width(28f)))
                    {
                        ShowContextMenu();
                    }
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(repositoryAvailable ? $"Branch {repositoryInfo.DisplayBranch}" : "Branch Unknown", mutedLabelStyle, GUILayout.Width(240f));
                    GUILayout.Label($"Changes {statusEntries.Count}", mutedLabelStyle, GUILayout.Width(100f));
                    GUILayout.Label(GetSelectionSummary(), mutedLabelStyle, GUILayout.Width(100f));
                    GUILayout.FlexibleSpace();
                    GUILayout.Label(GetRefreshText(), mutedLabelStyle);
                }

                if (isBusy)
                {
                    var progressRect = GUILayoutUtility.GetRect(18f, 18f, GUILayout.ExpandWidth(true));
                    EditorGUI.ProgressBar(progressRect, GetIndeterminateProgress(), GetBusyStatusText());
                }
            }
        }

        private void DrawCommitArea()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("MESSAGE", sectionTitleStyle);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("配置", GUILayout.Width(80f)))
                    {
                        SVNIgnoreSettingsWindow.ShowWindow();
                    }
                }

                GUILayout.Label(selectedPaths.Count > 0 ? "Selected items will be committed." : "All visible changes will be committed.", mutedLabelStyle);

                using (new EditorGUI.DisabledScope(!repositoryAvailable || isBusy))
                {
                    commitMessage = EditorGUILayout.TextArea(commitMessage, commitTextStyle, GUILayout.MinHeight(92f));

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.FlexibleSpace();
                        var commitLabel = selectedPaths.Count > 0 ? $"提交选中项 ({selectedPaths.Count})" : $"提交全部变更 ({statusEntries.Count})";
                        if (GUILayout.Button(commitLabel, GUILayout.Width(180f), GUILayout.Height(30f)))
                        {
                            CommitChanges();
                        }
                    }
                }
            }
        }

        private void DrawChangesArea()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("CHANGES", sectionTitleStyle);
                    GUILayout.FlexibleSpace();
                    GUILayout.Label(statusEntries.Count.ToString(), mutedLabelStyle, GUILayout.Width(32f));
                }

                if (!repositoryAvailable)
                {
                    EditorGUILayout.HelpBox(lastStatusMessage, MessageType.Warning);
                    return;
                }

                if (statusEntries.Count == 0)
                {
                    GUILayout.Space(10f);
                    GUILayout.Label("Working tree clean", EditorStyles.boldLabel);
                    GUILayout.Label("当前没有可显示的改动，或者改动已被忽略目录过滤。", mutedLabelStyle);
                    GUILayout.Space(24f);
                    return;
                }

                DrawChangesHeader();

                using (var scrollView = new EditorGUILayout.ScrollViewScope(changeListScroll, GUILayout.MinHeight(260f)))
                {
                    changeListScroll = scrollView.scrollPosition;
                    for (var index = 0; index < statusEntries.Count; index++)
                    {
                        DrawStatusRow(statusEntries[index]);
                    }
                }
            }
        }

        private void DrawChangesHeader()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(22f);
                GUILayout.Label("S", mutedLabelStyle, GUILayout.Width(28f));
                GUILayout.Label("Path", mutedLabelStyle, GUILayout.ExpandWidth(true));
                GUILayout.Label("State", mutedLabelStyle, GUILayout.Width(100f));
                GUILayout.Space(116f);
            }

            var lineRect = GUILayoutUtility.GetRect(1f, 1f, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(lineRect, new Color(1f, 1f, 1f, 0.08f));
        }

        private void DrawStatusRow(SVNStatusEntry entry)
        {
            var isFocused = focusedPath == entry.AbsolutePath;
            var rowRect = EditorGUILayout.BeginHorizontal();
            if (Event.current.type == EventType.Repaint && isFocused)
            {
                EditorGUI.DrawRect(rowRect, SelectionColor);
            }

            var isSelected = selectedPaths.Contains(entry.AbsolutePath);
            var toggled = GUILayout.Toggle(isSelected, GUIContent.none, GUILayout.Width(18f));
            if (toggled != isSelected)
            {
                if (toggled)
                {
                    selectedPaths.Add(entry.AbsolutePath);
                }
                else
                {
                    selectedPaths.Remove(entry.AbsolutePath);
                }
            }

            var previousColor = GUI.color;
            GUI.color = GetStatusColor(entry.DisplayStatus);
            GUILayout.Label(entry.DisplayStatus, badgeStyle, GUILayout.Width(28f));
            GUI.color = previousColor;

            if (GUILayout.Button(entry.RelativePath, pathButtonStyle, GUILayout.ExpandWidth(true)))
            {
                focusedPath = entry.AbsolutePath;
                if (Event.current.clickCount == 2)
                {
                    OpenDiff(entry);
                }
            }

            GUILayout.Label(GetItemDescription(entry), mutedLabelStyle, GUILayout.Width(100f));

            using (new EditorGUI.DisabledScope(isBusy))
            {
                if (isFocused)
                {
                    if (GUILayout.Button("日志", GUILayout.Width(54f)))
                    {
                        ShowLog(entry);
                    }

                    if (GUILayout.Button("还原", GUILayout.Width(54f)))
                    {
                        RevertEntry(entry);
                    }
                }
                else
                {
                    GUILayout.Space(116f);
                }
            }

            EditorGUILayout.EndHorizontal();
            HandleRowEvents(rowRect, entry);
        }

        private void HandleRowEvents(Rect rowRect, SVNStatusEntry entry)
        {
            var currentEvent = Event.current;
            if (!rowRect.Contains(currentEvent.mousePosition))
            {
                return;
            }

            if (currentEvent.type == EventType.MouseDown && currentEvent.button == 0)
            {
                focusedPath = entry.AbsolutePath;
                if (currentEvent.clickCount == 2)
                {
                    OpenDiff(entry);
                    currentEvent.Use();
                }
                else
                {
                    Repaint();
                }
            }

            if (currentEvent.type == EventType.ContextClick && !isBusy)
            {
                focusedPath = entry.AbsolutePath;
                ShowEntryContextMenu(entry);
                currentEvent.Use();
            }
        }

        private void DrawStatusBar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                var branch = repositoryAvailable ? repositoryInfo.DisplayBranch : "Unknown";
                GUILayout.Label($"分支: {branch}", EditorStyles.miniLabel, GUILayout.Width(220f));
                GUILayout.Label($"改动数: {statusEntries.Count}", EditorStyles.miniLabel, GUILayout.Width(100f));
                GUILayout.Label($"选中: {selectedPaths.Count}", EditorStyles.miniLabel, GUILayout.Width(80f));
                GUILayout.FlexibleSpace();
                GUILayout.Label(isBusy ? GetBusyStatusText() : lastStatusMessage, EditorStyles.miniLabel);
            }
        }

        private void ShowContextMenu()
        {
            var menu = new GenericMenu();
            if (isBusy)
            {
                menu.AddDisabledItem(new GUIContent("更新"));
                menu.AddDisabledItem(new GUIContent("刷新"));
            }
            else
            {
                menu.AddItem(new GUIContent("更新"), false, UpdateWorkingCopy);
                menu.AddItem(new GUIContent("刷新"), false, () => RefreshStatusAsync(true));
            }

            menu.ShowAsContext();
        }

        private void ShowEntryContextMenu(SVNStatusEntry entry)
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("查看 Diff"), false, () => OpenDiff(entry));
            menu.AddItem(new GUIContent("查看日志"), false, () => ShowLog(entry));
            menu.AddItem(new GUIContent("还原"), false, () => RevertEntry(entry));
            menu.ShowAsContext();
        }

        private void RefreshStatusAsync(bool fromUserAction)
        {
            if (isBusy)
            {
                return;
            }

            var ignoredDirectories = new List<string>(SVNIgnoreSettings.instance.IgnoredRelativeDirectories);
            StartBackgroundAction(
                fromUserAction ? "正在刷新状态..." : "正在读取 SVN 状态...",
                () =>
                {
                    SetOperationStep("读取仓库信息");
                    if (!SVNClient.TryGetRepositoryInfo(out var info, out var repositoryError))
                    {
                        return new RefreshPayload(false, info, new List<SVNStatusEntry>(), repositoryError, DateTime.Now);
                    }

                    SetOperationStep("读取改动列表");
                    var entries = SVNClient.GetStatusEntries(info, null, out var statusError);
                    return new RefreshPayload(true, info, entries, statusError, DateTime.Now);
                },
                payload =>
                {
                    SetOperationStep("应用忽略规则");
                    repositoryAvailable = payload.RepositoryAvailable;
                    repositoryInfo = payload.RepositoryInfo;
                    statusEntries.Clear();
                    statusEntries.AddRange(FilterIgnoredEntries(payload.StatusEntries, ignoredDirectories));
                    selectedPaths.RemoveWhere(path => !statusEntries.Exists(entry => entry.AbsolutePath == path));
                    lastRefreshTime = payload.RefreshTime;
                    lastStatusMessage = string.IsNullOrWhiteSpace(payload.Message)
                        ? (repositoryAvailable ? "状态已刷新" : "未检测到 svn 命令或工作副本。")
                        : payload.Message;
                    Repaint();
                },
                "SVN 刷新失败");
        }

        private void UpdateWorkingCopy()
        {
            if (!repositoryAvailable || isBusy)
            {
                return;
            }

            StartBackgroundAction(
                "正在更新工作副本...",
                () =>
                {
                    SetOperationStep("执行 svn update");
                    var success = SVNClient.UpdateWorkingCopy(repositoryInfo, out var output);
                    return new OperationPayload(success, output, success);
                },
                payload =>
                {
                    if (payload.ShouldRefreshAssets)
                    {
                        SetOperationStep("刷新 Unity 资源");
                        AssetDatabase.Refresh();
                    }

                    lastStatusMessage = payload.Success ? "更新完成" : payload.Output;
                    if (!payload.Success && !string.IsNullOrWhiteSpace(payload.Output))
                    {
                        EditorUtility.DisplayDialog("SVN 更新失败", payload.Output, "确定");
                    }

                    EditorApplication.delayCall += TriggerDeferredRefresh;
                },
                "SVN 更新失败");
        }

        private void CommitChanges()
        {
            if (isBusy)
            {
                return;
            }

            var entriesToCommit = GetEntriesToCommit();
            if (entriesToCommit.Count == 0)
            {
                EditorUtility.DisplayDialog("没有可提交的文件", "当前没有可提交的文件。", "确定");
                return;
            }

            StartBackgroundAction(
                "正在提交改动...",
                () =>
                {
                    SetOperationStep("执行 svn commit");
                    var success = SVNClient.Commit(repositoryInfo, entriesToCommit, commitMessage, out var output);
                    return new OperationPayload(success, output, success);
                },
                payload =>
                {
                    if (payload.ShouldRefreshAssets)
                    {
                        SetOperationStep("刷新 Unity 资源");
                        AssetDatabase.Refresh();
                    }

                    lastStatusMessage = payload.Success ? "提交完成" : payload.Output;
                    if (!payload.Success)
                    {
                        EditorUtility.DisplayDialog("SVN 提交失败", payload.Output, "确定");
                        return;
                    }

                    commitMessage = string.Empty;
                    selectedPaths.Clear();
                    EditorApplication.delayCall += TriggerDeferredRefresh;
                },
                "SVN 提交失败");
        }

        private List<SVNStatusEntry> GetEntriesToCommit()
        {
            if (selectedPaths.Count == 0)
            {
                return new List<SVNStatusEntry>(statusEntries);
            }

            var results = new List<SVNStatusEntry>();
            for (var index = 0; index < statusEntries.Count; index++)
            {
                var entry = statusEntries[index];
                if (selectedPaths.Contains(entry.AbsolutePath))
                {
                    results.Add(entry);
                }
            }

            return results;
        }

        private void RevertEntry(SVNStatusEntry entry)
        {
            if (isBusy)
            {
                return;
            }

            if (!EditorUtility.DisplayDialog("确认还原", $"确定要还原 {entry.RelativePath} 吗？", "还原", "取消"))
            {
                return;
            }

            StartBackgroundAction(
                $"正在还原 {entry.RelativePath}...",
                () =>
                {
                    SetOperationStep("执行 svn revert");
                    var success = SVNClient.RevertPath(repositoryInfo, entry.AbsolutePath, out var output);
                    return new OperationPayload(success, output, success);
                },
                payload =>
                {
                    if (payload.ShouldRefreshAssets)
                    {
                        SetOperationStep("刷新 Unity 资源");
                        AssetDatabase.Refresh();
                    }

                    lastStatusMessage = payload.Success ? $"已还原 {entry.RelativePath}" : payload.Output;
                    if (!payload.Success)
                    {
                        EditorUtility.DisplayDialog("SVN 还原失败", payload.Output, "确定");
                    }

                    EditorApplication.delayCall += TriggerDeferredRefresh;
                },
                "SVN 还原失败");
        }

        private void OpenDiff(SVNStatusEntry entry)
        {
            var success = SVNClient.ShowDiff(repositoryInfo, entry, out var output);
            lastStatusMessage = output;
            if (!success)
            {
                EditorUtility.DisplayDialog("无法打开 Diff", output, "确定");
            }
        }

        private void ShowLog(SVNStatusEntry entry)
        {
            var success = SVNClient.ShowLog(repositoryInfo, entry, out var output);
            lastStatusMessage = output;
            if (!success)
            {
                EditorUtility.DisplayDialog("无法打开日志", output, "确定");
            }
        }

        private void StartBackgroundAction<T>(string busyLabel, Func<T> work, Action<T> onSuccess, string dialogTitle)
        {
            isBusy = true;
            currentOperationLabel = busyLabel;
            currentOperationStep = "准备中";
            lastStatusMessage = busyLabel;

            SVNBackgroundTask<T>.Run(
                work,
                onSuccess,
                exception =>
                {
                    currentOperationStep = "失败";
                    lastStatusMessage = exception.Message;
                    EditorUtility.DisplayDialog(dialogTitle, exception.Message, "确定");
                },
                () =>
                {
                    isBusy = false;
                    currentOperationLabel = string.Empty;
                    currentOperationStep = string.Empty;
                    Repaint();
                });
        }

        private static List<SVNStatusEntry> FilterIgnoredEntries(List<SVNStatusEntry> entries, IReadOnlyList<string> ignoredDirectories)
        {
            if (entries == null || entries.Count == 0 || ignoredDirectories == null || ignoredDirectories.Count == 0)
            {
                return entries ?? new List<SVNStatusEntry>();
            }

            var filtered = new List<SVNStatusEntry>();
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                if (!IsIgnored(entry.RelativePath, ignoredDirectories))
                {
                    filtered.Add(entry);
                }
            }

            return filtered;
        }

        private static bool IsIgnored(string relativePath, IReadOnlyList<string> ignoredDirectories)
        {
            if (string.IsNullOrEmpty(relativePath))
            {
                return false;
            }

            var normalizedPath = NormalizePath(relativePath);
            for (var index = 0; index < ignoredDirectories.Count; index++)
            {
                var ignoredPath = NormalizePath(ignoredDirectories[index]);
                if (string.IsNullOrEmpty(ignoredPath))
                {
                    continue;
                }

                if (normalizedPath.Equals(ignoredPath, StringComparison.OrdinalIgnoreCase) || normalizedPath.StartsWith(ignoredPath + "/", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string NormalizePath(string path)
        {
            return path.Replace('\\', '/').Trim('/');
        }

        private string GetSelectionSummary()
        {
            return selectedPaths.Count > 0 ? $"Selected {selectedPaths.Count}" : "Selected none";
        }

        private string GetRefreshText()
        {
            if (lastRefreshTime == DateTime.MinValue)
            {
                return "Not refreshed yet";
            }

            var elapsed = DateTime.Now - lastRefreshTime;
            if (elapsed.TotalSeconds < 5d)
            {
                return "Updated just now";
            }

            if (elapsed.TotalSeconds < 60d)
            {
                return $"Updated {(int)elapsed.TotalSeconds}s ago";
            }

            return $"Updated {(int)elapsed.TotalMinutes}m ago";
        }

        private static string GetItemDescription(SVNStatusEntry entry)
        {
            switch (entry.WorkingCopyStatus)
            {
                case "added":
                case "unversioned":
                    return "Added";
                case "deleted":
                case "missing":
                    return "Deleted";
                case "conflicted":
                    return "Conflicted";
                case "replaced":
                    return "Replaced";
                default:
                    return "Modified";
            }
        }

        private static float GetIndeterminateProgress()
        {
            return (float)((Math.Sin(EditorApplication.timeSinceStartup * 2.5d) + 1d) * 0.5d);
        }

        private void TriggerDeferredRefresh()
        {
            EditorApplication.delayCall -= TriggerDeferredRefresh;
            RefreshStatusAsync(false);
        }

        private bool EnsureStyles()
        {
            if (EditorStyles.label == null || EditorStyles.boldLabel == null || EditorStyles.textArea == null)
            {
                return false;
            }

            if (sectionTitleStyle != null)
            {
                return true;
            }

            sectionTitleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 11,
            };

            mutedLabelStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = MutedTextColor },
            };

            pathButtonStyle = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleLeft,
                clipping = TextClipping.Clip,
                padding = new RectOffset(0, 6, 4, 4),
            };

            commitTextStyle = new GUIStyle(EditorStyles.textArea)
            {
                wordWrap = true,
            };

            badgeStyle = new GUIStyle(EditorStyles.miniBoldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white },
            };

            return true;
        }

        private string GetBusyStatusText()
        {
            return string.IsNullOrEmpty(currentOperationStep)
                ? currentOperationLabel
                : currentOperationLabel + "  步骤: " + currentOperationStep;
        }

        private void SetOperationStep(string step)
        {
            currentOperationStep = step;
        }

        private static Color GetStatusColor(string status)
        {
            switch (status)
            {
                case "A":
                    return new Color(0.26f, 0.66f, 0.33f);
                case "D":
                    return new Color(0.8f, 0.28f, 0.25f);
                default:
                    return AccentColor;
            }
        }

        private struct RefreshPayload
        {
            public RefreshPayload(bool repositoryAvailable, SVNRepositoryInfo repositoryInfo, List<SVNStatusEntry> statusEntries, string message, DateTime refreshTime)
            {
                RepositoryAvailable = repositoryAvailable;
                RepositoryInfo = repositoryInfo;
                StatusEntries = statusEntries;
                Message = message;
                RefreshTime = refreshTime;
            }

            public bool RepositoryAvailable;
            public SVNRepositoryInfo RepositoryInfo;
            public List<SVNStatusEntry> StatusEntries;
            public string Message;
            public DateTime RefreshTime;
        }

        private struct OperationPayload
        {
            public OperationPayload(bool success, string output, bool shouldRefreshAssets)
            {
                Success = success;
                Output = output;
                ShouldRefreshAssets = shouldRefreshAssets;
            }

            public bool Success;
            public string Output;
            public bool ShouldRefreshAssets;
        }
    }
}
