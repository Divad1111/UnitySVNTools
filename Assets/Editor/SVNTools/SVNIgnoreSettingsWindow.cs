using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnitySVNTools.Editor
{
    internal sealed class SVNIgnoreSettingsWindow : EditorWindow
    {
        private Vector2 scrollPosition;

        public static void ShowWindow()
        {
            var window = GetWindow<SVNIgnoreSettingsWindow>(true, "忽略目录配置");
            window.minSize = new Vector2(480f, 280f);
            window.Show();
        }

        private void OnGUI()
        {
            var settings = SVNIgnoreSettings.instance;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("忽略目录列表", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("忽略目录中的改动不会显示在改动列表里，也不会参与提交。", MessageType.Info);

            using (var scrollView = new EditorGUILayout.ScrollViewScope(scrollPosition))
            {
                scrollPosition = scrollView.scrollPosition;
                if (settings.IgnoredRelativeDirectories.Count == 0)
                {
                    EditorGUILayout.LabelField("当前没有忽略目录。", EditorStyles.centeredGreyMiniLabel);
                }

                for (var index = 0; index < settings.IgnoredRelativeDirectories.Count; index++)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.SelectableLabel(settings.IgnoredRelativeDirectories[index], GUILayout.Height(EditorGUIUtility.singleLineHeight));
                        if (GUILayout.Button("移除", GUILayout.Width(72f)))
                        {
                            settings.RemoveIgnoredDirectoryAt(index);
                            SVNToolsWindow.RequestRefreshAll();
                            GUIUtility.ExitGUI();
                        }
                    }
                }
            }

            GUILayout.FlexibleSpace();

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("添加目录", GUILayout.Width(120f)))
                {
                    AddDirectory();
                }
            }
        }

        private static void AddDirectory()
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            var selectedPath = EditorUtility.OpenFolderPanel("选择忽略目录", projectRoot, string.Empty);
            if (string.IsNullOrEmpty(selectedPath))
            {
                return;
            }

            if (!selectedPath.StartsWith(projectRoot))
            {
                EditorUtility.DisplayDialog("目录无效", "请选择当前项目目录中的子目录。", "确定");
                return;
            }

            var relativePath = selectedPath.Substring(projectRoot.Length).Replace('\\', '/').Trim('/');
            SVNIgnoreSettings.instance.AddIgnoredDirectory(relativePath);
            SVNToolsWindow.RequestRefreshAll();
        }
    }
}