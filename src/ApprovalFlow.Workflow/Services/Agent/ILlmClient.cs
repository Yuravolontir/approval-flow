using ApprovalFlow.Shared.Models;

namespace ApprovalFlow.Workflow.Services.Agent;

public interface ILlmClient
{
    Task<AgentDecision> AnalyzeInvoiceAsync(InvoiceDto invoice, string policyText, CancellationToken ct = default);
}
