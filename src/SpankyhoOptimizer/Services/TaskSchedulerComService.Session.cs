using System;

namespace SpankyhoOptimizer.Services;

internal static partial class TaskSchedulerComService
{
    // Schedule.Service must be created, connected and released around every
    // single-task call; keep that exact sequence in one place for both partials.
    private sealed class TaskComSession : IDisposable
    {
        private TaskComSession(string taskName) => TaskName = taskName;

        internal string TaskName { get; }
        internal string FolderPath { get; private set; } = "\\";
        internal object? Service { get; private set; }
        internal object? Folder { get; private set; }

        internal static TaskComSession Open(string path, out dynamic folder)
        {
            int split = path.LastIndexOf('\\');
            var session = new TaskComSession(path[(split + 1)..]);
            session.FolderPath = split <= 0 ? "\\" : path[..split];
            try
            {
                var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: false)
                    ?? throw new PlatformNotSupportedException("Task Scheduler COM není dostupný.");
                session.Service = Activator.CreateInstance(type)
                    ?? throw new InvalidOperationException("Task Scheduler COM nelze vytvořit.");
                dynamic service = session.Service;
                service.Connect();
                session.Folder = service.GetFolder(session.FolderPath);
                folder = session.Folder;
                return session;
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Release(Folder);
            Release(Service);
            Folder = null;
            Service = null;
        }
    }
}
