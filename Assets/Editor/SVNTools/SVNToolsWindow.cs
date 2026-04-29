using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnitySVNTools.Editor
{
    internal sealed class SVNToolsWindow : EditorWindow
    {
        private const string CommitMessageControlName = "SVNTools.CommitMessage";
        private const float LayoutSpacing = 8f;
        private const float HeaderPadding = 6f;
        private const float HeaderRowSpacing = 4f;
        private const float HeaderToolbarHeight = 18f;
        private const float HeaderSummaryHeight = 18f;
        private const float HeaderProgressHeight = 18f;
        private const float StatusBarHeight = 18f;
        private const float SplitterSize = 6f;
        private const float MinMessageAreaHeight = 176f;
        private const float ChangesHeaderHeight = 24f;
        private const float ChangesToolbarHeight = 24f;
        private const float ChangesFooterHeight = 24f;
        private const float RowHeight = 22f;
        private const float ToggleColumnWidth = 22f;
        private const float ActionsColumnWidth = 116f;
        private const float MinPathColumnWidth = 140f;

        private static readonly Color AccentColor = new Color(0.24f, 0.54f, 0.92f);
        private static readonly Color SelectionColor = new Color(0.24f, 0.54f, 0.92f, 0.18f);
        private static readonly Color MutedTextColor = new Color(0.58f, 0.62f, 0.68f);
        private static readonly Color SeparatorColor = new Color(1f, 1f, 1f, 0.08f);
        private static readonly Color HeaderBackgroundColor = new Color(1f, 1f, 1f, 0.025f);

        private readonly List<SVNStatusEntry> statusEntries = new List<SVNStatusEntry>();
        private readonly HashSet<string> selectedPaths = new HashSet<string>();
        private readonly HashSet<string> selectedRows = new HashSet<string>();

        private SVNRepositoryInfo repositoryInfo;
        private Vector2 changeListScroll;
        private string commitMessage = string.Empty;
        private string focusedPath = string.Empty;
        private string lastStatusMessage = "Ready";
        private string currentOperationLabel = string.Empty;
        private volatile string currentOperationStep = string.Empty;
        private string pendingCommitMessageSelection;
        private DateTime lastRefreshTime = DateTime.MinValue;
        private bool repositoryAvailable;
        private bool isBusy;
        private bool layoutInitialized;
        private bool changesListHasFocus;
        private bool refreshRequestedAfterOperation;
        private bool showIgnoredEntries;
        private bool showUnversionedEntries;
        private SortColumn sortColumn;
        private SortDirection sortDirection;
        private float messageAreaHeight;
        private float statusColumnWidth;
        private float stateColumnWidth;
        private int selectionAnchorIndex = -1;
        private DragMode dragMode;

        private GUIStyle sectionTitleStyle;
        private GUIStyle mutedLabelStyle;
        private GUIStyle clippedMutedLabelStyle;
        private GUIStyle headerPathLabelStyle;
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
            LoadSettings();
            RefreshStatusAsync(false);
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

            ApplyPendingCommitMessageSelection();

            if (!layoutInitialized)
            {
                LoadSettings();
            }

            HandleKeyboardShortcuts();

            var headerRect = new Rect(0f, 0f, position.width, GetHeaderHeight());
            DrawHeader(headerRect);

            var contentTop = headerRect.yMax + LayoutSpacing;
            var contentRect = new Rect(0f, contentTop, position.width, Mathf.Max(220f, position.height - contentTop));
            DrawContent(contentRect);

            if (Event.current.rawType == EventType.MouseUp && dragMode != DragMode.None)
            {
                SaveLayoutSettings();
                dragMode = DragMode.None;
            }
        }

        private void DrawHeader(Rect rect)
        {
            GUI.Box(rect, GUIContent.none, EditorStyles.helpBox);

            var contentRect = new Rect(
                rect.x + HeaderPadding,
                rect.y + HeaderPadding,
                rect.width - (HeaderPadding * 2f),
                rect.height - (HeaderPadding * 2f));

            var toolbarRect = new Rect(contentRect.x, contentRect.y, contentRect.width, HeaderToolbarHeight);
            GUI.Box(toolbarRect, GUIContent.none, EditorStyles.toolbar);

            var sourceLabelRect = new Rect(toolbarRect.x + 6f, toolbarRect.y, 120f, toolbarRect.height);
            var ellipsisRect = new Rect(toolbarRect.xMax - 28f, toolbarRect.y, 28f, toolbarRect.height);
            var refreshRect = new Rect(ellipsisRect.x - 52f, toolbarRect.y, 52f, toolbarRect.height);
            var pathRect = new Rect(sourceLabelRect.xMax + 6f, toolbarRect.y, refreshRect.x - sourceLabelRect.xMax - 12f, toolbarRect.height);

            GUI.Label(sourceLabelRect, "SOURCE CONTROL", EditorStyles.miniBoldLabel);
            GUI.Label(pathRect, repositoryAvailable ? repositoryInfo.WorkingCopyRoot : "未检测到 SVN 工作副本", headerPathLabelStyle);

            using (new EditorGUI.DisabledScope(isBusy))
            {
                if (GUI.Button(refreshRect, "刷新", EditorStyles.toolbarButton))
                {
                    RefreshStatusAsync(true);
                }

                if (GUI.Button(ellipsisRect, "...", EditorStyles.toolbarButton))
                {
                    ShowContextMenu();
                }
            }

            var summaryY = toolbarRect.yMax + HeaderRowSpacing;
            var branchRect = new Rect(contentRect.x, summaryY, 240f, HeaderSummaryHeight);
            var changesRect = new Rect(branchRect.xMax, summaryY, 100f, HeaderSummaryHeight);
            var selectionRect = new Rect(changesRect.xMax, summaryY, 100f, HeaderSummaryHeight);
            var refreshTextRect = new Rect(contentRect.xMax - 140f, summaryY, 140f, HeaderSummaryHeight);

            GUI.Label(branchRect, repositoryAvailable ? $"Branch {repositoryInfo.DisplayBranch}" : "Branch Unknown", clippedMutedLabelStyle);
            GUI.Label(changesRect, $"Changes {GetVisibleEntries().Count}", clippedMutedLabelStyle);
            GUI.Label(selectionRect, GetSelectionSummary(), clippedMutedLabelStyle);
            GUI.Label(refreshTextRect, GetRefreshText(), clippedMutedLabelStyle);

            if (isBusy)
            {
                var progressRect = new Rect(contentRect.x, summaryY + HeaderSummaryHeight + HeaderRowSpacing, contentRect.width, HeaderProgressHeight);
                EditorGUI.ProgressBar(progressRect, GetIndeterminateProgress(), GetBusyStatusText());
            }
        }

        private float GetHeaderHeight()
        {
            var height = HeaderPadding + HeaderToolbarHeight + HeaderRowSpacing + HeaderSummaryHeight + HeaderPadding;
            if (isBusy)
            {
                height += HeaderRowSpacing + HeaderProgressHeight;
            }

            return height;
        }

        private void DrawContent(Rect rect)
        {
            var statusRect = new Rect(rect.x, rect.yMax - StatusBarHeight, rect.width, StatusBarHeight);
            var availableHeight = Mathf.Max(220f, rect.height - StatusBarHeight - LayoutSpacing);
            var contentRect = new Rect(rect.x, rect.y, rect.width, availableHeight);
            var maxMessageHeight = Mathf.Max(MinMessageAreaHeight, contentRect.height - 140f);
            messageAreaHeight = Mathf.Clamp(messageAreaHeight, MinMessageAreaHeight, maxMessageHeight);

            var commitRect = new Rect(contentRect.x, contentRect.y, contentRect.width, messageAreaHeight);
            var splitterRect = new Rect(contentRect.x, commitRect.yMax + 2f, contentRect.width, SplitterSize);
            var changesRect = new Rect(contentRect.x, splitterRect.yMax + 2f, contentRect.width, Mathf.Max(80f, contentRect.yMax - splitterRect.yMax - 2f));

            HandleMessageSplitter(splitterRect, maxMessageHeight);
            DrawCommitArea(commitRect);
            DrawChangesArea(changesRect);
            DrawStatusBar(statusRect);
        }

        private void DrawCommitArea(Rect rect)
        {
            if (Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition))
            {
                changesListHasFocus = false;
            }

            GUILayout.BeginArea(rect, EditorStyles.helpBox);
            using (new EditorGUILayout.VerticalScope())
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

                GUILayout.Label(selectedPaths.Count > 0 ? "Only checked items will be committed." : "请先勾选需要提交的文件。", mutedLabelStyle);

                using (new EditorGUI.DisabledScope(!repositoryAvailable || isBusy))
                {
                    GUI.SetNextControlName(CommitMessageControlName);
                    commitMessage = EditorGUILayout.TextArea(commitMessage, commitTextStyle, GUILayout.ExpandHeight(true));

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.FlexibleSpace();
                        using (new EditorGUI.DisabledScope(SVNToolSettings.instance.CommitMessageHistory.Count == 0))
                        {
                            if (GUILayout.Button("历史", GUILayout.Width(72f), GUILayout.Height(30f)))
                            {
                                ShowCommitHistoryMenu();
                            }
                        }

                        using (new EditorGUI.DisabledScope(selectedPaths.Count == 0))
                        {
                            var commitLabel = $"提交选中项 ({selectedPaths.Count})";
                            if (GUILayout.Button(commitLabel, GUILayout.Width(180f), GUILayout.Height(30f)))
                            {
                                CommitChanges();
                            }
                        }
                    }
                }
            }

            GUILayout.EndArea();
        }

        private void DrawChangesArea(Rect rect)
        {
            GUILayout.BeginArea(rect, EditorStyles.helpBox);
            var titleRect = new Rect(8f, 6f, rect.width - 16f, 18f);
            GUI.Label(new Rect(titleRect.x, titleRect.y, 120f, titleRect.height), "CHANGES", sectionTitleStyle);
            var visibleEntries = GetVisibleEntries();
            var countText = $"{visibleEntries.Count}";
            GUI.Label(new Rect(titleRect.xMax - 40f, titleRect.y, 32f, titleRect.height), countText, mutedLabelStyle);

            var toolbarRect = new Rect(8f, titleRect.yMax + 4f, rect.width - 16f, ChangesToolbarHeight);
            DrawChangesToolbar(toolbarRect, visibleEntries);

            var tableRect = new Rect(8f, toolbarRect.yMax + 4f, rect.width - 16f, rect.height - toolbarRect.yMax - ChangesFooterHeight - 12f);
            if (!repositoryAvailable)
            {
                EditorGUI.HelpBox(tableRect, lastStatusMessage, MessageType.Warning);
                DrawChangesFooter(new Rect(8f, rect.height - ChangesFooterHeight - 6f, rect.width - 16f, ChangesFooterHeight));
                GUILayout.EndArea();
                return;
            }

            if (visibleEntries.Count == 0)
            {
                GUI.Label(new Rect(tableRect.x, tableRect.y + 12f, tableRect.width, 18f), "Working tree clean", EditorStyles.boldLabel);
                GUI.Label(new Rect(tableRect.x, tableRect.y + 34f, tableRect.width, 18f), GetEmptyChangesMessage(), mutedLabelStyle);
                DrawChangesFooter(new Rect(8f, rect.height - ChangesFooterHeight - 6f, rect.width - 16f, ChangesFooterHeight));
                GUILayout.EndArea();
                return;
            }

            DrawChangesTable(tableRect, visibleEntries);
            DrawChangesFooter(new Rect(8f, rect.height - ChangesFooterHeight - 6f, rect.width - 16f, ChangesFooterHeight));
            GUILayout.EndArea();
        }

        private void DrawChangesToolbar(Rect rect, List<SVNStatusEntry> visibleEntries)
        {
            GUI.BeginGroup(rect);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("All", GUILayout.Width(52f)))
                {
                    SelectAllVisibleEntries(visibleEntries, true);
                }

                if (GUILayout.Button("None", GUILayout.Width(52f)))
                {
                    SelectAllVisibleEntries(visibleEntries, false);
                }

                GUILayout.FlexibleSpace();
            }

            GUI.EndGroup();
        }

        private void DrawChangesFooter(Rect rect)
        {
            GUI.BeginGroup(rect);
            var ignoredToggleRect = new Rect(0f, 2f, 170f, 18f);
            var ignoredToggled = GUI.Toggle(ignoredToggleRect, showIgnoredEntries, "显示忽略文件夹内容");
            if (ignoredToggled != showIgnoredEntries)
            {
                showIgnoredEntries = ignoredToggled;
                SVNToolSettings.instance.SetShowIgnoredEntries(showIgnoredEntries);
                SyncSelectionWithVisibility();
                Repaint();
            }

            var unversionedToggleRect = new Rect(ignoredToggleRect.xMax + 12f, 2f, 190f, 18f);
            var unversionedToggled = GUI.Toggle(unversionedToggleRect, showUnversionedEntries, "显示未在版本控制中的");
            if (unversionedToggled != showUnversionedEntries)
            {
                showUnversionedEntries = unversionedToggled;
                SVNToolSettings.instance.SetShowUnversionedEntries(showUnversionedEntries);
                SyncSelectionWithVisibility();
                Repaint();
            }

            GUI.EndGroup();
        }

        private void DrawChangesTable(Rect rect, List<SVNStatusEntry> visibleEntries)
        {
            var columns = GetColumns(rect.width);
            var headerRect = new Rect(rect.x, rect.y, rect.width, ChangesHeaderHeight);
            EditorGUI.DrawRect(headerRect, HeaderBackgroundColor);
            DrawHeaderCell(TranslateRect(columns.ToggleRect, rect.x, rect.y), string.Empty);
            DrawSortableHeaderCell(TranslateRect(columns.StatusRect, rect.x, rect.y), "S", SortColumn.Status);
            DrawSortableHeaderCell(TranslateRect(columns.PathRect, rect.x, rect.y), "Path", SortColumn.Path);
            DrawSortableHeaderCell(TranslateRect(columns.StateRect, rect.x, rect.y), "Ext", SortColumn.Extension);
            DrawHeaderCell(TranslateRect(columns.ActionsRect, rect.x, rect.y), string.Empty);
            DrawColumnSeparators(columns, rect.x, headerRect.y, headerRect.yMax);
            HandleColumnResize(columns, headerRect);

            var bodyRect = new Rect(rect.x, headerRect.yMax, rect.width, rect.height - headerRect.height);
            var contentHeight = Mathf.Max(bodyRect.height, visibleEntries.Count * RowHeight);
            var viewRect = new Rect(0f, 0f, rect.width - 16f, contentHeight);
            changeListScroll = GUI.BeginScrollView(bodyRect, changeListScroll, viewRect);
            for (var index = 0; index < visibleEntries.Count; index++)
            {
                DrawStatusRow(visibleEntries[index], index, columns, viewRect.width);
            }
            GUI.EndScrollView();
        }

        private void DrawHeaderCell(Rect rect, string text)
        {
            GUI.Label(rect, text, mutedLabelStyle);
        }

        private void DrawSortableHeaderCell(Rect rect, string text, SortColumn column)
        {
            var suffix = string.Empty;
            if (sortColumn == column)
            {
                suffix = sortDirection == SortDirection.Ascending ? " ▲" : sortDirection == SortDirection.Descending ? " ▼" : string.Empty;
            }

            if (GUI.Button(rect, text + suffix, mutedLabelStyle))
            {
                ToggleSort(column);
            }
        }

        private void DrawStatusRow(SVNStatusEntry entry, int index, ColumnLayout columns, float totalWidth)
        {
            var rowRect = new Rect(0f, index * RowHeight, totalWidth, RowHeight);
            var isRowSelected = selectedRows.Contains(entry.AbsolutePath);
            if (Event.current.type == EventType.Repaint)
            {
                if (isRowSelected)
                {
                    EditorGUI.DrawRect(rowRect, SelectionColor);
                }

                EditorGUI.DrawRect(new Rect(rowRect.x, rowRect.yMax - 1f, rowRect.width, 1f), SeparatorColor);
                DrawColumnSeparators(columns, 0f, rowRect.y, rowRect.yMax);
            }

            var toggleRect = OffsetRect(columns.ToggleRect, rowRect.y);
            var statusRect = OffsetRect(columns.StatusRect, rowRect.y);
            var pathRect = OffsetRect(columns.PathRect, rowRect.y);
            var stateRect = OffsetRect(columns.StateRect, rowRect.y);
            var actionsRect = OffsetRect(columns.ActionsRect, rowRect.y);

            var isSelected = selectedPaths.Contains(entry.AbsolutePath);
            var toggled = GUI.Toggle(new Rect(toggleRect.x + 2f, toggleRect.y + 2f, 18f, 18f), isSelected, GUIContent.none);
            if (toggled != isSelected)
            {
                SetChecked(entry.AbsolutePath, toggled);
            }

            var previousColor = GUI.color;
            GUI.color = GetStatusColor(entry.DisplayStatus);
            GUI.Label(statusRect, entry.DisplayStatus, badgeStyle);
            GUI.color = previousColor;

            GUI.Label(pathRect, entry.RelativePath, pathButtonStyle);
            GUI.Label(stateRect, GetExtensionText(entry), mutedLabelStyle);

            if (focusedPath == entry.AbsolutePath)
            {
                var actionEntries = GetActionEntries(entry);
                using (new EditorGUI.DisabledScope(isBusy))
                {
                    var logRect = new Rect(actionsRect.x, actionsRect.y + 1f, 54f, RowHeight - 2f);
                    var revertRect = new Rect(actionsRect.x + 62f, actionsRect.y + 1f, 54f, RowHeight - 2f);
                    var logLabel = actionEntries.Count > 1 ? $"日志({actionEntries.Count})" : "日志";
                    var revertLabel = actionEntries.Count > 1 ? $"还原({actionEntries.Count})" : "还原";
                    if (GUI.Button(logRect, logLabel))
                    {
                        ShowLog(actionEntries);
                    }

                    if (GUI.Button(revertRect, revertLabel))
                    {
                        RevertEntries(actionEntries);
                    }
                }
            }

            HandleRowEvents(rowRect, toggleRect, pathRect, actionsRect, entry, index);
        }

        private void HandleRowEvents(Rect rowRect, Rect toggleRect, Rect pathRect, Rect actionsRect, SVNStatusEntry entry, int index)
        {
            var currentEvent = Event.current;
            if (!rowRect.Contains(currentEvent.mousePosition))
            {
                return;
            }

            if (currentEvent.type == EventType.MouseDown && currentEvent.button == 0)
            {
                FocusChangesList();
                ApplyRowSelection(entry, index, currentEvent.control || currentEvent.command, currentEvent.shift);

                if (toggleRect.Contains(currentEvent.mousePosition))
                {
                    currentEvent.Use();
                    return;
                }

                if (actionsRect.Contains(currentEvent.mousePosition))
                {
                    return;
                }

                if (currentEvent.clickCount == 2 && pathRect.Contains(currentEvent.mousePosition))
                {
                    OpenDiff(GetActionEntries(entry));
                    currentEvent.Use();
                }
                else
                {
                    focusedPath = entry.AbsolutePath;
                    Repaint();
                }
            }

            if (currentEvent.type == EventType.ContextClick && !isBusy)
            {
                FocusChangesList();
                if (!(selectedRows.Count > 1 && selectedRows.Contains(entry.AbsolutePath)))
                {
                    ApplyRowSelection(entry, index, false, false);
                }

                focusedPath = entry.AbsolutePath;
                ShowEntryContextMenu(entry);
                currentEvent.Use();
            }
        }

        private void FocusChangesList()
        {
            changesListHasFocus = true;
            EditorGUIUtility.editingTextField = false;
            GUI.FocusControl(string.Empty);
        }

        private void DrawStatusBar(Rect rect)
        {
            GUILayout.BeginArea(rect, EditorStyles.toolbar);
            using (new EditorGUILayout.HorizontalScope())
            {
                var branch = repositoryAvailable ? repositoryInfo.DisplayBranch : "Unknown";
                GUILayout.Label($"分支: {branch}", EditorStyles.miniLabel, GUILayout.Width(220f));
                GUILayout.Label($"改动数: {GetVisibleEntries().Count}", EditorStyles.miniLabel, GUILayout.Width(100f));
                GUILayout.Label($"选中: {selectedPaths.Count}", EditorStyles.miniLabel, GUILayout.Width(80f));
                GUILayout.FlexibleSpace();
                GUILayout.Label(isBusy ? GetBusyStatusText() : lastStatusMessage, EditorStyles.miniLabel);
            }

            GUILayout.EndArea();
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
            var actionEntries = GetActionEntries(entry);
            var suffix = actionEntries.Count > 1 ? $" ({actionEntries.Count})" : string.Empty;
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("查看 Diff" + suffix), false, () => OpenDiff(actionEntries));
            menu.AddItem(new GUIContent("查看日志" + suffix), false, () => ShowLog(actionEntries));
            menu.AddItem(new GUIContent("还原" + suffix), false, () => RevertEntries(actionEntries));
            menu.AddItem(new GUIContent("删除" + suffix), false, () => DeleteEntries(actionEntries));
            menu.AddItem(new GUIContent("在文件浏览器中打开" + suffix), false, () => OpenContainingFolders(actionEntries));
            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent("添加到忽略目录" + suffix), false, () => AddEntriesToIgnore(actionEntries));
            menu.ShowAsContext();
        }

        private void RefreshStatusAsync(bool fromUserAction)
        {
            if (isBusy)
            {
                return;
            }

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
                    statusEntries.AddRange(payload.StatusEntries ?? new List<SVNStatusEntry>());
                    selectedPaths.RemoveWhere(path => !statusEntries.Exists(entry => entry.AbsolutePath == path));
                    SyncSelectionWithVisibility();
                    if (!string.IsNullOrEmpty(focusedPath) && !statusEntries.Exists(entry => entry.AbsolutePath == focusedPath))
                    {
                        focusedPath = string.Empty;
                    }
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

            if (!SVNClient.OpenUpdateWindow(repositoryInfo, out var output))
            {
                lastStatusMessage = output;
                EditorUtility.DisplayDialog("无法打开 TortoiseSVN 更新窗口", output, "确定");
                return;
            }

            lastStatusMessage = output;
            Repaint();
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
                        if (SVNClient.OpenCommitWindow(repositoryInfo, entriesToCommit, out var fallbackOutput))
                        {
                            lastStatusMessage = "svn commit 失败，已打开 TortoiseSVN 提交窗口。";
                        }
                        else
                        {
                            var message = string.IsNullOrWhiteSpace(fallbackOutput)
                                ? payload.Output
                                : $"{payload.Output}\n\n{fallbackOutput}";
                            EditorUtility.DisplayDialog("SVN 提交失败", message, "确定");
                        }

                        return;
                    }

                    SVNToolSettings.instance.AddCommitMessageHistory(commitMessage);
                    commitMessage = string.Empty;
                    selectedPaths.Clear();
                    selectedRows.Clear();
                    RequestRefreshAfterCurrentOperation();
                },
                "SVN 提交失败");
        }

        private List<SVNStatusEntry> GetEntriesToCommit()
        {
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

        private void RevertEntries(IReadOnlyList<SVNStatusEntry> entries)
        {
            if (isBusy || entries == null || entries.Count == 0)
            {
                return;
            }

            var confirmMessage = entries.Count == 1
                ? $"确定要还原 {entries[0].RelativePath} 吗？"
                : $"确定要还原选中的 {entries.Count} 项吗？";
            if (!EditorUtility.DisplayDialog("确认还原", confirmMessage, "还原", "取消"))
            {
                return;
            }

            StartBackgroundAction(
                entries.Count == 1 ? $"正在还原 {entries[0].RelativePath}..." : $"正在还原 {entries.Count} 项...",
                () =>
                {
                    var builder = new StringBuilder();
                    for (var index = 0; index < entries.Count; index++)
                    {
                        var currentEntry = entries[index];
                        SetOperationStep($"执行 svn revert ({index + 1}/{entries.Count})");
                        var success = SVNClient.RevertPath(repositoryInfo, currentEntry.AbsolutePath, out var output);
                        if (!string.IsNullOrWhiteSpace(output))
                        {
                            if (builder.Length > 0)
                            {
                                builder.AppendLine();
                            }

                            builder.Append(output);
                        }

                        if (!success)
                        {
                            return new OperationPayload(false, builder.ToString(), true);
                        }
                    }

                    return new OperationPayload(true, builder.ToString(), true);
                },
                payload =>
                {
                    if (payload.ShouldRefreshAssets)
                    {
                        SetOperationStep("刷新 Unity 资源");
                        AssetDatabase.Refresh();
                    }

                    lastStatusMessage = payload.Success
                        ? (entries.Count == 1 ? $"已还原 {entries[0].RelativePath}" : $"已还原 {entries.Count} 项")
                        : payload.Output;
                    if (!payload.Success)
                    {
                        EditorUtility.DisplayDialog("SVN 还原失败", payload.Output, "确定");
                    }

                    EditorApplication.delayCall += TriggerDeferredRefresh;
                },
                "SVN 还原失败");
        }

        private void OpenContainingFolders(IReadOnlyList<SVNStatusEntry> entries)
        {
            if (entries == null || entries.Count == 0)
            {
                return;
            }

            var openedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                if (entry == null || string.IsNullOrWhiteSpace(entry.AbsolutePath))
                {
                    continue;
                }

                var folderPath = Directory.Exists(entry.AbsolutePath)
                    ? entry.AbsolutePath
                    : Path.GetDirectoryName(entry.AbsolutePath);
                if (string.IsNullOrWhiteSpace(folderPath) || !openedFolders.Add(folderPath))
                {
                    continue;
                }

                if (!SVNClient.OpenContainingFolder(entry.AbsolutePath, out var output))
                {
                    EditorUtility.DisplayDialog("无法打开目录", output, "确定");
                    return;
                }

                lastStatusMessage = output;
            }
        }

        private void DeleteEntries(IReadOnlyList<SVNStatusEntry> entries)
        {
            if (isBusy || entries == null || entries.Count == 0)
            {
                return;
            }

            var targetEntries = BuildDeleteTargets(entries);
            if (targetEntries.Count == 0)
            {
                return;
            }

            var confirmMessage = targetEntries.Count == 1
                ? $"确定要删除本地文件 {targetEntries[0].RelativePath} 吗？"
                : $"确定要删除本地选中的 {targetEntries.Count} 项吗？";
            if (!EditorUtility.DisplayDialog("确认删除", confirmMessage, "删除", "取消"))
            {
                return;
            }

            StartBackgroundAction(
                targetEntries.Count == 1 ? $"正在删除 {targetEntries[0].RelativePath}..." : $"正在删除 {targetEntries.Count} 项...",
                () =>
                {
                    var builder = new StringBuilder();
                    for (var index = 0; index < targetEntries.Count; index++)
                    {
                        var currentEntry = targetEntries[index];
                        SetOperationStep($"删除本地文件 ({index + 1}/{targetEntries.Count})");
                        var success = SVNClient.DeleteLocalPath(currentEntry.AbsolutePath, out var output);
                        if (!string.IsNullOrWhiteSpace(output))
                        {
                            if (builder.Length > 0)
                            {
                                builder.AppendLine();
                            }

                            builder.Append(output);
                        }

                        if (!success)
                        {
                            return new OperationPayload(false, builder.ToString(), true);
                        }
                    }

                    return new OperationPayload(true, builder.ToString(), true);
                },
                payload =>
                {
                    if (payload.ShouldRefreshAssets)
                    {
                        SetOperationStep("刷新 Unity 资源");
                        AssetDatabase.Refresh();
                    }

                    lastStatusMessage = payload.Success
                        ? (targetEntries.Count == 1 ? $"已删除 {targetEntries[0].RelativePath}" : $"已删除 {targetEntries.Count} 项")
                        : payload.Output;
                    if (!payload.Success)
                    {
                        EditorUtility.DisplayDialog("删除失败", payload.Output, "确定");
                    }

                    EditorApplication.delayCall += TriggerDeferredRefresh;
                },
                "删除失败");
        }

        private void OpenDiff(IReadOnlyList<SVNStatusEntry> entries)
        {
            if (entries == null || entries.Count == 0)
            {
                return;
            }

            for (var index = 0; index < entries.Count; index++)
            {
                var success = SVNClient.ShowDiff(repositoryInfo, entries[index], out var output);
                lastStatusMessage = output;
                if (!success)
                {
                    EditorUtility.DisplayDialog("无法打开 Diff", output, "确定");
                    return;
                }
            }

            if (entries.Count > 1)
            {
                lastStatusMessage = $"已打开 {entries.Count} 个 Diff";
            }
        }

        private void ShowLog(IReadOnlyList<SVNStatusEntry> entries)
        {
            if (entries == null || entries.Count == 0)
            {
                return;
            }

            for (var index = 0; index < entries.Count; index++)
            {
                var success = SVNClient.ShowLog(repositoryInfo, entries[index], out var output);
                lastStatusMessage = output;
                if (!success)
                {
                    EditorUtility.DisplayDialog("无法打开日志", output, "确定");
                    return;
                }
            }

            if (entries.Count > 1)
            {
                lastStatusMessage = $"已打开 {entries.Count} 项日志";
            }
        }

        private void StartBackgroundAction<T>(string busyLabel, Func<T> work, Action<T> onSuccess, string dialogTitle)
        {
            isBusy = true;
            refreshRequestedAfterOperation = false;
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

                    if (refreshRequestedAfterOperation)
                    {
                        refreshRequestedAfterOperation = false;
                        RefreshStatusAsync(false);
                        return;
                    }

                    Repaint();
                });
        }

        private static List<SVNStatusEntry> FilterIgnoredEntries(List<SVNStatusEntry> entries, SVNToolSettings settings)
        {
            if (entries == null || entries.Count == 0 || settings == null)
            {
                return entries ?? new List<SVNStatusEntry>();
            }

            var filtered = new List<SVNStatusEntry>();
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                if (!settings.IsIgnored(entry.RelativePath))
                {
                    filtered.Add(entry);
                }
            }

            return filtered;
        }

        private static List<SVNStatusEntry> FilterUnversionedEntries(List<SVNStatusEntry> entries)
        {
            if (entries == null || entries.Count == 0)
            {
                return entries ?? new List<SVNStatusEntry>();
            }

            var filtered = new List<SVNStatusEntry>();
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                if (!string.Equals(entry?.WorkingCopyStatus, "unversioned", StringComparison.OrdinalIgnoreCase))
                {
                    filtered.Add(entry);
                }
            }

            return filtered;
        }

        private string GetEmptyChangesMessage()
        {
            if (showIgnoredEntries && showUnversionedEntries)
            {
                return "当前没有可显示的改动。";
            }

            if (!showIgnoredEntries && !showUnversionedEntries)
            {
                return "当前没有可显示的改动，或者改动已被忽略目录和未版本控制过滤。";
            }

            return showIgnoredEntries
                ? "当前没有可显示的改动，或者改动已被未版本控制过滤。"
                : "当前没有可显示的改动，或者改动已被忽略目录过滤。";
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

        private static string GetExtensionText(SVNStatusEntry entry)
        {
            var extension = Path.GetExtension(entry?.RelativePath ?? string.Empty);
            if (!string.IsNullOrEmpty(extension))
            {
                return extension.TrimStart('.');
            }

            return Directory.Exists(entry?.AbsolutePath ?? string.Empty) ? "<DIR>" : string.Empty;
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

        private void HandleKeyboardShortcuts()
        {
            var currentEvent = Event.current;
            if (currentEvent.type != EventType.KeyDown || !changesListHasFocus || GUI.GetNameOfFocusedControl() == CommitMessageControlName)
            {
                return;
            }

            if ((currentEvent.control || currentEvent.command) && currentEvent.keyCode == KeyCode.A)
            {
                SelectAllRows();
                currentEvent.Use();
                return;
            }

            if (currentEvent.keyCode == KeyCode.Space)
            {
                ToggleCheckedForSelectedRows();
                currentEvent.Use();
                return;
            }

            if (currentEvent.keyCode == KeyCode.Delete
                || (Application.platform == RuntimePlatform.OSXEditor && currentEvent.command && currentEvent.keyCode == KeyCode.Backspace))
            {
                DeleteEntries(GetKeyboardActionEntries());
                currentEvent.Use();
            }
        }

        private void HandleMessageSplitter(Rect splitterRect, float maxMessageHeight)
        {
            EditorGUIUtility.AddCursorRect(splitterRect, MouseCursor.ResizeVertical);
            EditorGUI.DrawRect(new Rect(splitterRect.x, splitterRect.center.y, splitterRect.width, 1f), SeparatorColor);

            var currentEvent = Event.current;
            if (currentEvent.type == EventType.MouseDown && currentEvent.button == 0 && splitterRect.Contains(currentEvent.mousePosition))
            {
                dragMode = DragMode.MessageSplitter;
                currentEvent.Use();
            }
            else if (dragMode == DragMode.MessageSplitter && currentEvent.type == EventType.MouseDrag)
            {
                messageAreaHeight = Mathf.Clamp(messageAreaHeight + currentEvent.delta.y, 120f, maxMessageHeight);
                Repaint();
                currentEvent.Use();
            }
        }

        private void HandleColumnResize(ColumnLayout columns, Rect headerRect)
        {
            var statusHandle = GetColumnHandleRect(headerRect.x + columns.StatusRect.xMax, headerRect);
            var stateHandle = GetColumnHandleRect(headerRect.x + columns.PathRect.xMax, headerRect);
            EditorGUIUtility.AddCursorRect(statusHandle, MouseCursor.ResizeHorizontal);
            EditorGUIUtility.AddCursorRect(stateHandle, MouseCursor.ResizeHorizontal);

            var currentEvent = Event.current;
            if (currentEvent.type == EventType.MouseDown && currentEvent.button == 0)
            {
                if (statusHandle.Contains(currentEvent.mousePosition))
                {
                    dragMode = DragMode.StatusColumn;
                    currentEvent.Use();
                }
                else if (stateHandle.Contains(currentEvent.mousePosition))
                {
                    dragMode = DragMode.StateColumn;
                    currentEvent.Use();
                }
            }
            else if (currentEvent.type == EventType.MouseDrag)
            {
                if (dragMode == DragMode.StatusColumn)
                {
                    statusColumnWidth = Mathf.Clamp(statusColumnWidth + currentEvent.delta.x, 34f, 96f);
                    Repaint();
                    currentEvent.Use();
                }
                else if (dragMode == DragMode.StateColumn)
                {
                    stateColumnWidth = Mathf.Clamp(stateColumnWidth - currentEvent.delta.x, 72f, 180f);
                    Repaint();
                    currentEvent.Use();
                }
            }
        }

        private void DrawColumnSeparators(ColumnLayout columns, float xOffset, float yMin, float yMax)
        {
            DrawSeparatorLine(xOffset + columns.ToggleRect.xMax, yMin, yMax);
            DrawSeparatorLine(xOffset + columns.StatusRect.xMax, yMin, yMax);
            DrawSeparatorLine(xOffset + columns.PathRect.xMax, yMin, yMax);
            DrawSeparatorLine(xOffset + columns.StateRect.xMax, yMin, yMax);
        }

        private static void DrawSeparatorLine(float x, float yMin, float yMax)
        {
            EditorGUI.DrawRect(new Rect(x, yMin, 1f, yMax - yMin), SeparatorColor);
        }

        private static Rect GetColumnHandleRect(float x, Rect headerRect)
        {
            return new Rect(x - 3f, headerRect.y, 6f, headerRect.height);
        }

        private static Rect OffsetRect(Rect rect, float y)
        {
            return new Rect(rect.x, y + rect.y, rect.width, rect.height);
        }

        private static Rect TranslateRect(Rect rect, float x, float y)
        {
            return new Rect(rect.x + x, rect.y + y, rect.width, rect.height);
        }

        private ColumnLayout GetColumns(float totalWidth)
        {
            var resolvedStatusWidth = Mathf.Clamp(statusColumnWidth, 34f, 96f);
            var resolvedStateWidth = Mathf.Clamp(stateColumnWidth, 72f, 180f);
            var pathWidth = totalWidth - ToggleColumnWidth - resolvedStatusWidth - resolvedStateWidth - ActionsColumnWidth;
            if (pathWidth < MinPathColumnWidth)
            {
                var deficit = MinPathColumnWidth - pathWidth;
                var reducibleState = Mathf.Max(0f, resolvedStateWidth - 72f);
                var stateReduction = Mathf.Min(deficit, reducibleState);
                resolvedStateWidth -= stateReduction;
                deficit -= stateReduction;

                if (deficit > 0f)
                {
                    resolvedStatusWidth = Mathf.Max(34f, resolvedStatusWidth - deficit);
                }

                pathWidth = totalWidth - ToggleColumnWidth - resolvedStatusWidth - resolvedStateWidth - ActionsColumnWidth;
            }

            var x = 0f;
            var toggleRect = new Rect(x, 0f, ToggleColumnWidth, RowHeight);
            x += ToggleColumnWidth;
            var statusRect = new Rect(x, 0f, resolvedStatusWidth, RowHeight);
            x += resolvedStatusWidth;
            var pathRect = new Rect(x, 0f, Mathf.Max(MinPathColumnWidth, pathWidth), RowHeight);
            x += pathRect.width;
            var stateRect = new Rect(x, 0f, resolvedStateWidth, RowHeight);
            x += resolvedStateWidth;
            var actionsRect = new Rect(x, 0f, ActionsColumnWidth, RowHeight);
            return new ColumnLayout(toggleRect, statusRect, pathRect, stateRect, actionsRect);
        }

        private void ApplyRowSelection(SVNStatusEntry entry, int index, bool additive, bool rangeSelection)
        {
            var visibleEntries = GetVisibleEntries();
            if (entry == null)
            {
                return;
            }

            focusedPath = entry.AbsolutePath;
            if (rangeSelection && selectionAnchorIndex >= 0 && selectionAnchorIndex < visibleEntries.Count)
            {
                selectedRows.Clear();
                var start = Mathf.Min(selectionAnchorIndex, index);
                var end = Mathf.Max(selectionAnchorIndex, index);
                for (var selectionIndex = start; selectionIndex <= end; selectionIndex++)
                {
                    selectedRows.Add(visibleEntries[selectionIndex].AbsolutePath);
                }

                return;
            }

            if (additive)
            {
                if (!selectedRows.Add(entry.AbsolutePath))
                {
                    selectedRows.Remove(entry.AbsolutePath);
                }
            }
            else
            {
                selectedRows.Clear();
                selectedRows.Add(entry.AbsolutePath);
            }

            selectionAnchorIndex = index;
        }

        private void SelectAllRows()
        {
            var visibleEntries = GetVisibleEntries();
            selectedRows.Clear();
            for (var index = 0; index < visibleEntries.Count; index++)
            {
                selectedRows.Add(visibleEntries[index].AbsolutePath);
            }

            if (visibleEntries.Count > 0)
            {
                focusedPath = visibleEntries[0].AbsolutePath;
                selectionAnchorIndex = 0;
            }

            Repaint();
        }

        private void ToggleCheckedForSelectedRows()
        {
            var targets = new List<string>();
            if (selectedRows.Count > 0)
            {
                targets.AddRange(selectedRows);
            }
            else if (!string.IsNullOrEmpty(focusedPath))
            {
                targets.Add(focusedPath);
            }

            if (targets.Count == 0)
            {
                return;
            }

            var shouldSelect = false;
            for (var index = 0; index < targets.Count; index++)
            {
                if (!selectedPaths.Contains(targets[index]))
                {
                    shouldSelect = true;
                    break;
                }
            }

            for (var index = 0; index < targets.Count; index++)
            {
                SetChecked(targets[index], shouldSelect);
            }

            Repaint();
        }


        private void ApplyPendingCommitMessageSelection()
        {
            if (pendingCommitMessageSelection == null)
            {
                return;
            }

            commitMessage = pendingCommitMessageSelection;
            pendingCommitMessageSelection = null;
            GUI.FocusControl(string.Empty);
            GUIUtility.keyboardControl = 0;
            changesListHasFocus = false;
            GUI.changed = true;
        }

        private void RequestRefreshAfterCurrentOperation()
        {
            refreshRequestedAfterOperation = true;
        }
        private void SetChecked(string absolutePath, bool isChecked)
        {
            if (isChecked)
            {
                selectedPaths.Add(absolutePath);
            }
            else
            {
                selectedPaths.Remove(absolutePath);
            }
        }

        private void ShowCommitHistoryMenu()
        {
            var history = SVNToolSettings.instance.CommitMessageHistory;
            var menu = new GenericMenu();
            for (var index = 0; index < history.Count; index++)
            {
                var message = history[index];
                var displayText = message.Replace('\n', ' ');
                if (displayText.Length > 48)
                {
                    displayText = displayText.Substring(0, 48) + "...";
                }

                var capturedMessage = message;
                menu.AddItem(new GUIContent(displayText), false, () =>
                {
                    pendingCommitMessageSelection = capturedMessage;
                    Repaint();
                });
            }

            if (history.Count > 0)
            {
                menu.AddSeparator(string.Empty);
                menu.AddItem(new GUIContent("清空历史"), false, () => SVNToolSettings.instance.ClearCommitMessageHistory());
            }

            menu.ShowAsContext();
        }

        private void LoadSettings()
        {
            var settings = SVNToolSettings.instance;
            messageAreaHeight = settings.MessageAreaHeight;
            statusColumnWidth = settings.StatusColumnWidth;
            stateColumnWidth = settings.StateColumnWidth;
            showIgnoredEntries = settings.ShowIgnoredEntries;
            showUnversionedEntries = settings.ShowUnversionedEntries;
            layoutInitialized = true;
        }

        private void SaveLayoutSettings()
        {
            SVNToolSettings.instance.UpdateLayout(messageAreaHeight, statusColumnWidth, stateColumnWidth);
        }

        private bool EnsureStyles()
        {
            if (EditorStyles.label == null || EditorStyles.boldLabel == null || EditorStyles.textArea == null)
            {
                return false;
            }

            if (sectionTitleStyle != null
                && mutedLabelStyle != null
                && clippedMutedLabelStyle != null
                && headerPathLabelStyle != null
                && pathButtonStyle != null
                && commitTextStyle != null
                && badgeStyle != null)
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

            clippedMutedLabelStyle = new GUIStyle(mutedLabelStyle)
            {
                clipping = TextClipping.Clip,
            };

            headerPathLabelStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                clipping = TextClipping.Clip,
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

        private enum DragMode
        {
            None,
            MessageSplitter,
            StatusColumn,
            StateColumn,
        }

        private readonly struct ColumnLayout
        {
            public ColumnLayout(Rect toggleRect, Rect statusRect, Rect pathRect, Rect stateRect, Rect actionsRect)
            {
                ToggleRect = toggleRect;
                StatusRect = statusRect;
                PathRect = pathRect;
                StateRect = stateRect;
                ActionsRect = actionsRect;
            }

            public Rect ToggleRect { get; }

            public Rect StatusRect { get; }

            public Rect PathRect { get; }

            public Rect StateRect { get; }

            public Rect ActionsRect { get; }
        }

        private List<SVNStatusEntry> GetVisibleEntries()
        {
            List<SVNStatusEntry> entries;
            if (showIgnoredEntries)
            {
                entries = new List<SVNStatusEntry>(statusEntries);
            }
            else
            {
                entries = FilterIgnoredEntries(statusEntries, SVNToolSettings.instance);
            }

            if (!showUnversionedEntries)
            {
                entries = FilterUnversionedEntries(entries);
            }

            ApplySort(entries);
            return entries;
        }

        private void ToggleSort(SortColumn column)
        {
            if (sortColumn != column)
            {
                sortColumn = column;
                sortDirection = SortDirection.Ascending;
            }
            else if (sortDirection == SortDirection.Ascending)
            {
                sortDirection = SortDirection.Descending;
            }
            else
            {
                sortColumn = SortColumn.None;
                sortDirection = SortDirection.None;
            }

            Repaint();
        }

        private void ApplySort(List<SVNStatusEntry> entries)
        {
            if (entries == null || entries.Count <= 1 || sortColumn == SortColumn.None || sortDirection == SortDirection.None)
            {
                return;
            }

            Comparison<SVNStatusEntry> comparison;
            switch (sortColumn)
            {
                case SortColumn.Status:
                    comparison = (left, right) => string.CompareOrdinal(left?.DisplayStatus, right?.DisplayStatus);
                    break;
                case SortColumn.Path:
                    comparison = (left, right) => string.Compare(left?.RelativePath, right?.RelativePath, StringComparison.OrdinalIgnoreCase);
                    break;
                case SortColumn.Extension:
                    comparison = (left, right) =>
                    {
                        var result = string.Compare(GetExtensionText(left), GetExtensionText(right), StringComparison.OrdinalIgnoreCase);
                        return result != 0 ? result : string.Compare(left?.RelativePath, right?.RelativePath, StringComparison.OrdinalIgnoreCase);
                    };
                    break;
                default:
                    return;
            }

            entries.Sort((left, right) => sortDirection == SortDirection.Ascending ? comparison(left, right) : comparison(right, left));
        }

        private void SyncSelectionWithVisibility()
        {
            var visibleEntries = GetVisibleEntries();
            selectedRows.RemoveWhere(path => !visibleEntries.Exists(entry => entry.AbsolutePath == path));
            selectedPaths.RemoveWhere(path => !visibleEntries.Exists(entry => entry.AbsolutePath == path));
            if (!string.IsNullOrEmpty(focusedPath) && !visibleEntries.Exists(entry => entry.AbsolutePath == focusedPath))
            {
                focusedPath = string.Empty;
            }
        }

        private void SelectAllVisibleEntries(List<SVNStatusEntry> visibleEntries, bool isChecked)
        {
            for (var index = 0; index < visibleEntries.Count; index++)
            {
                SetChecked(visibleEntries[index].AbsolutePath, isChecked);
            }

            if (!isChecked)
            {
                selectedRows.Clear();
            }

            Repaint();
        }

        private List<SVNStatusEntry> GetActionEntries(SVNStatusEntry fallbackEntry)
        {
            var results = new List<SVNStatusEntry>();
            if (fallbackEntry == null)
            {
                return results;
            }

            var useSelection = selectedRows.Count > 1 && selectedRows.Contains(fallbackEntry.AbsolutePath);
            if (!useSelection)
            {
                results.Add(fallbackEntry);
                return results;
            }

            for (var index = 0; index < statusEntries.Count; index++)
            {
                var entry = statusEntries[index];
                if (selectedRows.Contains(entry.AbsolutePath))
                {
                    results.Add(entry);
                }
            }

            return results;
        }

        private List<SVNStatusEntry> GetKeyboardActionEntries()
        {
            var results = new List<SVNStatusEntry>();
            if (selectedRows.Count > 0)
            {
                for (var index = 0; index < statusEntries.Count; index++)
                {
                    var entry = statusEntries[index];
                    if (selectedRows.Contains(entry.AbsolutePath))
                    {
                        results.Add(entry);
                    }
                }

                return results;
            }

            if (string.IsNullOrEmpty(focusedPath))
            {
                return results;
            }

            for (var index = 0; index < statusEntries.Count; index++)
            {
                var entry = statusEntries[index];
                if (string.Equals(entry.AbsolutePath, focusedPath, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(entry);
                    break;
                }
            }

            return results;
        }

        private List<SVNStatusEntry> BuildDeleteTargets(IReadOnlyList<SVNStatusEntry> entries)
        {
            var results = new List<SVNStatusEntry>();
            if (entries == null)
            {
                return results;
            }

            var orderedEntries = new List<SVNStatusEntry>(entries);
            orderedEntries.Sort((left, right) => string.CompareOrdinal(right.AbsolutePath, left.AbsolutePath));
            for (var index = 0; index < orderedEntries.Count; index++)
            {
                var currentEntry = orderedEntries[index];
                var shouldSkip = false;
                for (var parentIndex = 0; parentIndex < results.Count; parentIndex++)
                {
                    var existingPath = results[parentIndex].AbsolutePath;
                    if (currentEntry.AbsolutePath.StartsWith(existingPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        || currentEntry.AbsolutePath.StartsWith(existingPath + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        shouldSkip = true;
                        break;
                    }
                }

                if (!shouldSkip)
                {
                    results.Add(currentEntry);
                }
            }

            return results;
        }

        private void AddEntriesToIgnore(IReadOnlyList<SVNStatusEntry> entries)
        {
            if (entries == null || entries.Count == 0)
            {
                return;
            }

            var settings = SVNToolSettings.instance;
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                if (entry == null || string.IsNullOrEmpty(entry.RelativePath))
                {
                    continue;
                }

                if (Directory.Exists(entry.AbsolutePath))
                {
                    settings.AddIgnoredDirectory(entry.RelativePath);
                }
                else
                {
                    settings.AddIgnoredFile(entry.RelativePath);
                }
            }

            lastStatusMessage = entries.Count == 1
                ? $"已添加到忽略列表: {entries[0].RelativePath}"
                : $"已添加 {entries.Count} 项到忽略列表";
            RequestRefreshAll();
        }

        private string GetBusyStatusText()
        {
            return string.IsNullOrEmpty(currentOperationStep)
                ? currentOperationLabel
                : currentOperationLabel + "  步骤: " + currentOperationStep;
        }

        private enum SortColumn
        {
            None,
            Status,
            Path,
            Extension,
        }

        private enum SortDirection
        {
            None,
            Ascending,
            Descending,
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
