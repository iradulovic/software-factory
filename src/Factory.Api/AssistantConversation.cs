using Factory.Core;
using Factory.Infrastructure;
using Microsoft.Extensions.Options;

namespace Factory.Api;

public interface IAssistantConversation
{
    Task<OperatorReply> AnswerAsync(string question, IReadOnlyList<OperatorConversationMessage> history, CancellationToken ct);
}

public sealed class AssistantConversation : IAssistantConversation, IDisposable
{
    private readonly IAgentRunner agent;
    private readonly AssistantOptions options;
    private readonly IClock clock;
    private readonly SemaphoreSlim capacity;
    private readonly Queue<DateTimeOffset> accepted = new();
    private readonly object budgetLock = new();

    public AssistantConversation(IEnumerable<IAgentRunner> agents, IOptions<AssistantOptions> options, IClock clock)
    {
        this.options = options.Value;
        this.clock = clock;
        agent = agents.FirstOrDefault(candidate => string.Equals(candidate.Name, this.options.PreferredAgent, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Assistant agent '{this.options.PreferredAgent}' is not configured.");
        capacity = new SemaphoreSlim(Math.Max(1, this.options.MaxConcurrentConversations));
    }

    public async Task<OperatorReply> AnswerAsync(string question, IReadOnlyList<OperatorConversationMessage> history, CancellationToken ct)
    {
        if (!await capacity.WaitAsync(0, ct))
            throw new AssistantCapacityException("The assistant is already answering another conversation. Try again shortly.");

        try
        {
            if (!TryUseBudget(clock.UtcNow))
                throw new AssistantCapacityException("The assistant's separate hourly conversation budget is exhausted. Try again after the rolling hour resets.");
            var turns = history.Select(message => new AgentConversationTurn(message.Role, message.Content)).ToList();
            turns.Add(new AgentConversationTurn("user", question));
            var workingDirectory = string.IsNullOrWhiteSpace(options.WorkingDirectory)
                ? Directory.GetCurrentDirectory() : Path.GetFullPath(options.WorkingDirectory);
            AgentConversationResult result;
            try
            {
                result = await agent.ConverseAsync(new AgentConversationRequest(turns, workingDirectory,
                    options.TaskClass, TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds))), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new AssistantUnavailableException($"The {agent.Name} assistant CLI could not be started: {ex.Message}");
            }
            if (!result.Process.Succeeded || string.IsNullOrWhiteSpace(result.Response))
            {
                var detail = result.QuotaDetected ? "The conversational quota lane reached the provider limit."
                    : result.Process.TimedOut ? "The assistant CLI timed out."
                    : "The assistant CLI did not return a response.";
                throw new AssistantUnavailableException(detail);
            }

            // Conversation quota is intentionally local to this service. Do not call ITaskStore.RecordAgentQuotaStatusAsync:
            // task selection's durable provider quota lane must only reflect implementation/review invocations.
            return new OperatorReply(result.Response, null, null, [], null, clock.UtcNow, "assistant", agent.Name);
        }
        finally
        {
            capacity.Release();
        }
    }

    private bool TryUseBudget(DateTimeOffset now)
    {
        lock (budgetLock)
        {
            while (accepted.TryPeek(out var timestamp) && now - timestamp >= TimeSpan.FromHours(1)) accepted.Dequeue();
            if (options.MaxRequestsPerHour <= 0 || accepted.Count >= options.MaxRequestsPerHour) return false;
            accepted.Enqueue(now);
            return true;
        }
    }

    public void Dispose() => capacity.Dispose();
}

public sealed class OperatorAskRouter(IOperatorStateResponder deterministic, IAssistantConversation assistant)
{
    public Task<OperatorReply> AnswerAsync(string question, IReadOnlyList<OperatorConversationMessage> history, CancellationToken ct) =>
        OperatorQuestionParser.Parse(question).Intent is not null
            ? deterministic.AnswerAsync(question, ct)
            : assistant.AnswerAsync(question, history, ct);
}

public sealed class AssistantCapacityException(string message) : Exception(message);
public sealed class AssistantUnavailableException(string message) : Exception(message);
