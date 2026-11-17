using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace CodeMaster.Agent.Services;

public static class AiResponseCompletion
{
    public const int MaximumAttempts = 2;
    public const string IncompleteMessage = "本次需求整理未完成：模型只返回了准备说明，没有给出需求正文。";

    public static bool IsRequirementsRequest(string? content)
    {
        if (!AiProjectPlanTurn.IsDiscussionOnly(content))
            return false;
        return Regex.IsMatch(content ?? string.Empty,
            @"(整理|梳理|分析|明确|细化|完善|总结|补充|列出|列一下|列下).{0,16}需求|需求.{0,10}(分析|梳理|整理|文档|清单)");
    }

    public static string BuildRequirementsInstructions(string projectName, long projectId) => $$"""
        You are the CodeMaster requirements analyst for project {{projectName}} (ProjectId {{projectId}}).
        The user requests requirements analysis only. Deliver the actual requirements document in the user's language in this turn.
        You may read the project summary to distinguish existing functionality from proposed scope. Only read tools are available.
        A Markdown or plain-text document the user attached to this turn is inlined below their message inside <document> blocks, and documents already inside the project folder can be listed with list_project_files and read with read_project_file. Ground the requirements in what those documents actually say; when a document and the conversation disagree, follow the document and state the assumption. Never invent scope a document does not contain.
        Do not create plans, submit changes, generate code, request implementation approval, or promise to answer in a later turn.
        Once the relevant context is available, write the requirements directly: modules, core records/fields, business flows,
        permissions, acceptance criteria, and assumptions or unresolved business decisions. Scale detail to the request.
        Do not expand optional suggestions into confirmed scope. Do not claim metadata exists without tool evidence.
        If metadata is unavailable, state that limitation briefly and still organize the user's supplied requirements.
        An opening progress sentence is not a final answer. End only after delivering substantive requirements or a precise blocking question.
        """;

    public static bool IsPreambleOnly(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        // Restrict recovery to short prospective narration. A real list, section,
        // concrete answer, or clarification must not be discarded for being brief.
        if (text.Length > 700 || Regex.IsMatch(text, @"(?m)^\s*(#{1,6}\s|[-*]\s|\d+[.、]\s*|\|)")) return false;
        var sentences = Regex.Split(text.Trim(), @"[。！？!?\r\n]+")
            .Select(item => item.Trim()).Where(item => item.Length > 0).ToList();
        return sentences.Count > 0 && sentences.All(sentence => Regex.IsMatch(sentence,
            @"^(好的[，,：:]?\s*)?(我会|我将|我先|我现在|接下来我|接下来将|接下来会|首先我|随后我|随后将|随后会|稍后我|正在(读取|核对|检查|整理|分析)|让我先|I will\b|I'll\b|Let me\b)",
            RegexOptions.IgnoreCase));
    }

    public static string GetFinalText(AiModelReply? reply, string fallback = "") =>
        reply == null ? fallback : reply.Completed && reply.ToolCalls.Length == 0 ? reply.Text : string.Empty;

    public static async Task<AiResponseCompletionResult> EnsureAsync(
        string originalRequest,
        bool requirementsOnly,
        Func<AiModelReply?> getLastReply,
        string fallbackText,
        Func<string, CancellationToken, Task> requestFinalAnswer,
        Func<CancellationToken, Task>? reportContinuation = null,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reply = getLastReply();
            var text = GetFinalText(reply, fallbackText);
            var missing = string.IsNullOrWhiteSpace(text) || requirementsOnly && IsPreambleOnly(text);
            var truncated = string.Equals(reply?.FinishReason, ChatFinishReason.Length.ToString(), StringComparison.OrdinalIgnoreCase);
            if (!missing && !truncated)
                return new(text, attempt, true);
            if (attempt >= MaximumAttempts)
                return new(requirementsOnly ? IncompleteMessage : "模型未返回完整的处理结果，请查看任务实际执行状态。", attempt, false);
            if (reportContinuation != null)
                await reportContinuation(cancellationToken);

            var prompt = requirementsOnly
                ? "The previous output did not complete the requested requirements document (it was empty, truncated, or only opening narration). " +
                  "Write the actual requirements now using the user request and any read results already in this session. " +
                  "Include the business modules, main records, flows, permissions, acceptance criteria, and assumptions. " +
                  "If project facts are unavailable, distinguish proposals from existing facts. Do not request more tools or begin implementation. " +
                  "Do not announce what you will do; deliver the document itself. Keep it within 2500 Chinese characters."
                : "Return the final user-facing answer using the tool results already in this session. " +
                  "Do not call tools, expose hidden reasoning, or invent results. Summarize actual outcomes and blockers, not future promises.";
            await requestFinalAnswer(prompt + "\nOriginal user request:\n" + originalRequest, cancellationToken);
        }
    }
}

public sealed record AiResponseCompletionResult(string Text, int Attempts, bool Complete);
