namespace CodeMaster.Agent.PlatformConstraints;

/// <summary>
/// CodeMaster frontend stack conventions. Returned to the model so it does not
/// re-derive the framework choice every turn.
/// </summary>
public sealed record FrontendConvention
{
    public string Framework { get; init; } = "Vue 3";
    public string UiKit { get; init; } = "Element Plus";
    public string BuildTool { get; init; } = "Vite";
    public string Language { get; init; } = "JavaScript (NOT TypeScript)";
    public string ComponentStyle { get; init; } = "<script setup> with Composition API";
    public string StateManagement { get; init; } = "Pinia with pinia-plugin-persistedstate";
    public string HttpClient { get; init; } = "Axios with token injection via request.js interceptors";
    public string I18n { get; init; } = "vue-i18n, translations loaded from GET /api/lang/i18n-map/{langCode}";
    public string Realtime { get; init; } = "SignalR client (signalr.js) for notifications and online user tracking";
    public string DevProxy { get; init; } = "Vite dev server proxies /api to http://localhost:5000";
    public string DefaultDevCommand { get; init; } = "npm run dev";
    public string DefaultBuildCommand { get; init; } = "npm run build";
    public string ApiBaseDirectory { get; init; } = "src/api";
    public string ViewBaseDirectory { get; init; } = "src/views";
    public string StoreBaseDirectory { get; init; } = "src/stores";

    public string RenderForPrompt() =>
        $"""
        Frontend stack (must follow exactly):
        - Framework: {Framework}
        - UI Kit: {UiKit}
        - Build tool: {BuildTool}
        - Language: {Language}
        - Component style: {ComponentStyle}
        - State: {StateManagement}
        - HTTP: {HttpClient}
        - i18n: {I18n}
        - Realtime: {Realtime}
        - Dev proxy: {DevProxy}
        - Verification command: {DefaultDevCommand} (preferred over {DefaultBuildCommand} for fast feedback)
        - API functions go in {ApiBaseDirectory}; pages go in {ViewBaseDirectory}; Pinia stores in {StoreBaseDirectory}
        """;
}