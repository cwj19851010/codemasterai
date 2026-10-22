using System.Diagnostics;
using System.Text.Json;

namespace CodeMaster.LocalAgent.Services;

internal static class LocalProjectProcessStore
{
    private const string StateFileName = "local-processes.json";
    private static readonly object SyncRoot = new();

    public static void Track(string projectPath, string kind, int processId)
    {
        ValidateKind(kind);
        if (processId <= 0)
            throw new ArgumentOutOfRangeException(nameof(processId));

        lock (SyncRoot)
        {
            var state = Read(projectPath);
            state.Processes[kind] = CreateIdentity(processId);
            Write(projectPath, state);
        }
    }

    public static bool TryGet(string projectPath, string kind, out int processId)
    {
        ValidateKind(kind);
        lock (SyncRoot)
        {
            if (Read(projectPath).Processes.TryGetValue(kind, out var identity))
            {
                processId = identity.ProcessId;
                return true;
            }

            processId = 0;
            return false;
        }
    }

    public static bool TryOpenOwnedProcess(
        string projectPath,
        string kind,
        int expectedProcessId,
        out Process? process)
    {
        ValidateKind(kind);
        ProcessIdentity? identity;
        lock (SyncRoot)
        {
            Read(projectPath).Processes.TryGetValue(kind, out identity);
        }

        process = null;
        if (identity is not { StartTimeUtcTicks: > 0 } || identity.ProcessId != expectedProcessId)
            return false;

        try
        {
            var candidate = Process.GetProcessById(expectedProcessId);
            if (candidate.HasExited || candidate.StartTime.ToUniversalTime().Ticks != identity.StartTimeUtcTicks)
            {
                candidate.Dispose();
                return false;
            }

            process = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public static void Remove(string projectPath, string kind)
    {
        ValidateKind(kind);
        lock (SyncRoot)
        {
            var state = Read(projectPath);
            if (!state.Processes.Remove(kind))
                return;

            Write(projectPath, state);
        }
    }

    internal static string GetStateFilePath(string projectPath)
    {
        return Path.Combine(Path.GetFullPath(projectPath), ".codemaster", StateFileName);
    }

    private static ProcessState Read(string projectPath)
    {
        var path = GetStateFilePath(projectPath);
        if (!File.Exists(path))
            return new ProcessState();

        try
        {
            return JsonSerializer.Deserialize<ProcessState>(File.ReadAllText(path))
                   ?? new ProcessState();
        }
        catch (JsonException)
        {
            return new ProcessState();
        }
    }

    private static void Write(string projectPath, ProcessState state)
    {
        var path = GetStateFilePath(projectPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static void ValidateKind(string kind)
    {
        if (kind is not ("backend" or "frontend"))
            throw new ArgumentException("Process kind must be backend or frontend.", nameof(kind));
    }

    private static ProcessIdentity CreateIdentity(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return new ProcessIdentity
            {
                ProcessId = processId,
                StartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new ProcessIdentity { ProcessId = processId };
        }
    }

    private sealed class ProcessState
    {
        public Dictionary<string, ProcessIdentity> Processes { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ProcessIdentity
    {
        public int ProcessId { get; set; }
        public long? StartTimeUtcTicks { get; set; }
    }
}
