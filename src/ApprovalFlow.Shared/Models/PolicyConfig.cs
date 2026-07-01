namespace ApprovalFlow.Shared.Models;

public class PolicyConfig
{
    public decimal AutonomyCeiling { get; set; } = 250m;
    public double AutonomyConfidence { get; set; } = 0.80;
    public decimal ReceiptThreshold { get; set; } = 25m;
    public decimal SaasMonthlyCap { get; set; } = 200m;
    public decimal HardwareCapitalThreshold { get; set; } = 1000m;
    public decimal TravelSingleExpenseThreshold { get; set; } = 1500m;
    public decimal ClientEntertainmentThreshold { get; set; } = 500m;
    public decimal FxHardStopThreshold { get; set; } = 1000m;
    public decimal MealPerAttendeeLimit { get; set; } = 75m;
}
