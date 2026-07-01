namespace ApprovalFlow.Shared.Models;

public class AgentDecision
{
    public string Recommendation { get; set; } = string.Empty; // "approve", "reject", "escalate"
    public double Confidence { get; set; }
    public List<string> Violations { get; set; } = new();
    public string Reasoning { get; set; } = string.Empty;
}
