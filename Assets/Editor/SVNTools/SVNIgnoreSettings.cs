using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnitySVNTools.Editor
{
    internal sealed class SVNIgnoreSettings : ScriptableSingleton<SVNIgnoreSettings>
    {
        [SerializeField] private List<string> ignoredRelativeDirectories = new List<string>();

        public IReadOnlyList<string> IgnoredRelativeDirectories => ignoredRelativeDirectories;

        public void AddIgnoredDirectory(string relativeDirectory)
        {
            if (string.IsNullOrWhiteSpace(relativeDirectory) || ignoredRelativeDirectories.Contains(relativeDirectory))
            {
                return;
            }

            ignoredRelativeDirectories.Add(relativeDirectory);
            Save(true);
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

        public bool IsIgnored(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
            {
                return false;
            }

            var normalizedPath = Normalize(relativePath);
            for (var index = 0; index < ignoredRelativeDirectories.Count; index++)
            {
                var ignored = Normalize(ignoredRelativeDirectories[index]);
                if (string.IsNullOrEmpty(ignored))
                {
                    continue;
                }

                if (normalizedPath.Equals(ignored) || normalizedPath.StartsWith(ignored + "/"))
                {
                    return true;
                }
            }

            return false;
        }

        private static string Normalize(string path)
        {
            return path.Replace('\\', '/').Trim('/');
        }
    }
}