using System.Text;
using System.Text.Json;
using CodeMaster.Agent.Serialization;
using CodeMaster.LocalAgent.Models;

namespace CodeMaster.LocalAgent.Services;

public class StdioLocalAgentHost
{
    private static readonly JsonSerializerOptions ProtocolJsonOptions = new(AgentJsonSerializer.Options)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly LocalCodegenExecutionService _executor;
    private readonly LocalAgentDiagnosticLog _diagnostics;

    public StdioLocalAgentHost(
        LocalCodegenExecutionService executor,
        LocalAgentDiagnosticLog diagnostics)
    {
        _executor = executor;
        _diagnostics = diagnostics;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await _diagnostics.WriteAsync(
            "information",
            "stdio.started",
            new Dictionary<string, object?>
            {
                ["logDirectory"] = _diagnostics.LogDirectory
            });
        using var stdin = CreateProtocolReader(Console.OpenStandardInput());
        using var protocolWriter = CreateProtocolWriter(Console.OpenStandardOutput());

        // stdout is reserved for newline-delimited JSON protocol messages.
        Console.SetOut(TextWriter.Null);

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await stdin.ReadLineAsync(cancellationToken);
            if (line == null)
                break;

            line = line.TrimStart('\uFEFF');
            if (string.IsNullOrWhiteSpace(line))
                continue;

            using var progressWriteGate = new SemaphoreSlim(1, 1);
            var response = await ExecuteLineAsync(line, async progress =>
            {
                await progressWriteGate.WaitAsync(cancellationToken);
                try
                {
                    await protocolWriter.WriteLineAsync(SerializeProtocolProgress(progress));
                    await protocolWriter.FlushAsync();
                }
                finally
                {
                    progressWriteGate.Release();
                }
            });
            await protocolWriter.WriteLineAsync(SerializeProtocolResponse(response));
            await protocolWriter.FlushAsync();
        }

        await _diagnostics.WriteAsync("information", "stdio.stopped");
    }

    internal static StreamReader CreateProtocolReader(Stream stream) =>
        new(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);

    internal static StreamWriter CreateProtocolWriter(Stream stream) =>
        new(stream, new UTF8Encoding(false, true)) { AutoFlush = true };

    internal static string SerializeProtocolResponse(StdioLocalExecutionResponse response) =>
        JsonSerializer.Serialize(response, ProtocolJsonOptions);

    internal static string SerializeProtocolProgress(StdioLocalExecutionProgress progress) =>
        JsonSerializer.Serialize(progress, ProtocolJsonOptions);

    private async Task<StdioLocalExecutionResponse> ExecuteLineAsync(
        string line,
        Func<StdioLocalExecutionProgress, Task>? reportProgress = null)
    {
        string requestId = string.Empty;

        try
        {
            var request = JsonSerializer.Deserialize<StdioLocalExecutionRequest>(line, ProtocolJsonOptions)
                ?? throw new InvalidOperationException("Empty stdio request");

            requestId = request.Id;
            var result = await _executor.ExecuteAsync(
                request.Action,
                request,
                reportProgress == null
                    ? null
                    : progress => reportProgress(new StdioLocalExecutionProgress
                    {
                        Id = requestId,
                        Event = progress.Event,
                        Phase = progress.Phase,
                        Text = progress.Text,
                        ToolName = progress.ToolName
                    }));

            return new StdioLocalExecutionResponse
            {
                Id = requestId,
                Success = result.Success,
                Message = result.Message,
                Data = result.Data,
                Output = result.Output,
                DataOnly = result.DataOnly
            };
        }
        catch (Exception ex)
        {
            await _diagnostics.WriteAsync(
                "error",
                "stdio.request.failed",
                new Dictionary<string, object?>
                {
                    ["transportId"] = requestId
                },
                ex);
            return new StdioLocalExecutionResponse
            {
                Id = requestId,
                Success = false,
                Message = FormatExceptionMessage(ex),
                Output = ex.ToString()
            };
        }
    }

    private static string FormatExceptionMessage(Exception ex)
    {
        var baseException = ex.GetBaseException();
        var message = baseException.Message;

        if (string.IsNullOrWhiteSpace(message))
            message = ex.Message;

        if (string.IsNullOrWhiteSpace(message))
            message = baseException.GetType().FullName ?? ex.GetType().FullName ?? "Unknown local execution error";

        return message;
    }
}
