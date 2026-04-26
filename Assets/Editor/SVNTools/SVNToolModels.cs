using System;

namespace UnitySVNTools.Editor
{
    [Serializable]
    internal sealed class SVNRepositoryInfo
    {
        public string WorkingCopyRoot = string.Empty;
        public string RepositoryUrl = string.Empty;
        public string RepositoryRootUrl = string.Empty;

        public string DisplayBranch
        {
            get
            {
                if (string.IsNullOrEmpty(RepositoryUrl) || string.IsNullOrEmpty(RepositoryRootUrl))
                {
                    return "Unknown";
                }

                if (!RepositoryUrl.StartsWith(RepositoryRootUrl, StringComparison.OrdinalIgnoreCase))
                {
                    return RepositoryUrl;
                }

                var relative = RepositoryUrl.Substring(RepositoryRootUrl.Length).Trim('/');
                return string.IsNullOrEmpty(relative) ? "/" : relative;
            }
        }
    }

    [Serializable]
    internal sealed class SVNStatusEntry
    {
        public string AbsolutePath = string.Empty;
        public string RelativePath = string.Empty;
        public string WorkingCopyStatus = string.Empty;
        public string DisplayStatus = string.Empty;
        public bool IsVersioned;
    }

    internal sealed class SVNCommandResult
    {
        public bool Success;
        public int ExitCode;
        public string StandardOutput = string.Empty;
        public string StandardError = string.Empty;

        public string CombinedOutput
        {
            get
            {
                if (string.IsNullOrEmpty(StandardError))
                {
                    return StandardOutput;
                }

                if (string.IsNullOrEmpty(StandardOutput))
                {
                    return StandardError;
                }

                return StandardOutput + Environment.NewLine + StandardError;
            }
        }
    }
}