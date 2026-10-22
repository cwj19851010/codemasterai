using CodeMaster.LocalAgent.Services;

namespace CodeMaster.LocalAgent.Tests.Services;

public class LocalProjectProcessStoreTests
{
    [Fact]
    public void TrackAndRemove_PersistAcrossCalls()
    {
        var root = Path.Combine(
            CodeMasterTestPaths.Root,
            "CodeMaster_LocalProcessStoreTests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var currentProcessId = Environment.ProcessId;
            LocalProjectProcessStore.Track(root, "backend", currentProcessId);

            Assert.True(LocalProjectProcessStore.TryGet(root, "backend", out var processId));
            Assert.Equal(currentProcessId, processId);
            Assert.True(LocalProjectProcessStore.TryOpenOwnedProcess(root, "backend", currentProcessId, out var process));
            process!.Dispose();
            Assert.True(File.Exists(LocalProjectProcessStore.GetStateFilePath(root)));

            LocalProjectProcessStore.Remove(root, "backend");

            Assert.False(LocalProjectProcessStore.TryGet(root, "backend", out _));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TryOpenOwnedProcess_RejectsLegacyPidOnlyState()
    {
        var root = Path.Combine(
            CodeMasterTestPaths.Root,
            "CodeMaster_LocalProcessStoreTests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var statePath = LocalProjectProcessStore.GetStateFilePath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            File.WriteAllText(
                statePath,
                $"{{\"Processes\":{{\"backend\":{{\"ProcessId\":{Environment.ProcessId}}}}}}}}}");

            Assert.False(LocalProjectProcessStore.TryOpenOwnedProcess(
                root,
                "backend",
                Environment.ProcessId,
                out _));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
