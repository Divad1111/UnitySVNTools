using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnitySVNTools.Editor
{
    [FilePath("UnitySVNTools/Settings.asset", FilePathAttribute.Location.PreferencesFolder)]
    internal sealed class SVNToolSettings : ScriptableSingleton<SVNToolSettings>
    {
        private const int MaxCommitHistoryCount = 20;
        private const float MinMessageAreaHeight = 176f;

        [SerializeField] private List<string> ignoredRelativeDirectories = new List<string>();
        [SerializeField] private List<string> ignoredRelativeFiles = new List<string>();
        [SerializeField] private List<string> commitMessageHistory = new List<string>();
        [SerializeField] private float messageAreaHeight = 156f;
        [SerializeField] private float statusColumnWidth = 44f;
        [SerializeField] private float stateColumnWidth = 104f;
        [SerializeField] private bool showIgnoredEntries;
        [SerializeField] private bool showUnversionedEntries = true;
        [SerializeField] private bool hasShowUnversionedEntriesPreference;

        public IReadOnlyList<string> IgnoredRelativeDirectories => ignoredRelativeDirectories;

        public IReadOnlyList<string> IgnoredRelativeFiles => ignoredRelativeFiles;

        public IReadOnlyList<string> CommitMessageHistory => commitMessageHistory;

        public float MessageAreaHeight => Mathf.Max(MinMessageAreaHeight, messageAreaHeight);

        public float StatusColumnWidth => Mathf.Clamp(statusColumnWidth, 34f, 96f);

        public float StateColumnWidth => Mathf.Clamp(stateColumnWidth, 72f, 180f);

        public bool ShowIgnoredEntries => showIgnoredEntries;

        public bool ShowUnversionedEntries => hasShowUnversionedEntriesPreference ? showUnversionedEntries : true;

        public void AddIgnoredDirectory(string relativeDirectory)
        {
            AddIgnoredPath(relativeDirectory, ignoredRelativeDirectories);
        }

        public void AddIgnoredFile(string relativeFile)
        {
            AddIgnoredPath(relativeFile, ignoredRelativeFiles);
        }

        public void RemoveIgnoredDirectoryAt(int index)
        {
            if (index < 0 || index >= ignoredRelativeDirectories.Count)
            {
                return;
            }

            ignoredRelativeDirectories.RemoveAt(index);
            Save(true);
        }

        public void RemoveIgnoredFileAt(int index)
        {
            if (index < 0 || index >= ignoredRelativeFiles.Count)
            {
                return;
            }

            ignoredRelativeFiles.RemoveAt(index);
            Save(true);
        }

        public void RemoveIgnoredDirectory(string relativeDirectory)
        {
            RemoveIgnoredPath(relativeDirectory, ignoredRelativeDirectories);
        }

        public void RemoveIgnoredFile(string relativeFile)
        {
            RemoveIgnoredPath(relativeFile, ignoredRelativeFiles);
        }

        public bool IsIgnored(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
            {
                return false;
            }

            var normalizedPath = Normalize(relativePath);
            for (var index = 0; index < ignoredRelativeFiles.Count; index++)
            {
                var ignoredFile = Normalize(ignoredRelativeFiles[index]);
                if (string.IsNullOrEmpty(ignoredFile))
                {
                    continue;
                }

                if (normalizedPath.Equals(ignoredFile, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            for (var index = 0; index < ignoredRelativeDirectories.Count; index++)
            {
                var ignored = Normalize(ignoredRelativeDirectories[index]);
                if (string.IsNullOrEmpty(ignored))
                {
                    continue;
                }

                if (normalizedPath.Equals(ignored, StringComparison.OrdinalIgnoreCase) || normalizedPath.StartsWith(ignored + "/", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public void AddCommitMessageHistory(string message)
        {
            var normalized = NormalizeMessage(message);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            commitMessageHistory.RemoveAll(existing => string.Equals(existing, normalized, StringComparison.Ordinal));
            commitMessageHistory.Insert(0, normalized);
            if (commitMessageHistory.Count > MaxCommitHistoryCount)
            {
                commitMessageHistory.RemoveRange(MaxCommitHistoryCount, commitMessageHistory.Count - MaxCommitHistoryCount);
            }

            Save(true);
        }

        public void ClearCommitMessageHistory()
        {
            if (commitMessageHistory.Count == 0)
            {
                return;
            }

            commitMessageHistory.Clear();
            Save(true);
        }

        public void UpdateLayout(float newMessageAreaHeight, float newStatusColumnWidth, float newStateColumnWidth)
        {
            var clampedMessageHeight = Mathf.Max(MinMessageAreaHeight, newMessageAreaHeight);
            var clampedStatusWidth = Mathf.Clamp(newStatusColumnWidth, 34f, 96f);
            var clampedStateWidth = Mathf.Clamp(newStateColumnWidth, 72f, 180f);
            if (Mathf.Approximately(messageAreaHeight, clampedMessageHeight)
                && Mathf.Approximately(statusColumnWidth, clampedStatusWidth)
                && Mathf.Approximately(stateColumnWidth, clampedStateWidth))
            {
                return;
            }

            messageAreaHeight = clampedMessageHeight;
            statusColumnWidth = clampedStatusWidth;
            stateColumnWidth = clampedStateWidth;
            Save(true);
        }

        public void SetShowIgnoredEntries(bool value)
        {
            if (showIgnoredEntries == value)
            {
                return;
            }

            showIgnoredEntries = value;
            Save(true);
        }

        public void SetShowUnversionedEntries(bool value)
        {
            if (hasShowUnversionedEntriesPreference && showUnversionedEntries == value)
            {
                return;
            }

            showUnversionedEntries = value;
            hasShowUnversionedEntriesPreference = true;
            Save(true);
        }

        public void ClearIgnoredEntries()
        {
            if (ignoredRelativeDirectories.Count == 0 && ignoredRelativeFiles.Count == 0)
            {
                return;
            }

            ignoredRelativeDirectories.Clear();
            ignoredRelativeFiles.Clear();
            Save(true);
        }

        private void AddIgnoredPath(string relativePath, List<string> target)
        {
            var normalized = Normalize(relativePath);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            for (var index = 0; index < target.Count; index++)
            {
                if (string.Equals(Normalize(target[index]), normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            target.Add(normalized);
            Save(true);
        }

        private void RemoveIgnoredPath(string relativePath, List<string> target)
        {
            var normalized = Normalize(relativePath);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            var removed = target.RemoveAll(existing => string.Equals(Normalize(existing), normalized, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                Save(true);
            }
        }

        private static string Normalize(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/').Trim('/');
        }

        private static string NormalizeMessage(string message)
        {
            return (message ?? string.Empty).Replace("\r\n", "\n").Trim();
        }
    }
}