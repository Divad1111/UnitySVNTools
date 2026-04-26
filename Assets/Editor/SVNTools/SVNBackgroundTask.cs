using System;
using System.Threading.Tasks;
using UnityEditor;

namespace UnitySVNTools.Editor
{
    internal sealed class SVNBackgroundTask<T>
    {
        private readonly Task<T> task;
        private readonly Action<T> onSuccess;
        private readonly Action<Exception> onFailure;
        private readonly Action onCompleted;
        private bool completionHandled;

        private SVNBackgroundTask(Task<T> task, Action<T> onSuccess, Action<Exception> onFailure, Action onCompleted)
        {
            this.task = task;
            this.onSuccess = onSuccess;
            this.onFailure = onFailure;
            this.onCompleted = onCompleted;
            EditorApplication.update += Poll;
        }

        public static SVNBackgroundTask<T> Run(Func<T> work, Action<T> onSuccess, Action<Exception> onFailure, Action onCompleted)
        {
            return new SVNBackgroundTask<T>(Task.Run(work), onSuccess, onFailure, onCompleted);
        }

        private void Poll()
        {
            if (!task.IsCompleted)
            {
                return;
            }

            EditorApplication.update -= Poll;
            if (completionHandled)
            {
                return;
            }

            completionHandled = true;

            try
            {
                if (task.IsFaulted)
                {
                    onFailure?.Invoke(task.Exception?.GetBaseException() ?? new Exception("Unknown SVN task error."));
                    return;
                }

                if (task.IsCanceled)
                {
                    onFailure?.Invoke(new OperationCanceledException("SVN task was cancelled."));
                    return;
                }

                onSuccess?.Invoke(task.Result);
            }
            finally
            {
                onCompleted?.Invoke();
            }
        }
    }
}