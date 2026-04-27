using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Xml.Linq;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UnitySVNTools.Editor
{
    internal static class SVNClient
    {
        private static readonly string ProjectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;

        public static bool TryGetRepositoryInfo(out SVNRepositoryInfo repositoryInfo, out string error)
        {
            repositoryInfo = null;
            error = string.Empty;

            var result = RunCommand(ProjectRoot, "info", "--xml");
            if (!result.Success)
            {
                error = result.CombinedOutput;
                return false;
            }

            try
            {
                var document = XDocument.Parse(result.StandardOutput);
                var entry = document.Root?.Element("entry");
                if (entry == null)
                {
                    error = "Failed to parse svn info output.";
                    return false;
                }

                repositoryInfo = new SVNRepositoryInfo
                {
                    WorkingCopyRoot = entry.Attribute("path")?.Value ?? ProjectRoot,
                    RepositoryUrl = entry.Element("url")?.Value ?? string.Empty,
                    RepositoryRootUrl = entry.Element("repository")?.Element("root")?.Value ?? string.Empty,
                };

                if (!Path.IsPathRooted(repositoryInfo.WorkingCopyRoot))
                {
                    repositoryInfo.WorkingCopyRoot = ProjectRoot;
                }

                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        public static List<SVNStatusEntry> GetStatusEntries(SVNRepositoryInfo repositoryInfo, SVNToolSettings ignoreSettings, out string error)
        {
            error = string.Empty;
            var entries = new List<SVNStatusEntry>();
            if (repositoryInfo == null)
            {
                error = "Repository info is not available.";
                return entries;
            }

            var result = RunCommand(repositoryInfo.WorkingCopyRoot, "status", "--xml");
            if (!result.Success)
            {
                error = result.CombinedOutput;
                return entries;
            }

            try
            {
                var document = XDocument.Parse(result.StandardOutput);
                foreach (var target in document.Descendants("target"))
                {
                    foreach (var entry in target.Elements("entry"))
                    {
                        var fullPath = entry.Attribute("path")?.Value ?? string.Empty;
                        if (string.IsNullOrEmpty(fullPath))
                        {
                            continue;
                        }

                        var absolutePath = Path.IsPathRooted(fullPath) ? fullPath : Path.GetFullPath(Path.Combine(repositoryInfo.WorkingCopyRoot, fullPath));
                        var relativePath = GetRelativePath(repositoryInfo.WorkingCopyRoot, absolutePath);
                        if (ignoreSettings != null && ignoreSettings.IsIgnored(relativePath))
                        {
                            continue;
                        }

                        var wcStatus = entry.Element("wc-status");
                        var item = wcStatus?.Attribute("item")?.Value ?? string.Empty;
                        var parsed = CreateStatusEntry(absolutePath, relativePath, item);
                        if (parsed != null)
                        {
                            entries.Add(parsed);
                        }
                    }
                }

                return entries;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return entries;
            }
        }

        public static bool UpdateWorkingCopy(SVNRepositoryInfo repositoryInfo, out string output)
        {
            var result = RunCommand(repositoryInfo.WorkingCopyRoot, "update");
            output = result.CombinedOutput;
            return result.Success;
        }

        public static bool RevertPath(SVNRepositoryInfo repositoryInfo, string absolutePath, out string output)
        {
            if (!File.Exists(absolutePath) && !Directory.Exists(absolutePath))
            {
                output = "The selected path does not exist anymore.";
                return false;
            }

            var status = RunCommand(repositoryInfo.WorkingCopyRoot, $"status {Quote(GetRelativePath(repositoryInfo.WorkingCopyRoot, absolutePath))}");
            if (status.Success && status.StandardOutput.StartsWith("?", StringComparison.Ordinal))
            {
                return DeleteUnversionedPath(absolutePath, out output);
            }

            var relativePath = GetRelativePath(repositoryInfo.WorkingCopyRoot, absolutePath);
            var result = RunCommand(repositoryInfo.WorkingCopyRoot, "revert", $"--depth infinity {Quote(relativePath)}");
            if (!result.Success)
            {
                output = result.CombinedOutput;
                return false;
            }

            var validation = RunCommand(repositoryInfo.WorkingCopyRoot, $"status {Quote(relativePath)}");
            output = string.IsNullOrWhiteSpace(result.CombinedOutput) ? "Reverted path." : result.CombinedOutput;
            return validation.Success && string.IsNullOrWhiteSpace(validation.StandardOutput);
        }

        public static bool DeleteLocalPath(string absolutePath, out string output)
        {
            output = string.Empty;
            if (!File.Exists(absolutePath) && !Directory.Exists(absolutePath))
            {
                output = "The selected path does not exist anymore.";
                return false;
            }

            try
            {
                if (File.Exists(absolutePath))
                {
                    File.Delete(absolutePath);
                }
                else
                {
                    Directory.Delete(absolutePath, true);
                }

                var metaFilePath = absolutePath + ".meta";
                if (File.Exists(metaFilePath))
                {
                    File.Delete(metaFilePath);
                }

                output = "Deleted local path.";
                return true;
            }
            catch (Exception exception)
            {
                output = exception.Message;
                return false;
            }
        }

        public static bool OpenContainingFolder(string absolutePath, out string output)
        {
            output = string.Empty;
            if (string.IsNullOrWhiteSpace(absolutePath))
            {
                output = "The selected path is empty.";
                return false;
            }

            try
            {
                var targetPath = Directory.Exists(absolutePath)
                    ? absolutePath
                    : Path.GetDirectoryName(absolutePath);
                if (string.IsNullOrWhiteSpace(targetPath) || !Directory.Exists(targetPath))
                {
                    output = "The containing folder does not exist.";
                    return false;
                }

                OpenPath(targetPath);
                output = "Opened containing folder.";
                return true;
            }
            catch (Exception exception)
            {
                output = exception.Message;
                return false;
            }
        }

        public static bool Commit(SVNRepositoryInfo repositoryInfo, IList<SVNStatusEntry> entries, string message, out string output)
        {
            output = string.Empty;
            if (entries == null || entries.Count == 0)
            {
                output = "There are no files to commit.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                output = "Commit message is required.";
                return false;
            }

            var paths = new List<string>();
            foreach (var entry in entries)
            {
                if (entry == null)
                {
                    continue;
                }

                var relativePath = GetRelativePath(repositoryInfo.WorkingCopyRoot, entry.AbsolutePath);
                if (entry.WorkingCopyStatus == "unversioned")
                {
                    var addResult = RunCommand(repositoryInfo.WorkingCopyRoot, "add", Quote(relativePath));
                    if (!addResult.Success)
                    {
                        output = addResult.CombinedOutput;
                        return false;
                    }
                }
                else if (entry.WorkingCopyStatus == "missing")
                {
                    var deleteResult = RunCommand(repositoryInfo.WorkingCopyRoot, "delete", Quote(relativePath));
                    if (!deleteResult.Success)
                    {
                        output = deleteResult.CombinedOutput;
                        return false;
                    }
                }

                paths.Add(Quote(relativePath));
            }

            var commitArguments = new StringBuilder();
            commitArguments.Append("commit -m ");
            commitArguments.Append(Quote(message));
            for (var index = 0; index < paths.Count; index++)
            {
                commitArguments.Append(' ');
                commitArguments.Append(paths[index]);
            }

            var commitResult = RunCommand(repositoryInfo.WorkingCopyRoot, commitArguments.ToString());
            output = commitResult.CombinedOutput;
            return commitResult.Success;
        }

        public static bool ShowDiff(SVNRepositoryInfo repositoryInfo, SVNStatusEntry entry, out string output)
        {
            return ExecuteExternalOrFallback(repositoryInfo, entry, true, out output);
        }

        public static bool ShowLog(SVNRepositoryInfo repositoryInfo, SVNStatusEntry entry, out string output)
        {
            return ExecuteExternalOrFallback(repositoryInfo, entry, false, out output);
        }

        private static SVNStatusEntry CreateStatusEntry(string absolutePath, string relativePath, string item)
        {
            switch (item)
            {
                case "added":
                    return CreateEntry(absolutePath, relativePath, item, "A", true);
                case "modified":
                case "replaced":
                case "conflicted":
                    return CreateEntry(absolutePath, relativePath, item, "U", true);
                case "deleted":
                case "missing":
                    return CreateEntry(absolutePath, relativePath, item, "D", true);
                case "unversioned":
                    return CreateEntry(absolutePath, relativePath, item, "A", false);
                default:
                    return null;
            }
        }

        private static SVNStatusEntry CreateEntry(string absolutePath, string relativePath, string status, string displayStatus, bool isVersioned)
        {
            return new SVNStatusEntry
            {
                AbsolutePath = absolutePath,
                RelativePath = relativePath,
                WorkingCopyStatus = status,
                DisplayStatus = displayStatus,
                IsVersioned = isVersioned,
            };
        }

        private static bool ExecuteExternalOrFallback(SVNRepositoryInfo repositoryInfo, SVNStatusEntry entry, bool showDiff, out string output)
        {
            output = string.Empty;
            if (repositoryInfo == null || entry == null)
            {
                output = "Repository or entry is missing.";
                return false;
            }

            if (showDiff && IsAddedFilePreviewEntry(entry))
            {
                if (TryRunAddedFileDiff(entry.AbsolutePath, out output))
                {
                    return true;
                }

                return OpenFileContentPreview(entry, out output);
            }

            if (TryRunTortoiseProc(entry.AbsolutePath, showDiff, out output))
            {
                return true;
            }

            var relativePath = GetRelativePath(repositoryInfo.WorkingCopyRoot, entry.AbsolutePath);
            var command = showDiff ? "diff" : "log";
            var arguments = showDiff ? $"diff {Quote(relativePath)}" : $"log {Quote(relativePath)} -l 30";
            var result = RunCommand(repositoryInfo.WorkingCopyRoot, arguments);
            if (!result.Success)
            {
                output = result.CombinedOutput;
                return false;
            }

            var extension = showDiff ? ".diff.txt" : ".log.txt";
            var tempFile = Path.Combine(Path.GetTempPath(), $"unity-svn-{Path.GetFileName(relativePath)}{extension}");
            File.WriteAllText(tempFile, result.StandardOutput);
            OpenPath(tempFile);
            output = $"Opened {command} output for {relativePath}.";
            return true;
        }

        private static bool IsAddedFilePreviewEntry(SVNStatusEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.AbsolutePath) || !File.Exists(entry.AbsolutePath))
            {
                return false;
            }

            return string.Equals(entry.WorkingCopyStatus, "added", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.WorkingCopyStatus, "unversioned", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryRunAddedFileDiff(string absolutePath, out string output)
        {
            output = string.Empty;
            if (!TryGetTortoiseProcPath(out var tortoiseProcPath))
            {
                return false;
            }

            try
            {
                var leftPath = CreateEmptyDiffBaseFile(absolutePath);
                var startInfo = new ProcessStartInfo
                {
                    FileName = tortoiseProcPath,
                    Arguments = $"/command:diff /path:{Quote(absolutePath)} /path2:{Quote(leftPath)}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                Process.Start(startInfo);
                output = "Opened added file preview in TortoiseSVN.";
                return true;
            }
            catch (Exception exception)
            {
                output = exception.Message;
                return false;
            }
        }

        private static bool OpenFileContentPreview(SVNStatusEntry entry, out string output)
        {
            output = string.Empty;
            if (entry == null || string.IsNullOrWhiteSpace(entry.AbsolutePath) || !File.Exists(entry.AbsolutePath))
            {
                output = "The selected file does not exist.";
                return false;
            }

            try
            {
                var extension = Path.GetExtension(entry.AbsolutePath);
                if (string.IsNullOrWhiteSpace(extension))
                {
                    extension = ".txt";
                }

                var previewPath = Path.Combine(
                    Path.GetTempPath(),
                    $"unity-svn-preview-{Path.GetFileNameWithoutExtension(entry.AbsolutePath)}-{Guid.NewGuid():N}{extension}");
                File.Copy(entry.AbsolutePath, previewPath, true);
                OpenPath(previewPath);
                output = $"Opened file preview for {entry.RelativePath}.";
                return true;
            }
            catch (Exception exception)
            {
                output = exception.Message;
                return false;
            }
        }

        private static bool TryRunTortoiseProc(string absolutePath, bool showDiff, out string output)
        {
            output = string.Empty;
            if (Application.platform != RuntimePlatform.WindowsEditor)
            {
                return false;
            }

            if (!TryGetTortoiseProcPath(out var tortoiseProcPath))
            {
                return false;
            }

            var command = showDiff ? "diff" : "log";
            var startInfo = new ProcessStartInfo
            {
                FileName = tortoiseProcPath,
                Arguments = $"/command:{command} /path:{Quote(absolutePath)}",
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            Process.Start(startInfo);
            output = $"Opened {command} in TortoiseSVN.";
            return true;

        }

        private static bool TryGetTortoiseProcPath(out string tortoiseProcPath)
        {
            tortoiseProcPath = string.Empty;
            if (Application.platform != RuntimePlatform.WindowsEditor)
            {
                return false;
            }

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var candidates = new[]
            {
                Path.Combine(programFiles, "TortoiseSVN", "bin", "TortoiseProc.exe"),
                Path.Combine(programFilesX86, "TortoiseSVN", "bin", "TortoiseProc.exe"),
            };

            for (var index = 0; index < candidates.Length; index++)
            {
                var candidate = candidates[index];
                if (!File.Exists(candidate))
                {
                    continue;
                }

                tortoiseProcPath = candidate;
                return true;
            }

            return false;
        }

        private static string CreateEmptyDiffBaseFile(string absolutePath)
        {
            var extension = Path.GetExtension(absolutePath);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".txt";
            }

            var tempFilePath = Path.Combine(
                Path.GetTempPath(),
                $"unity-svn-empty-{Path.GetFileNameWithoutExtension(absolutePath)}-{Guid.NewGuid():N}{extension}");
            File.WriteAllText(tempFilePath, string.Empty, Encoding.UTF8);
            return tempFilePath;
        }

        private static void OpenPath(string path)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                };

                Process.Start(startInfo);
            }
            catch (Exception exception)
            {
                Debug.LogError(exception);
            }
        }

        private static bool DeleteUnversionedPath(string absolutePath, out string output)
        {
            output = string.Empty;

            try
            {
                if (File.Exists(absolutePath))
                {
                    File.Delete(absolutePath);
                }
                else if (Directory.Exists(absolutePath))
                {
                    Directory.Delete(absolutePath, true);
                }

                var metaFilePath = absolutePath + ".meta";
                if (File.Exists(metaFilePath))
                {
                    File.Delete(metaFilePath);
                }

                output = "Deleted unversioned path.";
                return true;
            }
            catch (Exception exception)
            {
                output = exception.Message;
                return false;
            }
        }

        private static SVNCommandResult RunCommand(string workingDirectory, string arguments)
        {
            return RunCommand(workingDirectory, arguments, (Action<ProcessStartInfo>)null);
        }

        private static SVNCommandResult RunCommand(string workingDirectory, string command, string extraArguments)
        {
            var arguments = string.IsNullOrWhiteSpace(extraArguments) ? command : command + " " + extraArguments;
            return RunCommand(workingDirectory, arguments);
        }

        private static SVNCommandResult RunCommand(string workingDirectory, string arguments, Action<ProcessStartInfo> configure)
        {
            var result = new SVNCommandResult();
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "svn",
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };

                configure?.Invoke(startInfo);
                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        result.StandardError = "Failed to start svn process.";
                        return result;
                    }

                    result.StandardOutput = process.StandardOutput.ReadToEnd();
                    result.StandardError = process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    result.ExitCode = process.ExitCode;
                    result.Success = process.ExitCode == 0;
                }
            }
            catch (Exception exception)
            {
                result.Success = false;
                result.StandardError = exception.Message;
            }

            return result;
        }

        private static string GetRelativePath(string rootPath, string absolutePath)
        {
            var normalizedRoot = EnsureTrailingSeparator(rootPath.Replace('\\', '/'));
            var normalizedPath = absolutePath.Replace('\\', '/');
            if (normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return normalizedPath.Substring(normalizedRoot.Length);
            }

            return Path.GetFileName(absolutePath);
        }

        private static string EnsureTrailingSeparator(string path)
        {
            return path.EndsWith("/", StringComparison.Ordinal) ? path : path + "/";
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }
}