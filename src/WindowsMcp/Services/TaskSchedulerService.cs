using WinTask = Microsoft.Win32.TaskScheduler.Task;
using Microsoft.Win32.TaskScheduler;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

// Suppress ambiguous-reference: TaskScheduler library exports 'Task' which clashes with
// System.Threading.Tasks.Task. We alias the library type above and use System.Threading.Tasks
// explicitly via 'return System.Threading.Tasks.Task.FromResult(...)' patterns.
using SystemTask = System.Threading.Tasks.Task;

namespace WindowsMcp.Services;

public sealed class TaskSchedulerService : ITaskSchedulerService
{
    public System.Threading.Tasks.Task<ScheduledTaskDto[]> ListAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var ts = new TaskService();
        var tasks = ts.RootFolder.AllTasks
            .Select(t => new ScheduledTaskDto(t.Name, t.Path, t.State.ToString(), t.LastRunTime, t.NextRunTime))
            .ToArray();
        return System.Threading.Tasks.Task.FromResult(tasks);
    }

    public System.Threading.Tasks.Task<ScheduledTaskDto> GetAsync(string name, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var ts = new TaskService();
        var t = ts.GetTask(name)
            ?? throw new KeyNotFoundException($"Scheduled task '{name}' not found");
        return System.Threading.Tasks.Task.FromResult(
            new ScheduledTaskDto(t.Name, t.Path, t.State.ToString(), t.LastRunTime, t.NextRunTime));
    }

    public SystemTask RunAsync(string name, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var ts = new TaskService();
        var t = ts.GetTask(name) ?? throw new KeyNotFoundException(name);
        t.Run();
        return SystemTask.CompletedTask;
    }

    public SystemTask CreateAsync(string name, string command, string trigger, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var ts = new TaskService();
        var td = ts.NewTask();
        td.Actions.Add(new ExecAction(command));
        td.Triggers.Add(new TimeTrigger(DateTime.Parse(trigger)));
        ts.RootFolder.RegisterTaskDefinition(name, td);
        return SystemTask.CompletedTask;
    }

    public SystemTask DeleteAsync(string name, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var ts = new TaskService();
        ts.RootFolder.DeleteTask(name);
        return SystemTask.CompletedTask;
    }
}
